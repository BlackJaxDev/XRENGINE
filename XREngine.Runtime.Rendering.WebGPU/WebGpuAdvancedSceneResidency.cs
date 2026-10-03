using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>
/// Backend residency for the single canonical database. Bounded slots retain GPU
/// publication leases until an accepted queue prefix completes; they never wait.
/// </summary>
internal sealed class WebGpuAdvancedSceneResidency : IDisposable
{
    private readonly WebGpuAdvancedSceneSlot[] _slots;
    private int _nextSlot;

    internal WebGpuAdvancedSceneResidency(WebGpuRendererHost renderer)
    {
        _slots = new WebGpuAdvancedSceneSlot[AdvancedFrameSlotContract.DefaultSlotCount];
        for (int index = 0; index < _slots.Length; index++)
            _slots[index] = new(renderer, index);
    }

    internal void Reclaim(uint completedSequence)
    {
        for (int index = 0; index < _slots.Length; index++) _slots[index].Reclaim(completedSequence);
    }

    internal bool TryAcquire(BackendReadyFramePackage package, uint frameSequence,
        out WebGpuAdvancedSceneSlot? slot, out string reason)
    {
        slot = null;
        if (package.State != EBackendReadyFramePackageState.Published ||
            !package.TryGetCanonicalPublication(out AdvancedSharedGpuSceneDatabase database,
                out AdvancedGpuScenePublicationReference reference))
        {
            reason = "WebGPU.Advanced.PublicationMissing: the immutable backend package has no retained canonical database publication.";
            return false;
        }
        int recordedPublications = 0;
        for (int index = 0; index < _slots.Length; index++)
        {
            WebGpuAdvancedSceneSlot candidate = _slots[index];
            if (candidate.RecordingSequence == frameSequence) recordedPublications++;
            if (candidate.RecordingSequence == frameSequence && candidate.Publication == reference.Publication)
            {
                slot = candidate;
                reason = string.Empty;
                return true;
            }
        }
        if (recordedPublications == _slots.Length)
            throw new NotSupportedException("WebGPU.Advanced.PublicationCapacity: an atomic frame exceeds three distinct canonical scene publications; no queue completion can reclaim its unsubmitted work.");
        WebGpuAdvancedSceneSlot selected = _slots[_nextSlot];
        if (!selected.IsAvailable)
        {
            reason = "WebGPU.Advanced.SlotPending: the next canonical scene slot is still recorded or GPU-owned; retry after queue completion.";
            return false;
        }
        if (reference.DatabaseEpoch != database.DatabaseEpoch ||
            !database.TryAcquirePublicationLease(reference, EAdvancedGpuScenePublicationPinKind.Gpu, out AdvancedGpuScenePublicationLease lease))
        {
            reason = "WebGPU.Advanced.LeaseUnavailable: the exact canonical publication cannot acquire a GPU lease.";
            return false;
        }
        bool committed = false;
        try
        {
            if (!database.TryGetPublicationSnapshot(reference, out AdvancedGpuScenePublicationSnapshot snapshot) ||
                snapshot.DatabaseEpoch != reference.DatabaseEpoch || !HasExactSequence(snapshot, reference.Sequence))
            {
                reason = "WebGPU.Advanced.PublicationMismatch: canonical scene, geometry, material, and global records must share one retained publication.";
                return false;
            }
            selected.Prepare(lease, snapshot, frameSequence);
            _nextSlot = (_nextSlot + 1) % _slots.Length;
            slot = selected;
            committed = true;
            reason = string.Empty;
            return true;
        }
        finally { if (!committed) lease.Dispose(); }
    }

    internal void EndRecording(uint frameSequence, bool submitted)
    {
        for (int index = 0; index < _slots.Length; index++) _slots[index].EndRecording(frameSequence, submitted);
    }

    private static bool HasExactSequence(AdvancedGpuScenePublicationSnapshot snapshot, ulong sequence)
        => sequence != 0 && snapshot.Draws.Sequence == sequence && snapshot.Instances.Sequence == sequence &&
           snapshot.Transforms.Sequence == sequence && snapshot.Deformations.Sequence == sequence &&
           snapshot.RenderStates.Sequence == sequence && snapshot.EditorIdentities.Sequence == sequence &&
           snapshot.Geometry.Sequence == sequence && snapshot.Materials.Sequence == sequence &&
           snapshot.Kernels.Sequence == sequence && snapshot.Layouts.Sequence == sequence &&
           snapshot.MaterialPayloads.Sequence == sequence && snapshot.ResourcePayloads.Sequence == sequence &&
           snapshot.GlobalResources.Sequence == sequence;

    public void Dispose()
    {
        for (int index = 0; index < _slots.Length; index++) _slots[index].Dispose();
    }
}
