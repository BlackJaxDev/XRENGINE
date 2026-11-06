using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    // Publications that a committed picking source may lag behind the newest
    // candidate. It stays well inside the table journal capacity.
    private const ulong MaxPickingSourcePublicationLag = 16UL;
    private readonly object _advancedPickingSync = new();
    private ulong _advancedPickingGeneration;
    private AdvancedPickingRequest? _pendingAdvancedPickingRequest;
    private AdvancedGpuScenePublicationLease _advancedPickingSourceLease;
    private int _advancedPickingSourceResourceGeneration = -1;
    private long _advancedPickingSourcePackageGeneration = -1L;
    private AdvancedGpuScenePublicationLease _advancedPickingCandidateLease;
    private int _advancedPickingCandidateResourceGeneration = -1;
    private long _advancedPickingCandidatePackageGeneration = -1L;

    /// <summary>
    /// Queues a nonblocking integer readback against the immutable canonical publication
    /// currently paired with this pipeline's visibility resources.
    /// </summary>
    public bool TryQueueAdvancedPicking(
        AbstractRenderer renderer,
        in AdvancedPickingQuery query,
        Action<AdvancedPickingResult> completed,
        out AdvancedPickingRequest? request,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(completed);
        request = null;
        failure = null;

        if (!RuntimeEngine.IsRenderThread)
        {
            failure = "Advanced picking must be queued on the render thread so its visibility resources and canonical publication remain paired.";
            return false;
        }

        if (AdvancedOutputBinding.Request.Purpose ==
            XREngine.Data.Rendering.ERenderPipelinePurpose.OpenXrEye)
        {
            failure = "Advanced OpenXR picking requires an accepted XR visibility-source receipt and is not available yet.";
            return false;
        }

        if (!TryGetTexture(
                AdvancedVisibilityResourceNames.Identity,
                out XRTexture? identity) ||
            identity is null ||
            !TryGetTexture(
                AdvancedVisibilityResourceNames.Metadata,
                out XRTexture? metadata) ||
            metadata is null ||
            !TryGetTexture(
                AdvancedVisibilityResourceNames.Selection,
                out XRTexture? selection) ||
            selection is null)
        {
            failure = "The active pipeline generation has no complete Advanced visibility identity attachment set.";
            return false;
        }

        AdvancedSharedGpuSceneDatabase? database;
        AdvancedGpuScenePublicationReference publication;
        long packageGeneration;
        lock (_advancedPickingSync)
        {
            database = _advancedPickingSourceLease.Database;
            publication = _advancedPickingSourceLease.Reference;
            packageGeneration = _advancedPickingSourcePackageGeneration;
            if (_advancedPickingSourceResourceGeneration != ResourceGeneration)
                database = null;
        }
        if (database is null || !publication.IsValid || publication.Snapshot is null ||
            !database.TryAcquirePublicationLease(
                publication,
                EAdvancedGpuScenePublicationPinKind.Gpu,
                out AdvancedGpuScenePublicationLease lease))
        {
            failure = "The visibility attachments have no matching retained canonical publication for generation-safe picking.";
            return false;
        }

        AdvancedPickingRequest? superseded;
        AdvancedPickingRequest newRequest;
        ulong generation;
        lock (_advancedPickingSync)
        {
            generation = NextAdvancedPickingGeneration();
            superseded = _pendingAdvancedPickingRequest;
            newRequest = new AdvancedPickingRequest(
                generation,
                query,
                InstanceId,
                ResourceGeneration,
                packageGeneration,
                lease,
                completed);
            request = newRequest;
            _pendingAdvancedPickingRequest = newRequest;
        }
        superseded?.TrySupersede();

        if (renderer.TryQueueAdvancedPickingReadback(
                identity,
                metadata,
                selection,
                query,
                encoded => CompleteAdvancedPicking(newRequest, in encoded),
                out failure))
        {
            return true;
        }

        lock (_advancedPickingSync)
        {
            if (ReferenceEquals(_pendingAdvancedPickingRequest, newRequest))
                _pendingAdvancedPickingRequest = null;
        }
        newRequest.TrySupersede();
        request = null;
        failure ??= "The active renderer rejected the asynchronous Advanced picking readback.";
        return false;
    }

    private void CompleteAdvancedPicking(
        AdvancedPickingRequest request,
        in AdvancedVisibilityEncodedSurface encoded)
    {
        lock (_advancedPickingSync)
        {
            if (!ReferenceEquals(_pendingAdvancedPickingRequest, request) ||
                !request.IsPending ||
                request.PipelineInstanceId != InstanceId)
            {
                request.TrySupersede();
                return;
            }

            _pendingAdvancedPickingRequest = null;
        }

        if (!request.TryBeginDelivery(
                out AdvancedGpuScenePublicationSnapshot? snapshot))
            return;

        AdvancedPickingResult result;
        bool matchesQueryView = !encoded.IsValid ||
            encoded.Metadata.Decode().ViewIndex == request.Query.ViewIndex;
        if (!matchesQueryView ||
            snapshot is null ||
            snapshot.DatabaseEpoch != request.DatabaseEpoch ||
            snapshot.Draws.Sequence != request.PublicationSequence ||
            !AdvancedPickingResolver.TryResolve(
                encoded,
                snapshot,
                request.Generation,
                out result))
        {
            result = AdvancedPickingResult.Miss(
                request.Generation,
                request.DatabaseEpoch,
                request.PublicationSequence,
                request.Query.ViewIndex);
        }

        request.CompleteDelivery(result);
    }

    private ulong NextAdvancedPickingGeneration()
    {
        ++_advancedPickingGeneration;
        if (_advancedPickingGeneration == 0u)
            ++_advancedPickingGeneration;
        return _advancedPickingGeneration;
    }

    private void PublishAdvancedPickingSource(
        BackendReadyFramePackage package,
        bool requiresSubmissionAcceptance)
    {
        // Shadow and compatibility pipelines can consume a canonical scene but
        // never produce these picking attachments. An idle shadow map must not
        // retain a GPU picking pin and block later scene publications.
        if (Pipeline is not AdvancedRenderPipeline)
            return;
        if (package.State != EBackendReadyFramePackageState.Published ||
            !package.TryGetCanonicalPublication(
                out AdvancedSharedGpuSceneDatabase database,
                out AdvancedGpuScenePublicationReference publication) ||
            publication.Snapshot is null ||
            !database.TryAcquirePublicationLease(
                publication,
                EAdvancedGpuScenePublicationPinKind.Gpu,
                out AdvancedGpuScenePublicationLease replacement))
        {
            return;
        }

        AdvancedGpuScenePublicationLease previous;
        AdvancedGpuScenePublicationLease staleSource = default;
        lock (_advancedPickingSync)
        {
            if (requiresSubmissionAcceptance)
            {
                previous = _advancedPickingCandidateLease;
                // The source moves forward only when a frame commits. While frames
                // keep failing, it would pin one old publication forever, and the
                // database could not reclaim its table journals for any later
                // publication. Drop a source that lags far behind; picking is then
                // unavailable until the next commit.
                if (_advancedPickingSourceLease.IsValid &&
                    replacement.Reference.Sequence >
                        _advancedPickingSourceLease.Reference.Sequence + MaxPickingSourcePublicationLag)
                {
                    staleSource = _advancedPickingSourceLease;
                    _advancedPickingSourceLease = default;
                    _advancedPickingSourceResourceGeneration = -1;
                    _advancedPickingSourcePackageGeneration = -1L;
                }
                _advancedPickingCandidateLease = replacement;
                _advancedPickingCandidateResourceGeneration = ResourceGeneration;
                _advancedPickingCandidatePackageGeneration = package.PackageGeneration;
            }
            else
            {
                previous = _advancedPickingSourceLease;
                _advancedPickingSourceLease = replacement;
                _advancedPickingSourceResourceGeneration = ResourceGeneration;
                _advancedPickingSourcePackageGeneration = package.PackageGeneration;
            }
        }
        previous.Dispose();
        staleSource.Dispose();
    }

    /// <summary>
    /// Drops picking leases that lag too far behind the newest sealed publication.
    /// A viewport whose scene is not due, for example a desktop view with the VR
    /// mirror off, never reaches <see cref="PublishAdvancedPickingSource"/>. Its source
    /// and candidate leases would then pin one old publication forever, the table
    /// journals could not reclaim it, and the database would reject every later
    /// publication. Picking is unavailable until the pipeline renders and commits again.
    /// </summary>
    internal void ReleaseLaggingAdvancedPickingRetention()
    {
        AdvancedGpuScenePublicationLease source;
        AdvancedGpuScenePublicationLease candidate;
        lock (_advancedPickingSync)
        {
            source = _advancedPickingSourceLease;
            candidate = _advancedPickingCandidateLease;
        }
        bool dropSource = IsLaggingPickingLease(in source);
        bool dropCandidate = IsLaggingPickingLease(in candidate);
        if (!dropSource && !dropCandidate)
            return;

        AdvancedGpuScenePublicationLease droppedSource = default;
        AdvancedGpuScenePublicationLease droppedCandidate = default;
        lock (_advancedPickingSync)
        {
            // Drop only the lease that was checked. A render may have replaced it since.
            if (dropSource && _advancedPickingSourceLease.IsSameLease(in source))
            {
                droppedSource = _advancedPickingSourceLease;
                _advancedPickingSourceLease = default;
                _advancedPickingSourceResourceGeneration = -1;
                _advancedPickingSourcePackageGeneration = -1L;
            }
            if (dropCandidate && _advancedPickingCandidateLease.IsSameLease(in candidate))
            {
                droppedCandidate = _advancedPickingCandidateLease;
                _advancedPickingCandidateLease = default;
                _advancedPickingCandidateResourceGeneration = -1;
                _advancedPickingCandidatePackageGeneration = -1L;
            }
        }
        droppedSource.Dispose();
        droppedCandidate.Dispose();
    }

    private static bool IsLaggingPickingLease(in AdvancedGpuScenePublicationLease lease)
        => lease.IsValid &&
           lease.Database is { } database &&
           database.LastSealedPublicationSequence >
               lease.Reference.Sequence + MaxPickingSourcePublicationLag;

    /// <summary>Ends the source-only picking retention for a settled one-shot
    /// capture. Already queued picks retain their own immutable publication.</summary>
    internal void ReleaseCompletedCapturePickingSource(long packageGeneration)
    {
        AdvancedGpuScenePublicationLease source = default;
        AdvancedGpuScenePublicationLease candidate = default;
        lock (_advancedPickingSync)
        {
            if (_advancedPickingSourcePackageGeneration == packageGeneration)
            {
                source = _advancedPickingSourceLease;
                _advancedPickingSourceLease = default;
                _advancedPickingSourcePackageGeneration = -1;
                _advancedPickingSourceResourceGeneration = -1;
            }
            if (_advancedPickingCandidatePackageGeneration == packageGeneration)
            {
                candidate = _advancedPickingCandidateLease;
                _advancedPickingCandidateLease = default;
                _advancedPickingCandidatePackageGeneration = -1;
                _advancedPickingCandidateResourceGeneration = -1;
            }
        }
        source.Dispose();
        candidate.Dispose();
    }

    /// <summary>
    /// Promotes only the canonical publication whose visibility raster was actually
    /// recorded into a Vulkan primary that the native queue accepted.
    /// </summary>
    internal void CommitAdvancedPickingSource(
        in AdvancedGpuScenePublication publication,
        ulong resourceGeneration)
    {
        AdvancedGpuScenePublicationLease previous = default;
        lock (_advancedPickingSync)
        {
            if (!_advancedPickingCandidateLease.IsValid ||
                _advancedPickingCandidateLease.Reference.Publication != publication ||
                resourceGeneration != unchecked((ulong)_advancedPickingCandidateResourceGeneration))
            {
                return;
            }

            previous = _advancedPickingSourceLease;
            _advancedPickingSourceLease = _advancedPickingCandidateLease;
            _advancedPickingSourceResourceGeneration =
                _advancedPickingCandidateResourceGeneration;
            _advancedPickingSourcePackageGeneration =
                _advancedPickingCandidatePackageGeneration;
            _advancedPickingCandidateLease = default;
            _advancedPickingCandidateResourceGeneration = -1;
            _advancedPickingCandidatePackageGeneration = -1L;
        }
        previous.Dispose();
    }

    private void CancelPendingAdvancedPicking()
    {
        AdvancedPickingRequest? pending;
        AdvancedGpuScenePublicationLease sourceLease;
        AdvancedGpuScenePublicationLease candidateLease;
        lock (_advancedPickingSync)
        {
            pending = _pendingAdvancedPickingRequest;
            _pendingAdvancedPickingRequest = null;
            sourceLease = _advancedPickingSourceLease;
            candidateLease = _advancedPickingCandidateLease;
            _advancedPickingSourceLease = default;
            _advancedPickingCandidateLease = default;
            _advancedPickingSourceResourceGeneration = -1;
            _advancedPickingSourcePackageGeneration = -1L;
            _advancedPickingCandidateResourceGeneration = -1;
            _advancedPickingCandidatePackageGeneration = -1L;
        }
        sourceLease.Dispose();
        candidateLease.Dispose();
        pending?.TrySupersede();
    }
}
