using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
public unsafe partial class VulkanRenderer
{
        /// <summary>
        /// Coordinates one allocation-free desktop Vulkan frame attempt.
        /// </summary>
        protected override void WindowRenderCallback(double delta)
            => DesktopFrameCoordinator.Render(delta);

        internal bool TryEnterCoordinatedDesktopFrame(
            out DesktopFrameIdentity identity)
            => TryEnterDesktopFrameAttempt(out identity);

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

        internal InvalidOperationException CreateDesktopFrameDeviceLostException(
            string operation,
            Result result)
            => CreateDeviceLostException(operation, result);

        internal void BeginCoordinatedDesktopFrame(ulong frameNumber)
            => BeginDescriptorHeapFrame(frameNumber);

        internal void RecordCoordinatedDesktopFrameGap(
            ref VulkanFrameAttempt attempt)
            => RecordDesktopFrameGap(ref attempt);

        internal EDesktopFrameFlow RunCoordinatedDesktopFramePreflight(
            ref VulkanFrameAttempt attempt)
            => RunDesktopFramePreflight(ref attempt);

        internal EDesktopFrameFlow PrepareCoordinatedDesktopFrameSlot(
            ref VulkanFrameAttempt attempt)
            => PrepareDesktopFrameSlot(ref attempt);

        internal EDesktopFrameFlow AcquireCoordinatedDesktopSwapchainImage(
            ref VulkanFrameAttempt attempt)
            => AcquireDesktopSwapchainImage(ref attempt);

        internal void PrepareCoordinatedDesktopImage(
            ref VulkanFrameAttempt attempt)
            => PrepareAcquiredDesktopImage(ref attempt);

        internal EDesktopFrameFlow RecordCoordinatedDesktopFrame(
            ref VulkanFrameAttempt attempt)
            => RecordDesktopFrame(ref attempt);

        internal EDesktopFrameFlow SubmitCoordinatedDesktopFrame(
            ref VulkanFrameAttempt attempt)
            => SubmitDesktopFrame(ref attempt);

        internal EDesktopFrameFlow PresentCoordinatedDesktopFrame(
            ref VulkanFrameAttempt attempt)
            => PresentSubmittedDesktopFrame(ref attempt);

        internal void SettleCoordinatedDesktopAcquireAfterFailure(
            ref VulkanFrameAttempt attempt,
            Exception primaryFailure)
            => SettleDesktopAcquireAfterUnexpectedFailure(
                ref attempt,
                primaryFailure);

        internal void PublishCoordinatedDesktopFrameTelemetry(
            ref VulkanFrameAttempt attempt)
            => PublishDesktopFrameTelemetry(ref attempt);

        internal void ReportDesktopFrameTelemetryFailure(
            Exception telemetryFailure)
            => Debug.VulkanWarning(
                "[Vulkan] Desktop frame telemetry finalization failed: {0}",
                telemetryFailure.Message);

        internal void ExitCoordinatedDesktopFrame(
            in DesktopFrameIdentity identity)
            => ExitDesktopFrameAttempt(in identity);
    }
}
