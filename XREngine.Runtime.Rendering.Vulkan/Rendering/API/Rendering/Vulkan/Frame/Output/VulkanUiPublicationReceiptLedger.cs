using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded ownership for UI draw packets. A receipt is not reusable while a
/// queued request, recorded command artifact, or native submission can still
/// refer to its GPU cohorts.
/// </summary>
internal static class VulkanUiPublicationReceiptLedger
{
    private const int Capacity = 512;
    private static readonly object Gate = new();
    private static readonly Entry?[] Entries = new Entry[Capacity];

    private sealed class Entry(VulkanRenderer owner, ulong id, UIPublication publication, int expected,
        bool requiresSwapchainPresentation)
    {
        internal readonly VulkanRenderer Owner = owner;
        internal readonly long BackendGeneration = owner.BackendGeneration;
        internal readonly ulong Id = id;
        internal readonly UIPublication Publication = RetainPublication(publication);
        internal readonly UIPublicationReceiptState State =
            new(expected, requiresSwapchainPresentation);
        internal readonly ulong FrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        internal XRGpuFence? Fence;

        private static UIPublication RetainPublication(UIPublication publication)
        {
            publication.Retain();
            return publication;
        }
    }

    internal static bool TryReserve(VulkanRenderer owner, ulong id,
        UIPublication publication, int expected,
        bool requiresSwapchainPresentation)
    {
        if (id == 0UL || expected < 0)
            return false;
        lock (Gate)
        {
            for (int index = 0; index < Entries.Length; index++)
                if (Entries[index] is null)
                {
                    Entries[index] = new Entry(owner, id, publication, expected,
                        requiresSwapchainPresentation);
                    return true;
                }
        }
        return false;
    }

    internal static void Fail(ulong id)
    {
        lock (Gate)
            if (Find(id) is { } entry)
                entry.State.Fail();
    }

    internal static bool RecordQueued(ulong id)
    {
        lock (Gate)
        {
            return Find(id)?.State.TryQueueDraw() == true;
        }
    }

    internal static bool Seal(ulong id, bool authoringSucceeded, XRGpuFence? fence)
    {
        lock (Gate)
        {
            if (Find(id) is not { } entry)
                return false;
            bool sealedComplete = entry.State.TrySeal(authoringSucceeded);
            entry.Fence = fence;
            return sealedComplete;
        }
    }

    internal static void RetireFailedAuthoring(ulong id)
    {
        lock (Gate)
        {
            if (Find(id) is not { } entry)
                return;
            entry.State.Fail();
            if (!entry.State.Claimed)
                Remove(id);
        }
    }

    internal static bool TryAttestPrepared(ulong id, int preparedDraws)
    {
        lock (Gate)
        {
            return Find(id)?.State.TryAttestPrepared(preparedDraws) == true;
        }
    }

    /// <summary>
    /// Removes the sealed receipts of earlier frames that no frame plan claimed. A plan claims only
    /// the receipts of its own frame, so such a receipt can never be recorded: its frame was declined
    /// or deferred before planning. Its GPU cohorts were not submitted, so they can be reused.
    /// Without this removal the canvas cohorts stay reserved and the canvas never draws again.
    /// </summary>
    /// <param name="keptReceiptId">A receipt that another path owns, such as a required world UI attempt.</param>
    /// <returns>The number of removed receipts.</returns>
    internal static int RetireUnclaimedBefore(VulkanRenderer owner, ulong frameId, ulong keptReceiptId)
    {
        Span<ulong> candidates = stackalloc ulong[Capacity];
        int candidateCount = 0;
        lock (Gate)
            for (int index = 0; index < Entries.Length; index++)
                if (Entries[index] is { } entry && IsUnclaimedBefore(entry, owner, frameId, keptReceiptId))
                    candidates[candidateCount++] = entry.Id;
        if (candidateCount == 0)
            return 0;

        // The queues take their own locks, so they are checked outside the ledger gate.
        for (int index = 0; index < candidateCount; index++)
            if (owner.HasPendingUiPublicationWork(candidates[index]))
                candidates[index] = 0UL;

        int removed = 0;
        List<UIPublication>? detached = null;
        lock (Gate)
            for (int index = 0; index < Entries.Length; index++)
            {
                // Recheck: a plan can claim the receipt between the two gates.
                if (Entries[index] is not { } entry || !IsUnclaimedBefore(entry, owner, frameId, keptReceiptId) ||
                    !candidates[..candidateCount].Contains(entry.Id))
                    continue;
                entry.State.Fail();
                if (entry.Fence is VulkanTimelineGpuFence fence)
                    fence.Fail(EGpuFenceFailureSite.OutputCohortIncomplete,
                        EGpuFenceNativeSubmission.NotCalled);
                Entries[index] = null;
                (detached ??= []).Add(entry.Publication);
                removed++;
            }
        if (detached is not null)
            for (int index = 0; index < detached.Count; index++)
                detached[index].Release();
        return removed;
    }

    private static bool IsUnclaimedBefore(Entry entry, VulkanRenderer owner, ulong frameId, ulong keptReceiptId)
        => ReferenceEquals(entry.Owner, owner) && entry.FrameId < frameId && entry.Id != keptReceiptId &&
            entry.State.Sealed && !entry.State.Claimed;

    /// <summary>Claims one sealed receipt whose draws an accepted plan holds.</summary>
    internal static bool TryClaimSealed(VulkanRenderer owner, ulong id)
    {
        lock (Gate)
            return Find(id) is { } entry && ReferenceEquals(entry.Owner, owner) &&
                entry.State.TryClaim();
    }

    internal static bool TryGetSealed(ulong id, out int expected,
        out bool requiresSwapchainPresentation, out XRGpuFence? fence)
    {
        lock (Gate)
        {
            if (Find(id) is { } entry &&
                entry.State is { Sealed: true, Failed: false })
            {
                expected = entry.State.ExpectedDraws;
                requiresSwapchainPresentation = entry.State.RequiresPresentation;
                fence = entry.Fence;
                return true;
            }
        }
        expected = 0;
        requiresSwapchainPresentation = false;
        fence = null;
        return false;
    }

    internal static bool TryAttestRecorded(ulong id, int recordedDraws)
    {
        lock (Gate)
        {
            return Find(id)?.State.TryAttestRecorded(recordedDraws) == true;
        }
    }

    internal static bool TryAttachSubmission(ulong id, uint imageIndex,
        ulong outputGeneration, ulong sourceFrameId)
    {
        lock (Gate)
        {
            return Find(id) is { } entry &&
                entry.State.TryAttachSubmission(imageIndex, outputGeneration,
                    sourceFrameId);
        }
    }

    internal static bool TryPresent(ulong id, uint imageIndex, ulong outputGeneration,
        ulong sourceFrameId)
    {
        lock (Gate)
        {
            return Find(id) is { } entry &&
                entry.State.TryPresent(imageIndex, outputGeneration, sourceFrameId);
        }
    }

    internal static bool TryAcceptReplay(VulkanRenderer owner, ulong id,
        uint sourceImageIndex, ulong sourceOutputGeneration,
        uint targetImageIndex, ulong targetOutputGeneration, ulong sourceFrameId)
    {
        lock (Gate)
        {
            if (Find(id) is not { } entry || !ReferenceEquals(entry.Owner, owner) ||
                !entry.State.TryAttachReplay(sourceImageIndex, sourceOutputGeneration,
                    sourceFrameId, targetImageIndex, targetOutputGeneration))
                return false;
            return entry.State.TryPresent(targetImageIndex, targetOutputGeneration,
                sourceFrameId);
        }
    }

    internal static void InvalidateImage(VulkanRenderer owner, uint imageIndex,
        ulong outputGeneration)
    {
        lock (Gate)
            for (int index = 0; index < Entries.Length; index++)
                if (Entries[index] is { } entry && ReferenceEquals(entry.Owner, owner))
                    entry.State.InvalidateImage(imageIndex, outputGeneration);
    }

    internal static void RetireArtifact(ulong id)
    {
        lock (Gate)
            if (Find(id) is { } entry)
                entry.State.RetireArtifact();
    }

    internal static bool MarkArtifactReferenced(ulong id)
    {
        lock (Gate)
        {
            return Find(id)?.State.TryReferenceArtifact() == true;
        }
    }

    /// <summary>
    /// Returns the claim of a plan that reset before it recorded the receipt. The receipt stays
    /// sealed, because readiness can keep its prepared draws and bind them in a later plan. If no
    /// later plan holds its draws, <see cref="RetireUnclaimedBefore"/> releases it.
    /// </summary>
    internal static void ReleaseUnrecordedPlanClaim(ulong id)
    {
        lock (Gate)
        {
            if (Find(id) is not { } entry ||
                !entry.State.CanReleaseUnrecordedClaim)
                return;
            entry.State.ReleaseClaim();
        }
    }

    internal static bool IsRetired(ulong id)
    {
        lock (Gate)
        {
            Entry? entry = Find(id);
            if (entry is null)
                return true;
            bool nativeUseFinished = entry.Fence is not { } fence ||
                fence.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted &&
                fence.Poll() == EGpuFenceStatus.Signaled ||
                fence.SubmissionStatus == EGpuFenceSubmissionStatus.Failed &&
                fence.FirstFailureDiagnostic?.NativeSubmission ==
                    EGpuFenceNativeSubmission.NotCalled;
            if (!entry.State.CanRetire(nativeUseFinished))
                return false;
            Remove(id);
            return true;
        }
    }

    /// <summary>Release one renderer generation after native artifacts and GPU use end.</summary>
    internal static void RetireKnownIdle(VulkanRenderer owner, long backendGeneration)
    {
        List<UIPublication> detached = [];
        lock (Gate)
            for (int index = 0; index < Entries.Length; index++)
                if (Entries[index] is { } entry && ReferenceEquals(entry.Owner, owner) &&
                    entry.BackendGeneration == backendGeneration)
                {
                    Entries[index] = null;
                    detached.Add(entry.Publication);
                }
        for (int index = 0; index < detached.Count; index++)
            detached[index].Release();
    }

    private static Entry? Find(ulong id)
    {
        for (int index = 0; index < Entries.Length; index++)
            if (Entries[index]?.Id == id)
                return Entries[index];
        return null;
    }

    private static void Remove(ulong id)
    {
        for (int index = 0; index < Entries.Length; index++)
            if (Entries[index]?.Id == id)
            {
                Entry entry = Entries[index]!;
                Entries[index] = null;
                entry.Publication.Release();
                return;
            }
    }
}
