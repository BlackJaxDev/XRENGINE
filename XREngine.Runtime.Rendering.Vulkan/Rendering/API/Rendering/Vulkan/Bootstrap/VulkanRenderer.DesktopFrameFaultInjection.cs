namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    /// <summary>Arms a one-shot desktop frame failure; see <see cref="VulkanDesktopFrameFaultInjection"/>.</summary>
    internal void ArmDesktopFrameFaultInjection(EVulkanDesktopFrameFaultPoint point, int occurrence)
        => _frameLoop.ArmDesktopFrameFaultInjection(point, occurrence);

    /// <summary>Clears a pending desktop frame failure.</summary>
    internal void ClearDesktopFrameFaultInjection()
        => _frameLoop.ClearDesktopFrameFaultInjection();
}
