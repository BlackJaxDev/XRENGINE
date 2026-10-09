namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private readonly PhysicsChainSimulationReceipt[] _simulationReceipts = [new(), new(), new(), new()];

    private bool TryBeginSimulationReceipt(out PhysicsChainSimulationReceipt receipt)
    {
        for (int index = 0; index < _simulationReceipts.Length; ++index)
        {
            receipt = _simulationReceipts[index];
            if (receipt.InUse)
                continue;
            receipt.InUse = true;
            receipt.Entries.Clear();
            receipt.Entries.EnsureCapacity(_activeRequests.Count);
            receipt.PriorProducerEpoch = _publishedOutputPageIndex >= 0
                ? _outputPages[_publishedOutputPageIndex].ProducerEpoch : 0u;
            return true;
        }
        receipt = null!;
        return false;
    }

    private static void RecordSimulationReceipt(PhysicsChainSimulationReceipt receipt,
        GPUPhysicsChainRequest request)
    {
        bool hasTightCheckpoint = request.ResidentSolveCompleted || !request.ResidentSpatialValid;
        receipt.Entries.Add(new(request, request.AcceptedInput.Sequence, request.ResidentSpatialRevision,
            hasTightCheckpoint
                ? new(request.ResidentTightParticleBounds, request.ResidentTightBasisStretch)
                : new(request.ResidentParticleBounds, request.ResidentBasisStretch),
            hasTightCheckpoint ? request.ResidentTightSpatialValid : request.ResidentSpatialValid));
    }

    private void SealSimulationReceipt(IPhysicsChainComputeBackend backend, PhysicsChainSimulationReceipt receipt)
    {
        if (receipt.Entries.Count == 0)
        {
            ReleaseSimulationReceipt(receipt);
            return;
        }
        lock (_outputPageSync)
        {
            if (_publishedOutputPageIndex >= 0)
            {
                PhysicsChainOutputPage page = _outputPages[_publishedOutputPageIndex];
                if (page.ProducerEpoch > receipt.PriorProducerEpoch && page.ProducerFence is not null &&
                    TryRetainOutputPageLocked(_publishedOutputPageIndex,
                        MakeOutputPageToken(_publishedOutputPageIndex), out var lease))
                {
                    receipt.OutputPage = lease;
                    receipt.Fence = page.ProducerFence;
                    return;
                }
            }
        }

        // Output admission can fail after the solve is queued. Its native outcome still needs a marker.
        try
        {
            receipt.Fence = backend.InsertFence();
            receipt.OwnsFence = receipt.Fence is not null;
        }
        catch (Exception exception)
        {
            XREngine.Debug.LogException(exception);
        }
        if (receipt.Fence is not null)
            return;
        RetrySimulationReceipt(receipt);
        ReleaseSimulationReceipt(receipt);
        RecordDispatchFailure("SimulationReceiptFence");
    }

    private void PollSimulationReceipts()
    {
        for (int index = 0; index < _simulationReceipts.Length; ++index)
        {
            PhysicsChainSimulationReceipt receipt = _simulationReceipts[index];
            if (!receipt.InUse || receipt.Fence is not { } fence)
                continue;
            bool failed = fence.IsDisposed || fence.SubmissionStatus == EGpuFenceSubmissionStatus.Failed ||
                AbstractRenderer.Current is { AcceptsBackendWork: false } ||
                AbstractRenderer.Current is IRuntimeRendererHost { IsDeviceLost: true };
            EGpuFenceStatus status = failed ? EGpuFenceStatus.Failed :
                fence.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted ? fence.Poll() : EGpuFenceStatus.Pending;
            if (status == EGpuFenceStatus.Pending)
                continue;
            if (status == EGpuFenceStatus.Failed)
            {
                RetrySimulationReceipt(receipt);
                if (receipt.OutputPage.Token.IsValid)
                {
                    lock (_outputPageSync)
                        if (TryGetOutputPageLocked(checked((int)receipt.OutputPage.Token.PageIndexPlusOne - 1),
                            receipt.OutputPage.Token, out var page))
                            page!.KnownProducerFailure = true;
                }
            }
            else
            {
                for (int entryIndex = 0; entryIndex < receipt.Entries.Count; ++entryIndex)
                {
                    PhysicsChainSimulationReceiptEntry entry = receipt.Entries[entryIndex];
                    GPUPhysicsChainRequest request = entry.Request;
                    if (request.ResidentSpatialRevision != entry.SpatialRevision)
                        continue;
                    _paletteOutputRefreshPending |= request.ResidentSpatialValid != entry.TightSpatialStateValid ||
                        request.ResidentParticleBounds.Min != entry.TightSpatialState.ParticleBounds.Min ||
                        request.ResidentParticleBounds.Max != entry.TightSpatialState.ParticleBounds.Max ||
                        request.ResidentBasisStretch != entry.TightSpatialState.MaximumBasisStretch;
                    request.ResidentParticleBounds = entry.TightSpatialState.ParticleBounds;
                    request.ResidentBasisStretch = entry.TightSpatialState.MaximumBasisStretch;
                    request.ResidentSpatialValid = entry.TightSpatialStateValid;
                }
            }
            ReleaseSimulationReceipt(receipt);
        }
    }

    private void RetrySimulationReceipt(PhysicsChainSimulationReceipt receipt)
    {
        for (int index = 0; index < receipt.Entries.Count; ++index)
            receipt.Entries[index].Request.RetryAcceptedInput();
        _paletteOutputRefreshPending = true;
    }

    private void ReleaseSimulationReceipt(PhysicsChainSimulationReceipt receipt)
    {
        if (receipt.OutputPage.Token.IsValid)
            ReleaseOutputPage(receipt.OutputPage.Token);
        else if (receipt.OwnsFence)
            receipt.Fence?.Dispose();
        receipt.Fence = null;
        receipt.OutputPage = default;
        receipt.OwnsFence = false;
        receipt.Entries.Clear();
        receipt.InUse = false;
    }

    private void RetireSimulationReceipts()
    {
        for (int index = 0; index < _simulationReceipts.Length; ++index)
        {
            PhysicsChainSimulationReceipt receipt = _simulationReceipts[index];
            if (!receipt.InUse)
                continue;
            RetrySimulationReceipt(receipt);
            ReleaseSimulationReceipt(receipt);
        }
    }
}
