using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Rendering.Composition.Expressions;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Threading;

namespace Avalonia.Rendering.Composition.Server
{
    /// <summary>
    /// Server-side counterpart of the <see cref="Compositor"/>.
    /// 1) manages deserialization of changes received from the UI thread
    /// 2) triggers animation ticks
    /// 3) asks composition targets to render themselves
    /// </summary>
    internal partial class ServerCompositor : IRenderLoopTask
    {
        private readonly IRenderLoop _renderLoop;

        private readonly Queue<CompositionBatch> _batches = new Queue<CompositionBatch>();
        private readonly Queue<Action> _receivedJobQueue = new();
        private readonly Queue<Action> _receivedPostTargetJobQueue = new();
        public long LastBatchId { get; private set; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public TimeSpan ServerNow { get; private set; }
        private readonly List<ServerCompositionTarget> _activeTargets = new();
        internal BatchStreamObjectPool<object?> BatchObjectPool;
        internal BatchStreamMemoryPool BatchMemoryPool;
        public CompositorPools Pools { get; } = new();
        private readonly object _lock = new object();
        private Thread? _safeThread;
        private bool _uiThreadIsInsideRender;
        public PlatformRenderInterfaceContextManager RenderInterface { get; }
        internal static readonly object RenderThreadDisposeStartMarker = new();
        internal static readonly object RenderThreadJobsStartMarker = new();
        internal static readonly object RenderThreadJobsEndMarker = new();
        internal static readonly object RenderThreadPostTargetJobsStartMarker = new();
        internal static readonly object RenderThreadPostTargetJobsEndMarker = new();
        public CompositionOptions Options { get; }
        public ServerCompositorAnimations Animations { get; }
        public ReadbackIndices Readback { get; } = new();
        
        private int _ticksSinceLastCommit;
        private const int CommitGraceTicks = 10;

        // --- ServerCompositor render-phase diagnostic --------------------------------------
        // Splits each RenderCore call into:
        //   lock      : time spent acquiring _lock in RenderReentrancySafe
        //   global    : ExecuteGlobalPasses (animations + property update + adorner)
        //   srvJobs1  : ExecuteServerJobs(_receivedJobQueue)
        //   targets   : sum of t.Update + t.Render across all _activeTargets
        //               (this is where the chain reaches the platform render target,
        //                which on Vulkan calls into VulkanKhrRenderTarget.BeginDraw → ...)
        //   readback  : VisualReadbackUpdatePass
        //   srvJobs2  : ExecuteServerJobs(_receivedPostTargetJobQueue)
        // Logs only the worst N renders per 1s window.
        private const double DiagJitterThresholdMs = 9.0;
        private const int    DiagWorstPerWindow    = 5;
        private long _diagFrameCount;
        private long _diagJitterCount;
        private long _diagLastReportTs;
        // Per-call sub-phase timings, set by RenderReentrancySafe / RenderCore and
        // consumed by DiagReportRender at the end of the same call.
        private double _diagLockMs;
        private double _diagGlobalMs;
        private double _diagSrvJobs1Ms;
        private double _diagTargetsMs;
        private double _diagReadbackMs;
        private double _diagSrvJobs2Ms;
        private int    _diagTargetCount;
        private readonly double[] _diagWorstInterval = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstLock     = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstGlobal   = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstSrvJobs1 = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstTargets  = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstReadback = new double[DiagWorstPerWindow];
        private readonly double[] _diagWorstSrvJobs2 = new double[DiagWorstPerWindow];
        private readonly int   [] _diagWorstTargetCt = new int   [DiagWorstPerWindow];
        private readonly long  [] _diagWorstFrameNo  = new long  [DiagWorstPerWindow];

        public ServerCompositor(IRenderLoop renderLoop, IPlatformGraphics? platformGraphics,
            CompositionOptions options,
            BatchStreamObjectPool<object?> batchObjectPool, BatchStreamMemoryPool batchMemoryPool)
        {
            Options = options;
            Animations = new();
            _renderLoop = renderLoop;
            RenderInterface = new PlatformRenderInterfaceContextManager(platformGraphics);
            RenderInterface.ContextDisposed += RT_OnContextDisposed;
            RenderInterface.ContextCreated += RT_OnContextCreated;
            BatchObjectPool = batchObjectPool;
            BatchMemoryPool = batchMemoryPool;
            _renderLoop.Add(this);
        }

        public void EnqueueBatch(CompositionBatch batch)
        {
            lock (_batches) 
                _batches.Enqueue(batch);
            _renderLoop.Wakeup();
        }

        internal void UpdateServerTime() => ServerNow = Clock.Elapsed;

        readonly List<CompositionBatch> _reusableToNotifyProcessedList = new();
        readonly List<CompositionBatch> _reusableToNotifyRenderedList = new();
        void ApplyPendingBatches()
        {
            bool hadBatches = false;
            while (true)
            {
                CompositionBatch batch;
                lock (_batches)
                {
                    if(_batches.Count == 0)
                        break;
                    batch = _batches.Dequeue();
                }

                using (var stream = new BatchStreamReader(batch.Changes, BatchMemoryPool, BatchObjectPool))
                {
                    while (!stream.IsObjectEof)
                    {
                        var readObject = stream.ReadObject();
                        if (readObject == RenderThreadJobsStartMarker)
                        {
                            ReadServerJobs(stream, _receivedJobQueue, RenderThreadJobsEndMarker);
                            continue;
                        }
                        if (readObject == RenderThreadPostTargetJobsStartMarker)
                        {
                            ReadServerJobs(stream, _receivedPostTargetJobQueue, RenderThreadPostTargetJobsEndMarker);
                            continue;
                        }

                        if (readObject == RenderThreadDisposeStartMarker)
                        {
                            ReadDisposeJobs(stream);
                            continue;
                        }
                        
                        var target = (SimpleServerObject)readObject!;
                        target.DeserializeChanges(stream, batch);
#if DEBUG_COMPOSITOR_SERIALIZATION
                        if (stream.ReadObject() != BatchStreamDebugMarkers.ObjectEndMarker)
                            throw new InvalidOperationException(
                                $"Object {target.GetType()} failed to deserialize properly on object stream");
                        if(stream.Read<Guid>() != BatchStreamDebugMarkers.ObjectEndMagic)
                            throw new InvalidOperationException(
                                $"Object {target.GetType()} failed to deserialize properly on data stream");
#endif
                    }
                }

                _reusableToNotifyProcessedList.Add(batch);
                LastBatchId = batch.SequenceId;
                hadBatches = true;
            }
            
            if (hadBatches)
                _ticksSinceLastCommit = 0;
            else if (_ticksSinceLastCommit < int.MaxValue)
                _ticksSinceLastCommit++;
        }

        void ReadServerJobs(BatchStreamReader reader, Queue<Action> queue, object endMarker)
        {
            object? readObject;
            while ((readObject = reader.ReadObject()) != endMarker)
                queue.Enqueue((Action)readObject!);
        }

        void ReadDisposeJobs(BatchStreamReader reader)
        {
            var count = reader.Read<int>();
            while (count > 0)
            {
                (reader.ReadObject() as IDisposable)?.Dispose();
                count--;
            }
        }

        void ExecuteServerJobs(Queue<Action> queue)
        {
            while(queue.Count > 0)
                try
                {
                    queue.Dequeue()();
                }
                catch
                {
                    // Ignore
                }
        }

        void NotifyBatchesProcessed()
        {
            foreach (var batch in _reusableToNotifyProcessedList) 
                batch.NotifyProcessed();

            foreach (var batch in _reusableToNotifyProcessedList)
                _reusableToNotifyRenderedList.Add(batch);

            _reusableToNotifyProcessedList.Clear();
        }
        
        void NotifyBatchesRendered()
        {
            foreach (var batch in _reusableToNotifyRenderedList) 
                batch.NotifyRendered();

            _reusableToNotifyRenderedList.Clear();
        }

        bool IRenderLoopTask.Render() => ExecuteRender(true);
        public void Render(bool catchExceptions) => ExecuteRender(catchExceptions);
        
        private bool ExecuteRender(bool catchExceptions)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (_uiThreadIsInsideRender)
                    throw new InvalidOperationException("Reentrancy is not supported");
                _uiThreadIsInsideRender = true;
                try
                {
                    using (Dispatcher.UIThread.DisableProcessing()) 
                        return RenderReentrancySafe(catchExceptions);
                }
                finally
                {
                    _uiThreadIsInsideRender = false;
                }
            }
            else
                return RenderReentrancySafe(catchExceptions);
        }
        
        private bool RenderReentrancySafe(bool catchExceptions)
        {
            long lockEnterTs = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                _diagLockMs = (Stopwatch.GetTimestamp() - lockEnterTs)
                              * (1000.0 / Stopwatch.Frequency);
                try
                {
                    try
                    {
                        _safeThread = Thread.CurrentThread;
                        return RenderCore(catchExceptions);
                    }
                    finally
                    {
                        NotifyBatchesRendered();
                    }
                }
                finally
                {
                    _safeThread = null;
                }
            }
        }

        private TimeSpan ExecuteGlobalPasses()
        {
            var compositorGlobalPassesStarted = Stopwatch.GetTimestamp();
            ApplyPendingBatches();
            NotifyBatchesProcessed();

            Animations.Process();

            ApplyEnqueuedRenderResourceChangesPass();
            
            VisualOwnPropertiesUpdatePass();
            
            // Adorners need to be updated after own properties recompute pass,
            // because they may depend on ancestor's transform chain to be consistent
            AdornerUpdatePass();

            return Stopwatch.GetElapsedTime(compositorGlobalPassesStarted);
        }
        
        private bool RenderCore(bool catchExceptions)
        {
            UpdateServerTime();

            double tickToMs = 1000.0 / Stopwatch.Frequency;
            long tGlobal0 = Stopwatch.GetTimestamp();
            var compositorGlobalPassesElapsed = ExecuteGlobalPasses();
            long tGlobal1 = Stopwatch.GetTimestamp();
            _diagGlobalMs = (tGlobal1 - tGlobal0) * tickToMs;
            _diagSrvJobs1Ms = 0; _diagTargetsMs = 0; _diagReadbackMs = 0; _diagSrvJobs2Ms = 0;
            _diagTargetCount = 0;

            try
            {
                if (!RenderInterface.IsReady)
                {
                    DiagReportRender();
                    return true;
                }
                RenderInterface.EnsureValidBackendContext();

                long tJobs1_0 = Stopwatch.GetTimestamp();
                ExecuteServerJobs(_receivedJobQueue);
                // Server jobs (e.g. CompositionDrawingSurface.UpdateWithSemaphoresAsync)
                // can synchronously fire Surface.Changed -> InvalidateContent on visuals,
                // which enqueues them into _visualOwnPropertiesRecomputePass. Without this
                // second pass, that enqueueing slips to the NEXT frame's global pass,
                // causing one PhotonPlot/CompositionDrawingSurface update to produce TWO
                // target renders/presents (one for the batch's _redrawRequested, one for
                // the deferred DirtyRects). Re-running here is idempotent: the queue is
                // empty when no jobs1 invalidations occurred (steady state for apps not
                // using GpuInterop), and RecomputeOwnProperties clears its own dirty flags
                // so a visual processed twice is a no-op the second time.
                VisualOwnPropertiesUpdatePass();
                long tJobs1_1 = Stopwatch.GetTimestamp();
                _diagSrvJobs1Ms = (tJobs1_1 - tJobs1_0) * tickToMs;

                long tTargets0 = Stopwatch.GetTimestamp();
                foreach (var t in _activeTargets)
                {
                    t.Update(compositorGlobalPassesElapsed);
                    t.Render();
                    _diagTargetCount++;
                }
                long tTargets1 = Stopwatch.GetTimestamp();
                _diagTargetsMs = (tTargets1 - tTargets0) * tickToMs;

                long tReadback0 = Stopwatch.GetTimestamp();
                VisualReadbackUpdatePass();
                long tReadback1 = Stopwatch.GetTimestamp();
                _diagReadbackMs = (tReadback1 - tReadback0) * tickToMs;

                long tJobs2_0 = Stopwatch.GetTimestamp();
                ExecuteServerJobs(_receivedPostTargetJobQueue);
                long tJobs2_1 = Stopwatch.GetTimestamp();
                _diagSrvJobs2Ms = (tJobs2_1 - tJobs2_0) * tickToMs;
            }
            catch (Exception e) when(RT_OnContextLostExceptionFilterObserver(e) && catchExceptions)
            {
                Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this, "Exception when rendering: {Error}", e);
            }

            DiagReportRender();
            
            // Request a tick if we have active animations or if there are recent batches
            if (Animations.NeedNextTick || _ticksSinceLastCommit < CommitGraceTicks)
                return true;
            
            // Request a tick if we had unready targets in the last tick, to check if they are ready next time
            foreach (var target in _activeTargets)
                if (target.IsWaitingForReadyRenderTarget)
                    return true;
            
            // Otherwise there is no need to waste CPU cycles, tell the timer to pause
            return false;
        }

        private void DiagReportRender()
        {
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            _diagFrameCount++;
            long now = Stopwatch.GetTimestamp();
            if (_diagLastReportTs == 0) { _diagLastReportTs = now; return; }

            // Total interval = sum of measured phases (lock + global + jobs1 + targets +
            // readback + jobs2). We report this rather than wall-clock so the breakdown
            // sums to the reported total.
            double intervalMs = _diagLockMs + _diagGlobalMs + _diagSrvJobs1Ms + _diagTargetsMs
                              + _diagReadbackMs + _diagSrvJobs2Ms;

            if (intervalMs > DiagJitterThresholdMs)
            {
                _diagJitterCount++;
                int worstIdx = 0;
                for (int i = 1; i < DiagWorstPerWindow; i++)
                    if (_diagWorstInterval[i] < _diagWorstInterval[worstIdx]) worstIdx = i;
                if (intervalMs > _diagWorstInterval[worstIdx])
                {
                    _diagWorstInterval[worstIdx] = intervalMs;
                    _diagWorstLock    [worstIdx] = _diagLockMs;
                    _diagWorstGlobal  [worstIdx] = _diagGlobalMs;
                    _diagWorstSrvJobs1[worstIdx] = _diagSrvJobs1Ms;
                    _diagWorstTargets [worstIdx] = _diagTargetsMs;
                    _diagWorstReadback[worstIdx] = _diagReadbackMs;
                    _diagWorstSrvJobs2[worstIdx] = _diagSrvJobs2Ms;
                    _diagWorstTargetCt[worstIdx] = _diagTargetCount;
                    _diagWorstFrameNo [worstIdx] = _diagFrameCount;
                }
            }

            double sinceReportMs = (now - _diagLastReportTs) * tickToMs;
            if (sinceReportMs >= 1000.0)
            {
                Console.Error.WriteLine(
                    $"[SC] 1s: renders={_diagFrameCount} jitter={_diagJitterCount} "
                    + $"avgFps={(_diagFrameCount * 1000.0 / sinceReportMs):F1}");
                for (int i = 0; i < DiagWorstPerWindow; i++)
                {
                    if (_diagWorstInterval[i] <= 0) continue;
                    Console.Error.WriteLine(
                        $"  worst#{i} f={_diagWorstFrameNo[i]} total={_diagWorstInterval[i]:F2}ms "
                        + $"lock={_diagWorstLock[i]:F2} global={_diagWorstGlobal[i]:F2} "
                        + $"jobs1={_diagWorstSrvJobs1[i]:F2} targets={_diagWorstTargets[i]:F2} "
                        + $"readback={_diagWorstReadback[i]:F2} jobs2={_diagWorstSrvJobs2[i]:F2} "
                        + $"tgts={_diagWorstTargetCt[i]}");
                    _diagWorstInterval[i] = 0; _diagWorstLock[i] = 0;
                    _diagWorstGlobal[i] = 0; _diagWorstSrvJobs1[i] = 0;
                    _diagWorstTargets[i] = 0; _diagWorstReadback[i] = 0;
                    _diagWorstSrvJobs2[i] = 0; _diagWorstTargetCt[i] = 0;
                    _diagWorstFrameNo[i] = 0;
                }
                _diagFrameCount = 0; _diagJitterCount = 0; _diagLastReportTs = now;
            }
        }

        public void AddCompositionTarget(ServerCompositionTarget target)
        {
            _activeTargets.Add(target);
        }

        public void RemoveCompositionTarget(ServerCompositionTarget target)
        {
            _activeTargets.Remove(target);
        }
        
        public IRenderTarget CreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces)
        {
            using (RenderInterface.EnsureCurrent())
                return RenderInterface.CreateRenderTarget(surfaces);
        }

        public bool IsReadyToCreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces)
        {
            return RenderInterface.IsReadyToCreateRenderTarget(surfaces);
        }

        public bool CheckAccess() => _safeThread == Thread.CurrentThread;
        public void VerifyAccess()
        {
            if (!CheckAccess())
                throw new InvalidOperationException("This object can be only accessed under compositor lock");
        }
    }
}
