using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering;

/// <summary>
/// Registration seam for optional vendor-upscale support. The stable kernel never references
/// a concrete Vulkan implementation assembly.
/// </summary>
internal static class RuntimeVendorUpscaleService
{
    private static readonly object Sync = new();
    private static IRuntimeVendorUpscaleService? _current;
    private static int _registrationCount;

    public static IRuntimeVendorUpscaleService? Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static IDisposable Register(IRuntimeVendorUpscaleService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (Sync)
        {
            if (_current is not null && !ReferenceEquals(_current, service))
                throw new InvalidOperationException("A vendor-upscale runtime service is already registered.");

            _current = service;
            _registrationCount++;
        }

        return new Registration(service);
    }

    private sealed class Registration(IRuntimeVendorUpscaleService service) : IDisposable
    {
        private IRuntimeVendorUpscaleService? _service = service;

        public void Dispose()
        {
            IRuntimeVendorUpscaleService? current = Interlocked.Exchange(ref _service, null);
            if (current is null)
                return;

            bool releaseBridges = false;
            lock (Sync)
            {
                if (ReferenceEquals(_current, current))
                {
                    _registrationCount--;
                    if (_registrationCount == 0)
                    {
                        _current = null;
                        releaseBridges = true;
                    }
                }
            }

            if (releaseBridges)
            {
                RuntimeEngine.Rendering.ReleaseAllVulkanUpscaleBridges(
                    "vendor-upscale runtime module unloaded");
            }
        }
    }
}
