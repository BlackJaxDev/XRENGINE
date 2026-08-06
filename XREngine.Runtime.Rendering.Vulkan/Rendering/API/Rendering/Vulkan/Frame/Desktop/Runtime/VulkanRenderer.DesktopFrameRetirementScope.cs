using System.Threading;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Enters the coordinator-owned desktop retirement gate and exposes a
    /// read-only view of frame-slot synchronization state. OpenXR and
    /// diagnostics use this contract instead of reading mutable frame-loop
    /// fields directly.
    /// </summary>
    private DesktopFrameRetirementScope EnterDesktopFrameRetirementScope()
        => new(this, FrameLoop.RetirementGate);

    private readonly ref struct DesktopFrameRetirementScope
    {
        private readonly VulkanRenderer _renderer;
        private readonly object _gate;

        public DesktopFrameRetirementScope(
            VulkanRenderer renderer,
            object gate)
        {
            _renderer = renderer;
            _gate = gate;
            Monitor.Enter(gate);
        }

        public ReadOnlySpan<ulong> TimelineValues
            => _renderer._commandRuntime.Synchronization._frameSlotTimelineValues is { } values
                ? values
                : ReadOnlySpan<ulong>.Empty;

        public Semaphore TimelineSemaphore
            => _renderer._commandRuntime.Synchronization._graphicsTimelineSemaphore;

        public void Dispose()
            => Monitor.Exit(_gate);
    }
}
