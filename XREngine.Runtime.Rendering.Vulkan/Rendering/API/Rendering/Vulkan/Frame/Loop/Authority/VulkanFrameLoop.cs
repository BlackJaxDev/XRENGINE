using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns desktop frame admission, attempt identity, frame-slot progression, and
/// the ordered composition of each desktop frame attempt.
/// </summary>
internal sealed class VulkanFrameLoop
{
    private const int FrameSlotCount = 2;
    private readonly DesktopFrameActivityState _activity = new();
    private readonly object _retirementGate = new();
    private int _frameSlot;
    private ulong _acceptedAttemptCount;
    private long _lastObservedTickTimestamp;
    private long _resourceCatchUpStartedAt;
    private ulong _resourceCatchUpBlockedFrames;

    internal ulong AcceptedAttemptCount => Volatile.Read(ref _acceptedAttemptCount);
    internal object RetirementGate => _retirementGate;
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
            if (!_activity.TryEnter(frameNumber, frameSlot, out long activityPublicationToken))
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

    internal void AdvanceFrameSlot(int completedFrameSlot)
        => Volatile.Write(ref _frameSlot, (completedFrameSlot + 1) % FrameSlotCount);

    internal void RecordObservedTick(long timestamp)
        => Volatile.Write(ref _lastObservedTickTimestamp, timestamp);

    internal void ResetResourceCatchUpProgress()
    {
        _resourceCatchUpStartedAt = 0;
        _resourceCatchUpBlockedFrames = 0;
    }

    internal (ulong BlockedFrames, TimeSpan Elapsed) RecordResourceCatchUpProgress(long timestamp)
    {
        if (_resourceCatchUpStartedAt == 0)
            _resourceCatchUpStartedAt = timestamp;

        return (++_resourceCatchUpBlockedFrames,
            Stopwatch.GetElapsedTime(_resourceCatchUpStartedAt, timestamp));
    }

    /// <summary>Executes one allocation-free desktop callback attempt.</summary>
    internal void Render(VulkanRenderer renderer, double delta)
    {
        _ = delta;
        if (!TryEnter(out DesktopFrameIdentity desktopFrameIdentity))
        {
            renderer.ReportReentrantDesktopFrame();
            return;
        }

        VulkanFrameAttempt frameAttempt = new(renderer.FrameTelemetry, in desktopFrameIdentity);
        try
        {
            if (renderer.IsDesktopFrameDeviceLost)
                throw renderer.CreateDeviceLostException("RenderWindow", Result.ErrorDeviceLost);

            renderer.BeginDescriptorHeapFrame(frameAttempt.FrameNumber);
            renderer.RecordDesktopFrameGap(ref frameAttempt);

            if (renderer.RunDesktopFramePreflight(ref frameAttempt) != EDesktopFrameFlow.Continue ||
                renderer.PrepareDesktopFrameSlot(ref frameAttempt) != EDesktopFrameFlow.Continue ||
                renderer.AcquireDesktopFrameTarget(ref frameAttempt) != EDesktopFrameFlow.Continue)
                return;

            renderer.PrepareAcquiredDesktopImage(ref frameAttempt);
            if (renderer.RecordDesktopFrame(ref frameAttempt) != EDesktopFrameFlow.Continue ||
                renderer.SubmitDesktopFrame(ref frameAttempt) != EDesktopFrameFlow.Continue)
                return;

            _ = renderer.PresentSubmittedDesktopFrame(ref frameAttempt);
        }
        catch (Exception primaryFailure)
        {
            frameAttempt.PrimaryFailure = primaryFailure;
            renderer.SettleDesktopAcquireAfterUnexpectedFailure(ref frameAttempt, primaryFailure);
            throw;
        }
        finally
        {
            try
            {
                renderer.PublishDesktopFrameTelemetry(ref frameAttempt);
            }
            catch (Exception telemetryFailure)
            {
                if (frameAttempt.PrimaryFailure is null)
                    throw;

                renderer.ReportDesktopFrameTelemetryFailure(telemetryFailure);
            }
            finally
            {
                Exit(in desktopFrameIdentity);
            }
        }
    }
}
