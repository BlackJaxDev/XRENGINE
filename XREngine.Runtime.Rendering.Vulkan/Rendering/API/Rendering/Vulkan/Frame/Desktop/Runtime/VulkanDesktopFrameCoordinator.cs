using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the exactly-once lifecycle of desktop Vulkan frame attempts.
/// </summary>
internal sealed class VulkanDesktopFrameCoordinator
{
    private readonly VulkanRenderer _renderer;
    private readonly DesktopFrameActivityState _activity = new();
    private readonly object _retirementGate = new();
    private int _frameSlot;
    private ulong _acceptedAttemptCount;
    private long _lastObservedTickTimestamp;

    internal object RetirementGate => _retirementGate;
    internal ulong AcceptedAttemptCount => Volatile.Read(ref _acceptedAttemptCount);
    internal int CurrentFrameSlot => Volatile.Read(ref _frameSlot);
    internal long LastObservedTickTimestamp => Volatile.Read(ref _lastObservedTickTimestamp);
    internal bool HasObservedTick => LastObservedTickTimestamp != 0;

    internal DesktopFrameActivitySnapshot CaptureActivity()
        => _activity.Capture();

    internal bool TryEnter(out DesktopFrameIdentity identity)
    {
        lock (_retirementGate)
        {
            int frameSlot = CurrentFrameSlot;
            ulong frameNumber = checked(AcceptedAttemptCount + 1UL);
            if (!_activity.TryEnter(
                    frameNumber,
                    frameSlot,
                    out long activityPublicationToken))
            {
                identity = default;
                return false;
            }

            Volatile.Write(ref _acceptedAttemptCount, frameNumber);
            identity = new DesktopFrameIdentity(
                frameNumber,
                frameSlot,
                Stopwatch.GetTimestamp(),
                activityPublicationToken);
            return true;
        }
    }

    internal void Exit(in DesktopFrameIdentity identity)
    {
        lock (_retirementGate)
            _activity.TryExit(identity.ActivityPublicationToken);
    }

    internal void AdvanceFrameSlot(int completedFrameSlot, int frameSlotCount)
    {
        if (frameSlotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSlotCount));

        Volatile.Write(
            ref _frameSlot,
            (completedFrameSlot + 1) % frameSlotCount);
    }

    internal void RecordObservedTick(long timestamp)
        => Volatile.Write(ref _lastObservedTickTimestamp, timestamp);

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
