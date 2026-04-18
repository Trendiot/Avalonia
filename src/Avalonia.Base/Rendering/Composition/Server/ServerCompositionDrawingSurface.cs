using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Avalonia.Platform;
using Avalonia.Utilities;

namespace Avalonia.Rendering.Composition.Server;

internal class ServerCompositionDrawingSurface : ServerCompositionSurface, IDisposable
{
    private IRef<IBitmapImpl>? _bitmap;
    private IPlatformRenderInterfaceContext? _createdWithContext;

    // --- UpdateWith* phase timing ------------------------------------------------------
    // These calls are posted as server jobs and run inside ServerCompositor's jobs1
    // bucket. We split each call into:
    //   ensureCurrent : RenderInterface.EnsureCurrent() (context bind)
    //   sanity        : PerformSanityChecks
    //   snapshot      : the Image.SnapshotWith*() call itself — this is where Skia
    //                   submits a CB that waits on the user's semaphore (or imports
    //                   memory). If THIS is the slow part, the trigger is the user's
    //                   GPU work not signalling renderComplete on time.
    //   update        : the Update() local that swaps the bitmap and fires Changed
    private const double UpdDiagJitterThresholdMs = 5.0;
    private const int    UpdDiagWorstPerWindow    = 5;
    private long _updDiagFrameCount;
    private long _updDiagJitterCount;
    private long _updDiagLastReportTs;
    private readonly double[] _updDiagWorstTotal       = new double[UpdDiagWorstPerWindow];
    private readonly double[] _updDiagWorstEnsureCur   = new double[UpdDiagWorstPerWindow];
    private readonly double[] _updDiagWorstSanity      = new double[UpdDiagWorstPerWindow];
    private readonly double[] _updDiagWorstSnapshot    = new double[UpdDiagWorstPerWindow];
    private readonly double[] _updDiagWorstUpdate      = new double[UpdDiagWorstPerWindow];
    private readonly long  [] _updDiagWorstFrameNo     = new long  [UpdDiagWorstPerWindow];
    private readonly string[] _updDiagWorstKind        = new string[UpdDiagWorstPerWindow];

    private void UpdDiagReport(string kind, double ensureCurMs, double sanityMs,
        double snapshotMs, double updateMs)
    {
        _updDiagFrameCount++;
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        double totalMs = ensureCurMs + sanityMs + snapshotMs + updateMs;

        if (totalMs > UpdDiagJitterThresholdMs)
        {
            _updDiagJitterCount++;
            int worstIdx = 0;
            for (int i = 1; i < UpdDiagWorstPerWindow; i++)
                if (_updDiagWorstTotal[i] < _updDiagWorstTotal[worstIdx]) worstIdx = i;
            if (totalMs > _updDiagWorstTotal[worstIdx])
            {
                _updDiagWorstTotal    [worstIdx] = totalMs;
                _updDiagWorstEnsureCur[worstIdx] = ensureCurMs;
                _updDiagWorstSanity   [worstIdx] = sanityMs;
                _updDiagWorstSnapshot [worstIdx] = snapshotMs;
                _updDiagWorstUpdate   [worstIdx] = updateMs;
                _updDiagWorstFrameNo  [worstIdx] = _updDiagFrameCount;
                _updDiagWorstKind     [worstIdx] = kind;
            }
        }

        long now = Stopwatch.GetTimestamp();
        if (_updDiagLastReportTs == 0) _updDiagLastReportTs = now;
        double sinceReportMs = (now - _updDiagLastReportTs) * tickToMs;
        if (sinceReportMs >= 1000.0)
        {
            Console.Error.WriteLine(
                $"[UPD] 1s: calls={_updDiagFrameCount} jitter={_updDiagJitterCount}");
            for (int i = 0; i < UpdDiagWorstPerWindow; i++)
            {
                if (_updDiagWorstTotal[i] <= 0) continue;
                Console.Error.WriteLine(
                    $"  worst#{i} f={_updDiagWorstFrameNo[i]} kind={_updDiagWorstKind[i]} "
                    + $"total={_updDiagWorstTotal[i]:F2}ms "
                    + $"ensureCur={_updDiagWorstEnsureCur[i]:F2} "
                    + $"sanity={_updDiagWorstSanity[i]:F2} "
                    + $"snapshot={_updDiagWorstSnapshot[i]:F2} "
                    + $"update={_updDiagWorstUpdate[i]:F2}");
                _updDiagWorstTotal[i] = 0; _updDiagWorstEnsureCur[i] = 0;
                _updDiagWorstSanity[i] = 0; _updDiagWorstSnapshot[i] = 0;
                _updDiagWorstUpdate[i] = 0; _updDiagWorstFrameNo[i] = 0;
                _updDiagWorstKind[i] = null!;
            }
            _updDiagFrameCount = 0; _updDiagJitterCount = 0; _updDiagLastReportTs = now;
        }
    }

    public override IRef<IBitmapImpl>? Bitmap
    {
        get
        {
            // Failsafe to avoid consuming an image imported with a different context
            if (Compositor.RenderInterface.Value != _createdWithContext)
                return null;
            return _bitmap;
        }
    }

    public ServerCompositionDrawingSurface(ServerCompositor compositor) : base(compositor)
    {
    }

    void PerformSanityChecks(CompositionImportedGpuImage image)
    {
        // Failsafe to avoid consuming an image imported with a different context
        if (!image.IsUsable)
            throw new PlatformGraphicsContextLostException();

        // This should never happen, but check for it anyway to avoid a deadlock
        if (!image.ImportCompleted.IsCompleted)
            throw new InvalidOperationException("The import operation is not completed yet");

        // Rethrow the import here exception
        if (image.ImportCompleted.IsFaulted)
            image.ImportCompleted.GetAwaiter().GetResult();
    }

    void Update(IBitmapImpl newImage, IPlatformRenderInterfaceContext context)
    {
        _bitmap?.Dispose();
        _bitmap = RefCountable.Create(newImage);
        _createdWithContext = context;
        Changed?.Invoke();
    }

    public void UpdateWithAutomaticSync(CompositionImportedGpuImage image)
    {
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        long t0 = Stopwatch.GetTimestamp();
        using (Compositor.RenderInterface.EnsureCurrent())
        {
            long t1 = Stopwatch.GetTimestamp();
            PerformSanityChecks(image);
            long t2 = Stopwatch.GetTimestamp();
            var snap = image.Image.SnapshotWithAutomaticSync();
            long t3 = Stopwatch.GetTimestamp();
            Update(snap, image.Context);
            long t4 = Stopwatch.GetTimestamp();
            UpdDiagReport("Auto",
                (t1 - t0) * tickToMs, (t2 - t1) * tickToMs, (t3 - t2) * tickToMs, (t4 - t3) * tickToMs);
        }
    }

    public void UpdateWithKeyedMutex(CompositionImportedGpuImage image, uint acquireIndex, uint releaseIndex)
    {
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        long t0 = Stopwatch.GetTimestamp();
        using (Compositor.RenderInterface.EnsureCurrent())
        {
            long t1 = Stopwatch.GetTimestamp();
            PerformSanityChecks(image);
            long t2 = Stopwatch.GetTimestamp();
            var snap = image.Image.SnapshotWithKeyedMutex(acquireIndex, releaseIndex);
            long t3 = Stopwatch.GetTimestamp();
            Update(snap, image.Context);
            long t4 = Stopwatch.GetTimestamp();
            UpdDiagReport("KeyedMutex",
                (t1 - t0) * tickToMs, (t2 - t1) * tickToMs, (t3 - t2) * tickToMs, (t4 - t3) * tickToMs);
        }
    }

    public void UpdateWithSemaphores(CompositionImportedGpuImage image, CompositionImportedGpuSemaphore wait, CompositionImportedGpuSemaphore signal)
    {
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        long t0 = Stopwatch.GetTimestamp();
        using (Compositor.RenderInterface.EnsureCurrent())
        {
            long t1 = Stopwatch.GetTimestamp();
            PerformSanityChecks(image);
            if (!wait.IsUsable || !signal.IsUsable)
                throw new PlatformGraphicsContextLostException();
            long t2 = Stopwatch.GetTimestamp();
            var snap = image.Image.SnapshotWithSemaphores(wait.Semaphore, signal.Semaphore);
            long t3 = Stopwatch.GetTimestamp();
            Update(snap, image.Context);
            long t4 = Stopwatch.GetTimestamp();
            UpdDiagReport("Semaphores",
                (t1 - t0) * tickToMs, (t2 - t1) * tickToMs, (t3 - t2) * tickToMs, (t4 - t3) * tickToMs);
        }
    }

    public void UpdateWithTimelineSemaphores(CompositionImportedGpuImage image,
        CompositionImportedGpuSemaphore wait, ulong waitForValue,
        CompositionImportedGpuSemaphore signal, ulong signalValue)
    {
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        long t0 = Stopwatch.GetTimestamp();
        using (Compositor.RenderInterface.EnsureCurrent())
        {
            long t1 = Stopwatch.GetTimestamp();
            PerformSanityChecks(image);
            if (!wait.IsUsable || !signal.IsUsable)
                throw new PlatformGraphicsContextLostException();
            long t2 = Stopwatch.GetTimestamp();
            var snap = image.Image.SnapshotWithTimelineSemaphores(wait.Semaphore, waitForValue, signal.Semaphore, signalValue);
            long t3 = Stopwatch.GetTimestamp();
            Update(snap, image.Context);
            long t4 = Stopwatch.GetTimestamp();
            UpdDiagReport("Timeline",
                (t1 - t0) * tickToMs, (t2 - t1) * tickToMs, (t3 - t2) * tickToMs, (t4 - t3) * tickToMs);
        }
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
    }
}
