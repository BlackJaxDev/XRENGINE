using XREngine.Data.Profiling;
using XREngine.Execution;
using XREngine.Rendering;

namespace XREngine
{
    public static partial class RuntimeEngine
    {
        public static partial class Rendering
        {
            /// <summary>Routes optional secondary graphics work to one captured native owner.</summary>
            public sealed class SecondaryGpuContext : IDisposable
            {
                private readonly object _gate = new();
                private IRuntimeSecondaryGpuContext? _owner;
                private bool _initializing;
                private long _stopGeneration;

                public bool IsRunning => CaptureOwner()?.IsRunning == true;
                public bool HasRenderer => CaptureOwner()?.HasRenderer == true;

                private IRuntimeSecondaryGpuContext? CaptureOwner()
                {
                    lock (_gate) return _owner;
                }

                public void InitializeIfSupported(XRWindow? templateWindow)
                {
                    if (templateWindow is null || !Settings.EnableSecondaryGpuCompute
                        || OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread)
                        return;
                    IRuntimeRendererHost? templateRenderer = templateWindow.Renderer;
                    if (templateRenderer?.BackendId == RendererBackendId.Vulkan)
                    {
                        XREngine.Debug.RenderingWarning(
                            "Secondary Vulkan GPU context is unavailable: its separate device and surface " +
                            "ownership has not been established for the desktop window backend.");
                        return;
                    }
                    bool allowSharing = Settings.AllowSecondaryContextSharingFallback;
                    if (!HasMultipleGpus() && !allowSharing)
                        return;
                    if (templateWindow.DesktopGlContext is null)
                    {
                        XREngine.Debug.RenderingWarning("Secondary GPU context requires a desktop OpenGL context with explicit owner transfer.");
                        return;
                    }
                    if (Environment.CurrentManagedThreadId != templateWindow.NativeWindowThreadId)
                    {
                        XREngine.Debug.RenderingWarning("Secondary GPU context creation must run on the native window owner thread.");
                        return;
                    }
                    IRuntimeWindowBackendFactory windows = RuntimeWindowBackendRegistry.RequireFactory();
                    if (windows is not IRuntimeSecondaryGpuContextFactory factory)
                    {
                        XREngine.Debug.RenderingWarning("SecondaryGpuContext.Unavailable: the installed window backend does not provide a secondary graphics owner.");
                        return;
                    }

                    long generation;
                    IRuntimeSecondaryGpuContext? previous;
                    lock (_gate)
                    {
                        if (_initializing) return;
                        _initializing = true;
                        generation = _stopGeneration;
                        previous = _owner;
                    }
                    try
                    {
                        if (previous is { IsReleased: false }) return;
                        IRuntimeSecondaryGpuContext candidate = factory.CreateSecondaryGpuContext(templateWindow, allowSharing)
                            ?? throw new InvalidOperationException("The window backend returned no secondary graphics owner.");
                        bool stop;
                        lock (_gate)
                        {
                            _owner = candidate;
                            stop = generation != _stopGeneration;
                        }
                        if (stop) candidate.Dispose();
                    }
                    catch (Exception error)
                    {
                        XREngine.Debug.RenderingWarning($"Secondary GPU context creation failed: {error.Message}");
                    }
                    finally { lock (_gate) _initializing = false; }
                }

                public bool EnqueueJob(Action<AbstractRenderer> job, bool allowFallbackToMainThread = true)
                {
                    if (job is null) return false;
                    IRuntimeSecondaryGpuContext? owner;
                    lock (_gate) owner = _owner;
                    if (owner?.TryEnqueue(job) == true) return true;
                    if (!allowFallbackToMainThread) return false;
                    RuntimeEngine.EnqueueMainThreadTask(() =>
                    {
                        AbstractRenderer? renderer = AbstractRenderer.Current ?? RuntimeEngine.Windows.FirstOrDefault()?.Renderer;
                        if (renderer is not null) job(renderer);
                    });
                    return true;
                }

                public void Dispose()
                {
                    IRuntimeSecondaryGpuContext? owner;
                    lock (_gate)
                    {
                        _stopGeneration++;
                        owner = _owner;
                    }
                    // Retain the owner if its bounded stop or native window disposal is incomplete.
                    owner?.Dispose();
                }

                private static bool HasMultipleGpus()
                {
                    IHardwareInventory? inventory = HardwareInventoryServices.Current;
                    if (inventory is null) return false;
                    if (inventory.TryGetActiveGpuCount(out int count, out string? diagnostic)) return count > 1;
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
