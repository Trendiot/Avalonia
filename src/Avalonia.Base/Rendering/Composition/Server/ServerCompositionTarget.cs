using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Collections.Pooled;
using Avalonia.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Utilities;

namespace Avalonia.Rendering.Composition.Server
{
    /// <summary>
    /// Server-side counterpart of the <see cref="CompositionTarget"/>
    /// That's the place where we update visual transforms, track dirty rects and actually do rendering
    /// </summary>
    internal partial class ServerCompositionTarget : IDisposable
    {
        private readonly ServerCompositor _compositor;
        private readonly Func<IEnumerable<IPlatformRenderSurface>> _surfaces;
        private CompositionTargetOverlays _overlays;
        private static long s_nextId = 1;
        private IRenderTarget? _renderTarget;
        private PixelSize _layerSize;
        private IDrawingContextLayerImpl? _layer;
        private bool _updateRequested;
        private bool _redrawRequested;
        private bool _fullRedrawRequested;
        private bool _disposed;
        private readonly HashSet<ServerCompositionVisual> _attachedVisuals = new();
        public IDirtyRectTracker DirtyRects { get; }

        public long Id { get; }
        public ulong Revision { get; private set; }
        public ICompositionTargetDebugEvents? DebugEvents { get; set; }
        public int RenderedVisuals { get; set; }
        public int VisitedVisuals { get; set; }
        
        /// <summary>
        /// Returns true if the target is enabled and has pending work but its render target was not ready.
        /// </summary>
        internal bool IsWaitingForReadyRenderTarget { get; private set; }

        public ServerCompositionTarget(ServerCompositor compositor, Func<IEnumerable<IPlatformRenderSurface>> surfaces)
            : base(compositor)
        {
            _compositor = compositor;
            _surfaces = surfaces;
            _overlays = new CompositionTargetOverlays(this);
            var platformRender = AvaloniaLocator.Current.GetService<IPlatformRenderInterface>();

            if (platformRender?.SupportsRegions == true && compositor.Options.UseRegionDirtyRectClipping != false)
            {
                var maxRects = compositor.Options.MaxDirtyRects ?? 8;
                DirtyRects = maxRects <= 0
                    ? new RegionDirtyRectTracker(platformRender)
                    : new MultiDirtyRectTracker(platformRender, maxRects,
                        // WPF uses 50K, but that merges stuff rather aggressively 
                        compositor.Options.DirtyRectMergeEagerness ?? 1000); 
            }

            DirtyRects ??= new SingleDirtyRectTracker();
            
            Id = Interlocked.Increment(ref s_nextId);
        }
        
        partial void OnIsEnabledChanged()
        {
            if (IsEnabled)
            {
                _compositor.AddCompositionTarget(this);
                foreach (var v in _attachedVisuals)
                    v.Activate();
            }
            else
            {
                _compositor.RemoveCompositionTarget(this);
                foreach (var v in _attachedVisuals)
                    v.Deactivate();
            }
        }

        partial void OnDebugOverlaysChanged()
        {
            _fullRedrawRequested = true;
            _overlays.OnChanged(DebugOverlays);
        }

        partial void OnLastLayoutPassTimingChanged() => _overlays.OnLastLayoutPassTimingChanged(LastLayoutPassTiming);

        partial void DeserializeChangesExtra(BatchStreamReader c)
        {
            _redrawRequested = true;
            _fullRedrawRequested = true;
        }
        
        
        public void Update(TimeSpan diagnosticsCompositorGlobalUpdateElapsedTime = default)
        {
            if (_disposed)
            {
                Compositor.RemoveCompositionTarget(this);
                return;
            }

            if (Root == null)
                return;
            
            _overlays.RecordGlobalCompositorUpdateTime(diagnosticsCompositorGlobalUpdateElapsedTime);
            _overlays.MarkUpdateCallStart();
            using (Diagnostic.BeginCompositorUpdatePass())
            {
                var transform = Matrix.CreateScale(Scaling, Scaling);

                var collector = DebugEvents != null
                    ? new DebugEventsDirtyRectCollectorProxy(DirtyRects, DebugEvents)
                    : (IDirtyRectCollector)DirtyRects;
                
                Root.UpdateRoot(collector, transform, new LtrbRect(0, 0, PixelSize.Width, PixelSize.Height));

                _updateRequested = false;

                _overlays.MarkUpdateCallEnd();
            }
        }

        // ───────── DIAGNOSTIC INSTRUMENTATION (PhotonPlot lag investigation) ─────────
        //
        // PhotonPlot internal counter ticks at 60 fps on Iris Xe but DXGI sees only ~50
        // distinct DWM updates. The hypothesis: this Render() method early-returns 14-18%
        // of the time, swallowing the present that should have made it to DWM.
        //
        // Counters below sample once per second to STDOUT. They tell us:
        //   1. Whether the early-return actually fires at the rate we expect
        //   2. Which of the two early-return paths fires (line "all empty" vs "redraw
        //      false after the |=")
        //   3. At early-return time, how many SurfaceVisuals in the tree had
        //      _isDirtyForRender still true (means UpdateRoot did NOT visit them — the
        //      propagation slip we're hunting) or null _ownContentBounds (means the
        //      bitmap snapshot returned no usable size).
        //
        // Cost: a few interlocked increments per tick + one tree walk per early-return.
        // Negligible compared to the render itself.
        private static long s_diagRenderCalls;
        private static long s_diagEarlyAllEmpty;       // line "DirtyRects empty && !_redrawRequested && !_updateRequested"
        private static long s_diagEarlyRedrawFalse;    // line "DirtyRects empty after |= and !_redrawRequested"
        private static long s_diagSurfaceVisualsTotal;
        private static long s_diagSurfaceVisualsStillDirty;
        private static long s_diagSurfaceVisualsNullBounds;
        private static long s_diagLastLogTicks;
        private const long DiagLogIntervalTicks = 10_000_000L; // 1 second in 100ns ticks

        private void DiagInspectVisualsOnEarlyReturn()
        {
            if (Root == null)
                return;
            DiagWalkVisualTree(Root);
        }

        private static void DiagWalkVisualTree(ServerCompositionVisual node)
        {
            if (node is ServerCompositionSurfaceVisual)
            {
                Interlocked.Increment(ref s_diagSurfaceVisualsTotal);
                if (node.DiagnosticIsDirtyForRender)
                    Interlocked.Increment(ref s_diagSurfaceVisualsStillDirty);
                if (node.DiagnosticOwnContentBounds == null)
                    Interlocked.Increment(ref s_diagSurfaceVisualsNullBounds);
            }
            if (node is ServerCompositionContainerVisual container)
            {
                foreach (var child in container.Children)
                    DiagWalkVisualTree(child);
            }
        }

        private static void DiagMaybeLog()
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref s_diagLastLogTicks);
            if (now - last < DiagLogIntervalTicks)
                return;
            // CAS so only one thread wins the log race per interval.
            if (Interlocked.CompareExchange(ref s_diagLastLogTicks, now, last) != last)
                return;

            // Snapshot (and reset) all counters for this window.
            long calls       = Interlocked.Exchange(ref s_diagRenderCalls,             0);
            long allEmpty    = Interlocked.Exchange(ref s_diagEarlyAllEmpty,           0);
            long redrawFalse = Interlocked.Exchange(ref s_diagEarlyRedrawFalse,        0);
            long visuals     = Interlocked.Exchange(ref s_diagSurfaceVisualsTotal,     0);
            long stillDirty  = Interlocked.Exchange(ref s_diagSurfaceVisualsStillDirty,0);
            long nullBounds  = Interlocked.Exchange(ref s_diagSurfaceVisualsNullBounds,0);

            long earlyTotal = allEmpty + redrawFalse;
            double earlyPct = calls > 0 ? earlyTotal * 100.0 / calls : 0;
            Console.WriteLine(
                $"[ServerCompositionTarget DIAG] render={calls} early={earlyTotal} ({earlyPct:F1}%) " +
                $"[allEmpty={allEmpty} redrawFalse={redrawFalse}] " +
                $"surfaceVisualsAtEarly={visuals} stillDirty={stillDirty} nullBounds={nullBounds}");
        }
        // ──────────────────────────────────────────────────────────────────────────────

        public void Render()
        {
            Interlocked.Increment(ref s_diagRenderCalls);
            IsWaitingForReadyRenderTarget = false;

            if (_disposed)
                return;

            if (Root == null)
                return;

            if (_renderTarget?.PlatformRenderTargetState.IsCorrupted == true)
            {
                _layer?.Dispose();
                _layer = null;
                _renderTarget.Dispose();
                _renderTarget = null;
                _redrawRequested = true;
            }

            try
            {
                if (_renderTarget == null)
                {
                    if (!_compositor.IsReadyToCreateRenderTarget(_surfaces()))
                    {
                        IsWaitingForReadyRenderTarget = IsEnabled;
                        return;
                    }

                    _renderTarget = _compositor.CreateRenderTarget(_surfaces());
                }
            }
            catch (RenderTargetNotReadyException)
            {
                IsWaitingForReadyRenderTarget = IsEnabled;
                return;
            }
            catch (RenderTargetCorruptedException)
            {
                return;
            }

            if (DirtyRects.IsEmpty && !_redrawRequested && !_updateRequested)
            {
                Interlocked.Increment(ref s_diagEarlyAllEmpty);
                DiagInspectVisualsOnEarlyReturn();
                DiagMaybeLog();
                return;
            }

            _redrawRequested |= !DirtyRects.IsEmpty;

            if (!_redrawRequested)
            {
                Interlocked.Increment(ref s_diagEarlyRedrawFalse);
                DiagInspectVisualsOnEarlyReturn();
                DiagMaybeLog();
                return;
            }

            DiagMaybeLog();
            
            if (!_renderTarget.PlatformRenderTargetState.IsReady)
            {
                IsWaitingForReadyRenderTarget = IsEnabled;
                return;
            }

            var needLayer = _overlays.RequireLayer // Check if we don't need overlays
                            // Check if render target can be rendered to directly and preserves the previous frame
                            || !(_renderTarget.Properties.RetainsPreviousFrameContents
                                 && _renderTarget.Properties.IsSuitableForDirectRendering);
            
            using (var renderTargetContext = _renderTarget.CreateDrawingContext(new(PixelSize, Scaling), out var properties))
            using (var renderTiming = Diagnostic.BeginCompositorRenderPass())
            {
                var fullRedraw = false;
                
                if(needLayer && (PixelSize != _layerSize || _layer == null || _layer.IsCorrupted))
                {
                    _layer?.Dispose();
                    _layer = null;
                    _layer = renderTargetContext.CreateLayer(PixelSize);
                    _layerSize = PixelSize;
                    fullRedraw = true;
                }
                else if (!needLayer)
                {
                    _layer?.Dispose();
                    _layer = null;
                }

                if (_fullRedrawRequested || (!needLayer && !properties.PreviousFrameIsRetained))
                {
                    _fullRedrawRequested = false;
                    fullRedraw = true;
                }

                var renderBounds = new LtrbRect(0, 0, PixelSize.Width, PixelSize.Height);
                if (fullRedraw)
                {
                    DirtyRects.Initialize(renderBounds);
                    DirtyRects.AddRect(renderBounds);
                }

                if (!DirtyRects.IsEmpty)
                {
                    DirtyRects.FinalizeFrame(renderBounds);
                    if (_layer != null)
                    {
                        using (var context = _layer.CreateDrawingContext())
                            RenderRootToContextWithClip(context, Root);

                        renderTargetContext.Clear(Colors.Transparent);
                        renderTargetContext.Transform = Matrix.Identity;
                        if (_layer.CanBlit)
                            _layer.Blit(renderTargetContext);
                        else
                        {
                            var rect = new PixelRect(default, PixelSize).ToRect(1);
                            renderTargetContext.DrawBitmap(_layer, 1, rect, rect);
                        }
                        _overlays.Draw(renderTargetContext, true);
                    }
                    else
                    {
                        RenderRootToContextWithClip(renderTargetContext, Root);
                        _overlays.Draw(renderTargetContext, false);
                    }
                }

                RenderedVisuals = 0;
                VisitedVisuals = 0;

                _redrawRequested = false;
                DirtyRects.Initialize(renderBounds);
            }
        }

        void RenderRootToContextWithClip(IDrawingContextImpl context, ServerCompositionVisual root)
        {
            var useLayerClip = Compositor.Options.UseSaveLayerRootClip ?? false;
            
            using (DirtyRects.BeginDraw(context))
            {
                context.Clear(Colors.Transparent);
                if (useLayerClip)
                    context.PushLayer(DirtyRects.CombinedRect.ToRect());

                context.Transform = Matrix.CreateScale(Scaling, Scaling);
                (VisitedVisuals, RenderedVisuals) = root.Render(context, new LtrbRect(0,0, PixelSize.Width, PixelSize.Height), DirtyRects);
                if (DebugEvents != null)
                {
                    DebugEvents.RenderedVisuals = RenderedVisuals;
                    DebugEvents.VisitedVisuals = VisitedVisuals;
                }

                if (useLayerClip)
                    context.PopLayer();
            }
        }
        
        public void RequestUpdate() => _updateRequested = true;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            using (_compositor.RenderInterface.EnsureCurrent())
            {
                if (_layer != null)
                {
                    _layer.Dispose();
                    _layer = null;
                }

                _renderTarget?.Dispose();
                _renderTarget = null;
            }
            _compositor.RemoveCompositionTarget(this);
        }

        public void AddVisual(ServerCompositionVisual visual)
        {
            if (_attachedVisuals.Add(visual) && IsEnabled)
                visual.Activate();
        }

        public void RemoveVisual(ServerCompositionVisual visual)
        {
            if (_attachedVisuals.Remove(visual) && IsEnabled)
                visual.Deactivate();
        }
    }
}
