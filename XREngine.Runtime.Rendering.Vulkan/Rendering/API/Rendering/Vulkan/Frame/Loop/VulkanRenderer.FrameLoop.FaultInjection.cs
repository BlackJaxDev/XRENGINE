using System;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Arms a deterministic one-shot desktop frame failure at a phase boundary.
    /// This is an internal diagnostic seam and does not install a per-frame
    /// delegate or retain an external callback.
    /// </summary>
    internal void ArmDesktopFrameFaultInjection(
        EVulkanDesktopFrameFaultPoint point,
        int occurrence = 1)
        => _outputRuntime._desktopFrameFaultInjection.Arm(point, occurrence);

    /// <summary>
    /// Clears any pending deterministic desktop frame failure.
    /// </summary>
    internal void ClearDesktopFrameFaultInjection()
        => _outputRuntime._desktopFrameFaultInjection.Clear();

    private void ThrowIfDesktopFrameFaultInjected(
        EVulkanDesktopFrameFaultPoint point)
    {
        if (!_outputRuntime._desktopFrameFaultInjection.TryConsume(point))
            return;

        throw new InvalidOperationException(
            $"Injected Vulkan desktop frame failure at {point}.");
    }
}
