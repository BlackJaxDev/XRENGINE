using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
public unsafe partial class VulkanRenderer
{
        internal VulkanFrameTelemetry FrameTelemetry => _frameTelemetry;

        /// <summary>
        /// Coordinates one allocation-free desktop Vulkan frame attempt.
        /// </summary>
        private void RenderComposedFrame(double delta)
            => FrameLoop.Render(this, delta);

        internal void ReportReentrantDesktopFrame()
        {
            DesktopFrameActivitySnapshot active =
                CaptureDesktopFrameActivity();
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.ReentrantWindowRenderSkipped",
                TimeSpan.FromMilliseconds(250),
                "[Vulkan] Skipping reentrant desktop window render callback. ActiveFrame={0} ActiveFrameSlot={1}",
                active.FrameNumber,
                active.FrameSlot);
        }

        internal bool IsDesktopFrameDeviceLost => _deviceLost;

        internal EDesktopFrameFlow AcquireDesktopFrameTarget(
            ref VulkanFrameAttempt attempt)
            => DesktopWsiOutput.AcquireFrameTarget(this, ref attempt);

        internal void ReportDesktopFrameTelemetryFailure(
            Exception telemetryFailure)
            => Debug.VulkanWarning(
                "[Vulkan] Desktop frame telemetry finalization failed: {0}",
                telemetryFailure.Message);

    }
}
