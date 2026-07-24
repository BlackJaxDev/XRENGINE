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
        {
            _ = delta;
            if (!TryEnterDesktopFrameAttempt(
                    out DesktopFrameIdentity desktopFrameIdentity))
            {
                DesktopFrameActivitySnapshot active =
                    CaptureDesktopFrameActivity();
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.ReentrantWindowRenderSkipped",
                    TimeSpan.FromMilliseconds(250),
                    "[Vulkan] Skipping reentrant desktop window render callback. ActiveFrame={0} ActiveFrameSlot={1}",
                    active.FrameNumber,
                    active.FrameSlot);
                return;
            }

            DesktopFrameAttempt attempt = new(in desktopFrameIdentity);
            try
            {
                if (_deviceLost)
                    throw CreateDeviceLostException(
                        "RenderWindow",
                        Result.ErrorDeviceLost);

                BeginDescriptorHeapFrame(attempt.FrameNumber);
                RecordDesktopFrameGap(ref attempt);

                if (RunDesktopFramePreflight(ref attempt) !=
                    EDesktopFrameFlow.Continue)
                    return;
                if (PrepareDesktopFrameSlot(ref attempt) !=
                    EDesktopFrameFlow.Continue)
                    return;
                if (AcquireDesktopSwapchainImage(ref attempt) !=
                    EDesktopFrameFlow.Continue)
                    return;

                PrepareAcquiredDesktopImage(ref attempt);
                if (RecordDesktopFrame(ref attempt) !=
                    EDesktopFrameFlow.Continue)
                    return;
                if (SubmitDesktopFrame(ref attempt) !=
                    EDesktopFrameFlow.Continue)
                    return;

                _ = PresentSubmittedDesktopFrame(ref attempt);
            }
            catch (Exception primaryFailure)
            {
                attempt.PrimaryFailure = primaryFailure;
                SettleDesktopAcquireAfterUnexpectedFailure(
                    ref attempt,
                    primaryFailure);
                throw;
            }
            finally
            {
                try
                {
                    PublishDesktopFrameTelemetry(ref attempt);
                }
                catch (Exception telemetryFailure)
                {
                    if (attempt.PrimaryFailure is null)
                        throw;

                    Debug.VulkanWarning(
                        "[Vulkan] Desktop frame telemetry finalization failed: {0}",
                        telemetryFailure.Message);
                }
                finally
                {
                    ExitDesktopFrameAttempt(in desktopFrameIdentity);
                }
            }
        }
    }
}
