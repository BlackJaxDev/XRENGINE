using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Verifies that the OpenXR Vulkan submission path hands a temporal-history
/// candidate to publication exactly once, only after native queue acceptance,
/// and outside the submission tracker lock.
/// </summary>
[TestFixture]
public sealed class OpenXrTemporalHistoryPublicationTests
{
    private const int SlotIndex = 1;
    private const ulong TicketGeneration = 7UL;
    private const string SubmissionSourcePath =
        "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanCommandRuntime.OpenXrSubmission.cs";
    private const string TrackerSourcePath =
        "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/OpenXrVulkanSubmissionTracker.cs";

    [Test]
    public void TryClaimAcceptedCandidate_RequiresNativeAcceptanceAndClaimsOnce()
    {
        TemporalHistorySubmissionCandidate candidate = CreateCandidate(token: 5UL);
        OpenXrVulkanSubmissionTracker.InFlightSubmission entry = CreateRegisteredEntry(candidate, default);
        OpenXrVulkanSubmissionTracker tracker = CreateDetachedTracker(entry);
        OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket ticket = new(tracker, SlotIndex, TicketGeneration);

        // A registered submission that the queue did not accept yields no candidate.
        tracker.TryClaimAcceptedTemporalHistoryCandidate(in ticket, out TemporalHistorySubmissionCandidate claimed)
            .ShouldBeFalse();
        claimed.IsValid.ShouldBeFalse();
        entry.TemporalHistoryCandidateClaimed.ShouldBeFalse();

        entry.NativeSubmissionAccepted = true;
        tracker.TryClaimAcceptedTemporalHistoryCandidate(in ticket, out claimed).ShouldBeTrue();
        claimed.ShouldBe(candidate);
        entry.TemporalHistoryCandidateClaimed.ShouldBeTrue();

        // The finally backstop claims again; the claim guard keeps publication to one call.
        tracker.TryClaimAcceptedTemporalHistoryCandidate(in ticket, out claimed).ShouldBeFalse();
        claimed.IsValid.ShouldBeFalse();
    }

    [Test]
    public void TryClaimAcceptedCandidate_RejectsForeignTicketsAndFinalizedReceipts()
    {
        TemporalHistorySubmissionCandidate candidate = CreateCandidate(token: 9UL);
        OpenXrVulkanSubmissionTracker.InFlightSubmission entry = CreateRegisteredEntry(candidate, default);
        entry.NativeSubmissionAccepted = true;
        OpenXrVulkanSubmissionTracker tracker = CreateDetachedTracker(entry);

        OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket staleGeneration =
            new(tracker, SlotIndex, TicketGeneration + 1UL);
        OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket otherSlot =
            new(tracker, SlotIndex + 1, TicketGeneration);
        tracker.TryClaimAcceptedTemporalHistoryCandidate(in staleGeneration, out _).ShouldBeFalse();
        tracker.TryClaimAcceptedTemporalHistoryCandidate(in otherSlot, out _).ShouldBeFalse();
        entry.TemporalHistoryCandidateClaimed.ShouldBeFalse();

        // Receipt finalization opens the entry to completion polling and closes the claim window.
        OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket ticket = new(tracker, SlotIndex, TicketGeneration);
        tracker.FinalizeAcceptedSubmissionReceipt(in ticket);
        entry.PendingCommit.ShouldBeFalse();
        tracker.TryClaimAcceptedTemporalHistoryCandidate(in ticket, out TemporalHistorySubmissionCandidate claimed)
            .ShouldBeFalse();
        claimed.IsValid.ShouldBeFalse();
    }

    [Test]
    public void TryClaimAcceptedCandidate_RequiresExactlyOneEyeCandidate()
    {
        TemporalHistorySubmissionCandidate first = CreateCandidate(token: 11UL);
        TemporalHistorySubmissionCandidate second = CreateCandidate(token: 12UL);
        OpenXrVulkanSubmissionTracker.InFlightSubmission ambiguous = CreateRegisteredEntry(first, second);
        ambiguous.NativeSubmissionAccepted = true;
        OpenXrVulkanSubmissionTracker tracker = CreateDetachedTracker(ambiguous);
        OpenXrVulkanSubmissionTracker.SubmissionAdmissionTicket ticket = new(tracker, SlotIndex, TicketGeneration);

        tracker.TryClaimAcceptedTemporalHistoryCandidate(in ticket, out _).ShouldBeFalse();
        ambiguous.TemporalHistoryCandidateClaimed.ShouldBeFalse();

        OpenXrVulkanSubmissionTracker.InFlightSubmission missing = CreateRegisteredEntry(default, default);
        missing.NativeSubmissionAccepted = true;
        tracker = CreateDetachedTracker(missing);
        ticket = new(tracker, SlotIndex, TicketGeneration);
        tracker.TryClaimAcceptedTemporalHistoryCandidate(in ticket, out _).ShouldBeFalse();
        missing.TemporalHistoryCandidateClaimed.ShouldBeFalse();

        OpenXrVulkanSubmissionTracker.InFlightSubmission secondOnly = CreateRegisteredEntry(default, second);
        secondOnly.NativeSubmissionAccepted = true;
        tracker = CreateDetachedTracker(secondOnly);
        ticket = new(tracker, SlotIndex, TicketGeneration);
        tracker.TryClaimAcceptedTemporalHistoryCandidate(in ticket, out TemporalHistorySubmissionCandidate claimed)
            .ShouldBeTrue();
        claimed.ShouldBe(second);
    }

    [Test]
    public void OpenXrSubmission_PublishesAfterQueueGatewayBeforeReceiptFinalizationWithFinallyBackstop()
    {
        string source = SourceContractWorkspace.ReadExactFile(SubmissionSourcePath);
        const string publishCall = "PublishAcceptedTemporalHistoryCandidate(in input);";

        int gateway = source.IndexOf("submitReceipt = SubmitToGraphicsTimelineTrackedWithDisposition(", StringComparison.Ordinal);
        gateway.ShouldBeGreaterThanOrEqualTo(0);
        int acceptedPublish = source.IndexOf(publishCall, gateway, StringComparison.Ordinal);
        acceptedPublish.ShouldBeGreaterThan(gateway);

        // Publication precedes every receipt decision, including the publication-debt warning.
        source.IndexOf("OpenXrSubmissionTracker.ObserveSubmissionReceipt(", StringComparison.Ordinal)
            .ShouldBeGreaterThan(acceptedPublish);
        source.IndexOf("if (submitReceipt.Result != Result.Success)", StringComparison.Ordinal)
            .ShouldBeGreaterThan(acceptedPublish);
        source.IndexOf("if (!submitReceipt.PostSubmissionPublicationSucceeded)", StringComparison.Ordinal)
            .ShouldBeGreaterThan(acceptedPublish);

        // Receipt finalization clears PendingCommit, after which no claim can succeed.
        source.IndexOf("OpenXrSubmissionTracker.FinalizeAcceptedSubmissionReceipt(", StringComparison.Ordinal)
            .ShouldBeGreaterThan(acceptedPublish);

        // An exception after native acceptance still publishes through the finally block,
        // before the block decides whether to cancel the submitted arena slots.
        int finallyStart = source.IndexOf("        finally\n        {\n", acceptedPublish, StringComparison.Ordinal);
        finallyStart.ShouldBeGreaterThan(acceptedPublish);
        int backstopPublish = source.IndexOf(publishCall, finallyStart, StringComparison.Ordinal);
        backstopPublish.ShouldBeGreaterThan(finallyStart);
        source.IndexOf("nativeSubmitAccepted |=", finallyStart, StringComparison.Ordinal)
            .ShouldBeGreaterThan(backstopPublish);
        source.IndexOf(publishCall, backstopPublish + publishCall.Length, StringComparison.Ordinal).ShouldBe(-1);
    }

    [Test]
    public void OpenXrSubmission_PublicationIgnoresReceiptFlagAndRoutesDeviceLossOutsideTrackerLock()
    {
        string source = SourceContractWorkspace.ReadExactFile(SubmissionSourcePath);
        int helperStart = source.IndexOf(
            "private void PublishAcceptedTemporalHistoryCandidate(in VulkanOpenXrSubmissionInput input)",
            StringComparison.Ordinal);
        helperStart.ShouldBeGreaterThanOrEqualTo(0);
        int helperEnd = source.IndexOf("private unsafe void RetireOpenXrSubmission(", helperStart, StringComparison.Ordinal);
        helperEnd.ShouldBeGreaterThan(helperStart);
        string helper = source[helperStart..helperEnd];

        int claim = helper.IndexOf("OpenXrSubmissionTracker.TryClaimAcceptedTemporalHistoryCandidate(", StringComparison.Ordinal);
        int deviceCheck = helper.IndexOf("if (!DeviceContext.IsOperational)", StringComparison.Ordinal);
        int deviceLoss = helper.IndexOf("VPRC_TemporalAccumulationPass.InvalidateCandidateForDeviceLoss(in candidate);", StringComparison.Ordinal);
        int deviceLossReturn = helper.IndexOf("return;", deviceLoss, StringComparison.Ordinal);
        int accept = helper.IndexOf("VPRC_TemporalAccumulationPass.AcceptCandidate(in candidate);", StringComparison.Ordinal);
        claim.ShouldBeGreaterThanOrEqualTo(0);
        deviceCheck.ShouldBeGreaterThan(claim);
        deviceLoss.ShouldBeGreaterThan(deviceCheck);
        deviceLossReturn.ShouldBeGreaterThan(deviceLoss);
        accept.ShouldBeGreaterThan(deviceLossReturn);

        // An accepted-publication fault leaves the receipt flag false but must still publish once.
        helper.ShouldNotContain("PostSubmissionPublicationSucceeded");
        helper.ShouldNotContain("submitReceipt");
        helper.ShouldNotContain("lock (");

        // The tracker only claims under its gate; it never publishes history itself.
        string tracker = SourceContractWorkspace.ReadExactFile(TrackerSourcePath);
        tracker.ShouldNotContain("VPRC_TemporalAccumulationPass.AcceptCandidate");
    }

    [Test]
    public void OpenXrSubmissionTracker_DiscardsOnlyUnacceptedCandidatesAndInvalidatesOnDeviceLoss()
    {
        string tracker = SourceContractWorkspace.ReadExactFile(TrackerSourcePath);

        // Slot reuse resets acceptance and the claim guard before the ownership boundary.
        string register = Slice(tracker, "public bool RegisterSubmission(", "internal void SetSubmissionFrameSlotLifetimeSettledCallback");
        int resetAccepted = register.IndexOf("entry.NativeSubmissionAccepted = false;", StringComparison.Ordinal);
        int resetClaim = register.IndexOf("entry.TemporalHistoryCandidateClaimed = false;", StringComparison.Ordinal);
        int ownershipBoundary = register.IndexOf("entry.PendingCommit = true;", StringComparison.Ordinal);
        resetAccepted.ShouldBeGreaterThanOrEqualTo(0);
        resetClaim.ShouldBeGreaterThanOrEqualTo(0);
        ownershipBoundary.ShouldBeGreaterThan(resetAccepted);
        ownershipBoundary.ShouldBeGreaterThan(resetClaim);

        string commit = Slice(tracker, "public void CommitAcceptedSubmission(", "internal bool TryClaimAcceptedTemporalHistoryCandidate(");
        commit.ShouldContain("entry.NativeSubmissionAccepted = true;");

        // A cancelled (unsubmitted) attempt discards its candidates; an accepted one never does.
        string settleCancelled = Slice(tracker, "private bool SettleCancelledSubmission(InFlightSubmission entry)", "bool settled = SettleUploads(entry, publish: false);");
        int acceptedGuard = settleCancelled.IndexOf("|| entry.NativeSubmissionAccepted)\n            return false;", StringComparison.Ordinal);
        int discard = settleCancelled.IndexOf("VPRC_TemporalAccumulationPass.DiscardCandidate(in candidate);", StringComparison.Ordinal);
        acceptedGuard.ShouldBeGreaterThanOrEqualTo(0);
        discard.ShouldBeGreaterThan(acceptedGuard);

        string abandon = Slice(tracker, "private int AbandonAfterDeviceLossCore()", "public bool TryDisposeAfterDrain(");
        abandon.ShouldContain("VPRC_TemporalAccumulationPass.InvalidateCandidateForDeviceLoss(in candidate);");
        abandon.ShouldNotContain("DiscardCandidate(");
    }

    private static TemporalHistorySubmissionCandidate CreateCandidate(ulong token)
        => new(new object(), 1UL, token);

    private static OpenXrVulkanSubmissionTracker.InFlightSubmission CreateRegisteredEntry(
        TemporalHistorySubmissionCandidate first,
        TemporalHistorySubmissionCandidate second)
        => new()
        {
            Active = true,
            PendingCommit = true,
            AdmissionSlotIndex = SlotIndex,
            TicketGeneration = TicketGeneration,
            HasFirst = true,
            FirstRecorded = default(OpenXrRecordedEyeCommandBuffer) with { TemporalHistoryCandidate = first },
            HasSecond = true,
            SecondRecorded = default(OpenXrRecordedEyeCommandBuffer) with { TemporalHistoryCandidate = second },
        };

    /// <summary>
    /// Creates a tracker without a Vulkan device. Candidate claims and receipt
    /// finalization use only the tracker gate and its in-flight ownership table.
    /// </summary>
    private static OpenXrVulkanSubmissionTracker CreateDetachedTracker(OpenXrVulkanSubmissionTracker.InFlightSubmission entry)
    {
        var tracker = (OpenXrVulkanSubmissionTracker)RuntimeHelpers.GetUninitializedObject(typeof(OpenXrVulkanSubmissionTracker));
        SetTrackerField(tracker, "_gate", new object());
        SetTrackerField(tracker, "_inFlight", new OpenXrVulkanSubmissionTracker.InFlightSubmission[]
        {
            new(),
            entry,
            new(),
        });
        return tracker;
    }

    private static void SetTrackerField(OpenXrVulkanSubmissionTracker tracker, string name, object value)
        => typeof(OpenXrVulkanSubmissionTracker)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull()
            .SetValue(tracker, value);

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }
}
