using XREngine.Rendering.API.Rendering.OpenXR;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Holds smoke-only submission-validation evidence independently from individual
/// renderer lifetimes. A smoke run can recreate renderers during shutdown, so
/// detached OpenXR evidence is accumulated instead of being replaced by a later
/// desktop-only tracker.
/// </summary>
internal static class VulkanOpenXrSubmissionValidationState
{
    private static readonly object Gate = new();
    private static OpenXrSubmissionValidationRequest _request;
    // Weak registration lets a disabled observer remain non-owning while still
    // allowing a later smoke configuration to find an already-created tracker.
    private static readonly List<WeakReference<OpenXrVulkanSubmissionTracker>> Trackers = [];
    private static readonly List<OpenXrSubmissionOwnershipLedgerEntry> DetachedEntries = [];
    private static OpenXrSubmissionValidationSnapshot _detachedAggregate = new();
    private static long _nextRuntimeEpoch;

    internal static void Configure(in OpenXrSubmissionValidationRequest request)
    {
        lock (Gate)
        {
            _request = request with
            {
                LedgerCapacity = Math.Clamp(request.LedgerCapacity, 0, OpenXrVulkanSubmissionTracker.MaxSubmissionValidationEntries),
            };
            _nextRuntimeEpoch = 0;
            DetachedEntries.Clear();
            _detachedAggregate = new OpenXrSubmissionValidationSnapshot { Request = _request };
            for (int i = Trackers.Count - 1; i >= 0; i--)
            {
                if (!Trackers[i].TryGetTarget(out OpenXrVulkanSubmissionTracker? tracker))
                {
                    Trackers.RemoveAt(i);
                    continue;
                }
                tracker.ConfigureSubmissionValidation(in _request);
            }
        }
    }

    internal static void ConfigureNewTracker(OpenXrVulkanSubmissionTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        lock (Gate)
            tracker.ConfigureSubmissionValidation(in _request);
    }

    internal static void UpdateRegistration(OpenXrVulkanSubmissionTracker tracker, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        lock (Gate)
        {
            for (int i = Trackers.Count - 1; i >= 0; i--)
            {
                if (!Trackers[i].TryGetTarget(out OpenXrVulkanSubmissionTracker? registered) ||
                    ReferenceEquals(registered, tracker))
                {
                    Trackers.RemoveAt(i);
                    if (ReferenceEquals(registered, tracker))
                        break;
                }
            }
            Trackers.Add(new WeakReference<OpenXrVulkanSubmissionTracker>(tracker));
            if (enabled)
                tracker.SetSubmissionValidationRuntimeEpoch(++_nextRuntimeEpoch);
        }
    }

    internal static OpenXrSubmissionValidationSnapshot Capture()
    {
        lock (Gate)
        {
            int capacity = Math.Max(0, _request.LedgerCapacity);
            var entries = new List<OpenXrSubmissionOwnershipLedgerEntry>(capacity);
            var aggregate = new OpenXrSubmissionValidationSnapshot { Request = _request };
            MergeSnapshot(aggregate, _detachedAggregate, includeLiveCounts: false);
            AppendEntries(aggregate, entries, DetachedEntries, capacity);

            for (int i = Trackers.Count - 1; i >= 0; i--)
            {
                if (!Trackers[i].TryGetTarget(out OpenXrVulkanSubmissionTracker? tracker))
                {
                    Trackers.RemoveAt(i);
                    continue;
                }
                OpenXrSubmissionValidationSnapshot snapshot = tracker.CaptureSubmissionValidation();
                MergeSnapshot(aggregate, snapshot, includeLiveCounts: true);
                AppendEntries(aggregate, entries, snapshot.Entries, capacity);
            }

            aggregate.Entries = [.. entries];
            return aggregate;
        }
    }

    internal static void Detach(OpenXrVulkanSubmissionTracker tracker)
    {
        lock (Gate)
        {
            bool removed = false;
            for (int i = Trackers.Count - 1; i >= 0; i--)
            {
                if (!Trackers[i].TryGetTarget(out OpenXrVulkanSubmissionTracker? registered) ||
                    ReferenceEquals(registered, tracker))
                {
                    Trackers.RemoveAt(i);
                    removed |= ReferenceEquals(registered, tracker);
                }
            }
            if (!removed)
                return;
            OpenXrSubmissionValidationSnapshot snapshot = tracker.CaptureSubmissionValidation();
            MergeSnapshot(_detachedAggregate, snapshot, includeLiveCounts: false);
            AppendEntries(_detachedAggregate, DetachedEntries, snapshot.Entries, Math.Max(0, _request.LedgerCapacity));
        }
    }

    private static void MergeSnapshot(
        OpenXrSubmissionValidationSnapshot aggregate,
        OpenXrSubmissionValidationSnapshot source,
        bool includeLiveCounts)
    {
        aggregate.OverflowCount += source.OverflowCount;
        aggregate.AdmissionHighWater = Math.Max(aggregate.AdmissionHighWater, source.AdmissionHighWater);
        aggregate.AdmissionCapacity = Math.Max(aggregate.AdmissionCapacity, source.AdmissionCapacity);
        aggregate.ReservationDeferralCount += source.ReservationDeferralCount;
        aggregate.ForcedWaitCount += source.ForcedWaitCount;
        aggregate.HoldArmed |= source.HoldArmed;
        aggregate.HoldReleased |= source.HoldReleased;
        aggregate.AcceptedCount += source.AcceptedCount;
        aggregate.RejectedCount += source.RejectedCount;
        aggregate.PublicationFailureCount += source.PublicationFailureCount;
        aggregate.RealCompletionCount += source.RealCompletionCount;
        aggregate.RetiredCount += source.RetiredCount;
        aggregate.AbandonedSubmissionCount += source.AbandonedSubmissionCount;
        if (!includeLiveCounts)
            return;

        aggregate.ActiveCount += source.ActiveCount;
        aggregate.ReservedCount += source.ReservedCount;
        aggregate.PendingCommitCount += source.PendingCommitCount;
    }

    private static void AppendEntries(
        OpenXrSubmissionValidationSnapshot aggregate,
        List<OpenXrSubmissionOwnershipLedgerEntry> destination,
        IReadOnlyList<OpenXrSubmissionOwnershipLedgerEntry> source,
        int capacity)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (destination.Count < capacity)
                destination.Add(source[i]);
            else
                aggregate.OverflowCount++;
        }
    }
}
