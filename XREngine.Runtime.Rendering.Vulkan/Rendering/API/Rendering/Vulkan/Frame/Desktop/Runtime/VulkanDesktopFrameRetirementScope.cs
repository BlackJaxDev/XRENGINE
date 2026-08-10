using System.Threading;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Holds the desktop retirement gate while exposing a read-only snapshot of
/// command-owned frame-slot synchronization state.
/// </summary>
internal readonly ref struct VulkanDesktopFrameRetirementScope
{
    private readonly VulkanCommandSynchronizationState _synchronization;
    private readonly object _gate;

    public VulkanDesktopFrameRetirementScope(
        VulkanCommandRuntime commandRuntime,
        object gate)
    {
        _synchronization = commandRuntime.Synchronization;
        _gate = gate;
        Monitor.Enter(gate);
    }

    public ReadOnlySpan<ulong> TimelineValues
        => _synchronization._frameSlotTimelineValues is { } values
            ? values
            : ReadOnlySpan<ulong>.Empty;

    public Semaphore TimelineSemaphore
        => _synchronization._graphicsTimelineSemaphore;

    public void Dispose()
        => Monitor.Exit(_gate);
}
