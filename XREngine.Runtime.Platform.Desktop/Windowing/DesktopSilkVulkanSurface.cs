using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Borrowed Vulkan WSI operations; the Vulkan renderer owns surface destruction.</summary>
internal sealed unsafe class DesktopSilkVulkanSurface : IRuntimeWindowVulkanSurface
{
    private readonly IWindow _window;
    private int _retired;

    public DesktopSilkVulkanSurface(IWindow window, long ownerGeneration)
    {
        _window = window;
        OwnerGeneration = ownerGeneration;
        var provider = window.VkSurface
            ?? throw new InvalidOperationException("The desktop window has no Vulkan surface provider.");
        byte** names = provider.GetRequiredExtensions(out uint count);
        RequiredInstanceExtensions = SilkMarshal.PtrToStringArray((nint)names, checked((int)count));
    }

    public IReadOnlyList<string> RequiredInstanceExtensions { get; }
    public long OwnerGeneration { get; }

    public ulong CreateSurface(nint instance)
    {
        if (Volatile.Read(ref _retired) != 0)
            throw new ObjectDisposedException(nameof(DesktopSilkVulkanSurface));
        if (_window.VkSurface is null)
            throw new ObjectDisposedException(nameof(DesktopSilkVulkanSurface));
        return _window.VkSurface.Create<AllocationCallbacks>(new VkHandle(instance), null).Handle;
    }

    internal void Retire() => Volatile.Write(ref _retired, 1);
}
