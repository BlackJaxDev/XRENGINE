using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the exactly-once lifecycle of desktop Vulkan frame attempts.
/// </summary>
internal sealed class VulkanDesktopFrameCoordinator
{
    private readonly VulkanRenderer _renderer;

    internal VulkanDesktopFrameCoordinator(VulkanRenderer renderer)
        => _renderer = renderer;

    /// <summary>
    /// Executes one allocation-free desktop callback attempt.
    /// </summary>
    internal void Render(double delta)
    {
        _ = delta;
        if (!_renderer.TryEnterCoordinatedDesktopFrame(
                out DesktopFrameIdentity desktopFrameIdentity))
        {
            _renderer.ReportReentrantDesktopFrame();
            return;
        }

        VulkanFrameAttempt frameAttempt = new(in desktopFrameIdentity);
        try
        {
            if (_renderer.IsDesktopFrameDeviceLost)
            {
                throw _renderer.CreateDesktopFrameDeviceLostException(
                    "RenderWindow",
                    Result.ErrorDeviceLost);
            }

            _renderer.BeginCoordinatedDesktopFrame(frameAttempt.FrameNumber);
            _renderer.RecordCoordinatedDesktopFrameGap(ref frameAttempt);

            if (_renderer.RunCoordinatedDesktopFramePreflight(ref frameAttempt) !=
                EDesktopFrameFlow.Continue)
                return;
            if (_renderer.PrepareCoordinatedDesktopFrameSlot(ref frameAttempt) !=
                EDesktopFrameFlow.Continue)
                return;
            if (_renderer.AcquireCoordinatedDesktopSwapchainImage(ref frameAttempt) !=
                EDesktopFrameFlow.Continue)
                return;

            _renderer.PrepareCoordinatedDesktopImage(ref frameAttempt);
            if (_renderer.RecordCoordinatedDesktopFrame(ref frameAttempt) !=
                EDesktopFrameFlow.Continue)
                return;
            if (_renderer.SubmitCoordinatedDesktopFrame(ref frameAttempt) !=
                EDesktopFrameFlow.Continue)
                return;

            _ = _renderer.PresentCoordinatedDesktopFrame(ref frameAttempt);
        }
        catch (Exception primaryFailure)
        {
            frameAttempt.PrimaryFailure = primaryFailure;
            _renderer.SettleCoordinatedDesktopAcquireAfterFailure(
                ref frameAttempt,
                primaryFailure);
            throw;
        }
        finally
        {
            try
            {
                _renderer.PublishCoordinatedDesktopFrameTelemetry(ref frameAttempt);
            }
            catch (Exception telemetryFailure)
            {
                if (frameAttempt.PrimaryFailure is null)
                    throw;

                _renderer.ReportDesktopFrameTelemetryFailure(telemetryFailure);
            }
            finally
            {
                _renderer.ExitCoordinatedDesktopFrame(
                    in desktopFrameIdentity);
            }
        }
    }
}
