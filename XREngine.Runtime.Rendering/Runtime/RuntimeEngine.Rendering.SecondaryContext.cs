using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using XREngine.Data.Profiling;
using System.Threading;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using XREngine.Data.Vectors;

namespace XREngine
{
    public static partial class RuntimeEngine
    {
        public static partial class Rendering
        {
            /// <summary>
            /// Background compute context intended for secondary GPUs or shared-context offloading.
            /// </summary>
            public sealed class SecondaryGpuContext : IDisposable
            {
                private readonly ConcurrentQueue<Action<AbstractRenderer>> _jobs = new();
                private readonly AutoResetEvent _jobSignal = new(false);
                private CancellationTokenSource? _cts;
                private Thread? _thread;
                private XRWindow? _headlessWindow;
                private XRWindow? _templateWindow;
                private int _releaseStarted;
                private AbstractRenderer? _renderer;

                public bool IsRunning => _thread is not null && _thread.IsAlive;
                public bool HasRenderer => _renderer is not null;

                public void InitializeIfSupported(XRWindow? templateWindow)
                {
                    if (IsRunning || templateWindow is null)
                        return;

                    if (!RuntimeEngine.Rendering.Settings.EnableSecondaryGpuCompute)
                        return;

                    IRuntimeRendererHost? templateRenderer = templateWindow.Renderer;
                    if (templateRenderer?.BackendId == RendererBackendId.Vulkan)
                    {
                        XREngine.Debug.RenderingWarning(
                            "Secondary Vulkan GPU context is unavailable: its separate device and surface " +
                            "ownership has not been established for the desktop window backend.");
                        return;
                    }

                    if (!HasMultipleGpus() && !RuntimeEngine.Rendering.Settings.AllowSecondaryContextSharingFallback)
                        return;

                    if (templateWindow.DesktopGlContext is null)
                    {
                        XREngine.Debug.RenderingWarning(
                            "Secondary GPU context requires a desktop OpenGL context with explicit owner transfer.");
                        return;
                    }

                    if (Environment.CurrentManagedThreadId != templateWindow.NativeWindowThreadId)
                    {
                        XREngine.Debug.RenderingWarning(
                            "Secondary GPU context creation must run on the native window owner thread.");
                        return;
                    }

                    Interlocked.Exchange(ref _releaseStarted, 0);
                    _cts = new CancellationTokenSource();
                    _templateWindow = templateWindow;
                    try
                    {
                        CreateHeadlessWindow(templateWindow);
                    }
                    catch (Exception ex)
                    {
                        XREngine.Debug.RenderingWarning($"Secondary GPU context creation failed: {ex.Message}");
                        _headlessWindow?.Dispose();
                        _headlessWindow = null;
                        _templateWindow = null;
                        _cts.Dispose();
                        _cts = null;
                        return;
                    }

                    CancellationToken token = _cts.Token;
                    _thread = new Thread(() => RunContext(token))
                    {
                        IsBackground = true,
                        Name = "XR Secondary Render Context"
                    };
                    _thread.Start();
                }

                public bool EnqueueJob(Action<AbstractRenderer> job, bool allowFallbackToMainThread = true)
                {
                    if (job is null)
                        return false;

                    if (IsRunning)
                    {
                        _jobs.Enqueue(job);
                        _jobSignal.Set();
                        return true;
                    }

                    if (!allowFallbackToMainThread)
                        return false;

                    // fallback executes on render thread to preserve correctness
                    RuntimeEngine.EnqueueMainThreadTask(() =>
                    {
                        var renderer = AbstractRenderer.Current ?? RuntimeEngine.Windows.FirstOrDefault()?.Renderer;
                        if (renderer is null)
                            return;
                        job(renderer);
                    });
                    return true;
                }

                public void Dispose()
                {
                    try
                    {
                        _cts?.Cancel();
                        _jobSignal.Set();
                        _thread?.Join(TimeSpan.FromSeconds(1));
                    }
                    catch
                    {
                        // ignored - best effort shutdown
                    }

                    if (_thread is { IsAlive: true })
                        return;

                    ReleaseStoppedResources();
                }

                private void RunContext(CancellationToken token)
                {
                    try
                    {
                        var renderer = _renderer;
                        if (renderer is null || _headlessWindow is null)
                            return;

                        IRuntimeWindowGlContext context = _headlessWindow.DesktopGlContext
                            ?? throw new InvalidOperationException("Secondary GPU window lost its GL context.");
                        context.MakeCurrent();
                        renderer.Initialize();

                        while (!token.IsCancellationRequested)
                        {
                            if (!_jobs.TryDequeue(out var job))
                            {
                                _jobSignal.WaitOne(TimeSpan.FromMilliseconds(2));
                                continue;
                            }

                            try
                            {
                                renderer.Active = true;
                                AbstractRenderer.Current = renderer;
                                job(renderer);
                            }
                            finally
                            {
                                renderer.Active = false;
                                AbstractRenderer.Current = null;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        XREngine.Debug.RenderingWarning($"Secondary render context terminated: {ex.Message}\n{ex.StackTrace}");
                    }
                    finally
                    {
                        try
                        {
                            _renderer?.CleanUp();
                            _headlessWindow?.DesktopGlContext?.ClearCurrent();
                        }
                        catch (Exception ex)
                        {
                            _renderer?.AbandonShutdownTeardown();
                            XREngine.Debug.RenderingWarning($"Secondary GPU context teardown was abandoned: {ex.Message}");
                        }

                        ReleaseStoppedResources();
                    }
                }

                private void CreateHeadlessWindow(XRWindow templateWindow)
                {
                    var size = templateWindow.WindowSizeSnapshot;
                    int width = Math.Max(64, size.X / 8);
                    int height = Math.Max(64, size.Y / 8);
                    var startup = WindowStartupValues.Default with
                    {
                        Title = "XR Secondary GPU Context",
                        Width = width,
                        Height = height,
                    };
                    var request = new RuntimeWindowCreateOptions(
                        startup,
                        RuntimeGraphicsApiKind.OpenGL,
                        EInteractiveWindowResizeStrategy.Default,
                        RuntimeWindowPurpose.SecondaryGpuContext,
                        IVector2.Zero,
                        new IVector2(width, height),
                        false, false, false, false, false,
                        32, 24, 8, 4, 6, false, false, false,
                        RuntimeEngine.Rendering.Settings.AllowSecondaryContextSharingFallback
                            ? templateWindow.DesktopGlContext
                            : null);

                    var window = new XRWindow(request);
                    _headlessWindow = window;
                    _renderer = window.Renderer;
                    window.DesktopGlContext?.ClearCurrent();
                    templateWindow.DesktopGlContext?.MakeCurrent();
                }

                private void ReleaseStoppedResources()
                {
                    if (Interlocked.Exchange(ref _releaseStarted, 1) != 0)
                        return;

                    XRWindow? headless = _headlessWindow;
                    XRWindow? template = _templateWindow;
                    if (headless is not null && template is not null)
                        RuntimeRenderingHostServices.Scheduling.EnqueueWindowThreadTask(
                            template,
                            headless.Dispose,
                            "SecondaryGpuContext.DestroyNativeWindow");

                    _cts?.Dispose();
                    _headlessWindow = null;
                    _templateWindow = null;
                    _renderer = null;
                    _thread = null;
                    _cts = null;
                }

                private static bool HasMultipleGpus()
                {
                    IHardwareInventory? inventory = HardwareInventoryServices.Current;
                    if (inventory is null)
                        return false;
                    if (inventory.TryGetActiveGpuCount(out int count, out string? diagnostic))
                        return count > 1;
                    XREngine.Debug.RenderingWarning($"Unable to query GPU inventory: {diagnostic}");
                    return false;
                }
            }

            public static SecondaryGpuContext SecondaryContext { get; } = new();

            public static IReadOnlyList<string> RecommendedSecondaryGpuTasks { get; } = new List<string>
            {
                "CPU-visible readback of GPU counters and visibility buffers to avoid stalling the main swap chain",
                "Async skinning and bounds expansion for skinned meshes",
                "Mesh signed-distance-field (SDF) generation and voxelization jobs",
                "Building Hi-Z/occlusion data for next-frame culling",
                "Light probe or irradiance volume updates when not bound to the main frame budget"
            };
        }
    }
}
