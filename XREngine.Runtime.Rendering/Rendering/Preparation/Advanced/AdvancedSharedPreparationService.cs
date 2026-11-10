using System.Diagnostics;
using System.Runtime.CompilerServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Process-local pipeline-neutral preparation owner. The first desktop or eye
/// consumer builds a world frame; all later consumers of the same immutable
/// snapshot acquire the identical publication.
/// </summary>
public sealed class AdvancedSharedPreparationService : IDisposable
{
    private static readonly Lazy<AdvancedSharedPreparationService> Shared =
        new(static () => new AdvancedSharedPreparationService(
            AdvancedPreparationOptions.Default));

    private readonly object _sync = new();
    private AdvancedPreparationExtractor _extractor;
    private GPUScene? _publishedScene;
    private AdvancedGpuScenePublication _publishedSource;
    private AdvancedGpuScenePublicationLease _publicationLease;
    private AdvancedPreparationPublication _publication;
    private long _acquireCount;
    private long _cacheHitCount;
    private long _rebuildCount;
    private long _acquireLockWaitTicks;
    private long _maximumAcquireLockWaitTicks;
    private long _buildTicks;
    private long _extractionTicks;
    private long _indirectPlanningTicks;
    private long _initialViewPlanningTicks;
    private long _cacheHitViewCheckTicks;
    private long _deformationTicks;
    private long _copyCount;
    private long _copyFailureCount;
    private long _copyLockWaitTicks;
    private long _maximumCopyLockWaitTicks;
    private long _copyTicks;
    private long _maximumCopyTicks;
    private long _copiedBytes;
    private long _buildAllocatedBytes;

    public AdvancedSharedPreparationService(AdvancedPreparationOptions options)
        => _extractor = new AdvancedPreparationExtractor(options);

    public static AdvancedSharedPreparationService Instance => Shared.Value;

    public AdvancedPreparationExtractor Extractor => _extractor;

    /// <summary>
    /// Discards device-owned preparation state after every renderer using the
    /// retired device has detached. The next world frame rebuilds its logical
    /// preparation from the canonical scene publication.
    /// </summary>
    public static void ResetAfterRendererRetirement()
    {
        if (!Shared.IsValueCreated)
            return;

        AdvancedSharedPreparationService service = Shared.Value;
        lock (service._sync)
        {
            AdvancedPreparationExtractor retired = service._extractor;
            AdvancedPreparationExtractor replacement = new(retired.Options);
            retired.Dispose();
            service._publicationLease.Dispose();
            service._publicationLease = default;
            service._extractor = replacement;
            service._publishedScene = null;
            service._publishedSource = default;
            service._publication = default;
        }
    }

    /// <summary>Reads existing preparation diagnostics without initializing an unused renderer.</summary>
    public static AdvancedPreparationDiagnosticSnapshot? GetCurrentDiagnostics()
    {
        if (!Shared.IsValueCreated)
            return null;
        AdvancedSharedPreparationService service = Shared.Value;
        lock (service._sync)
            return new(service._publication, service._extractor.LastDeferralReason,
                service._extractor.GpuDeformation.LastOutputReuseStatus.ToString(),
                service._extractor.GpuDeformation.LastOutputReuseSlot,
                service._extractor.GpuDeformation.LastOutputReuseAuthority,
                service.GetTelemetryLocked(), service._extractor.ArenaTelemetry,
                service._extractor.FrameUploadTelemetry,
                service._extractor.VisibilityContentTelemetry)
            {
                DeformationSourceGeneration = service._extractor.DeformationSourceGeneration,
                DeformationSourceMeshCount = service._extractor.DeformationSourceMeshCount,
                DeformationSourceChanges = service._extractor.DeformationSourceChanges,
                DeformationSourceChangedMesh = service._extractor.DeformationSourceChangedMesh,
            };
    }

    private AdvancedSharedPreparationTelemetry GetTelemetryLocked() => new(
        _acquireCount, _cacheHitCount, _rebuildCount,
        _acquireLockWaitTicks, _maximumAcquireLockWaitTicks,
        _buildTicks, _extractionTicks,
        _indirectPlanningTicks, _initialViewPlanningTicks,
        _cacheHitViewCheckTicks,
        _deformationTicks, _copyCount, _copyFailureCount,
        _copyLockWaitTicks, _maximumCopyLockWaitTicks,
        _copyTicks, _maximumCopyTicks, _copiedBytes,
        _buildAllocatedBytes, Stopwatch.Frequency);

    public AdvancedPreparationPublication Acquire(
        in RenderWorldSnapshot world,
        RenderFrameViewSet? viewSet,
        EAdvancedPreparationConsumer consumers)
        => AcquireCore(world, viewSet, consumers,
            RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(), null);

    public AdvancedPreparationPublication Acquire(
        in RenderWorldSnapshot world,
        RenderFrameViewSet? viewSet,
        EAdvancedPreparationConsumer consumers,
        EMeshSubmissionStrategy submissionStrategy)
        => AcquireCore(world, viewSet, consumers, submissionStrategy, null);

    /// <summary>Acquires preparation from the exact scene publication retained by the active frame package.</summary>
    public AdvancedPreparationPublication Acquire(
        in RenderWorldSnapshot world,
        RenderFrameViewSet? viewSet,
        EAdvancedPreparationConsumer consumers,
        BackendReadyFramePackage package,
        EMeshSubmissionStrategy submissionStrategy)
    {
        ArgumentNullException.ThrowIfNull(package);
        return AcquireCore(world, viewSet, consumers, submissionStrategy, package);
    }

    private AdvancedPreparationPublication AcquireCore(
        in RenderWorldSnapshot world,
        RenderFrameViewSet? viewSet,
        EAdvancedPreparationConsumer consumers,
        EMeshSubmissionStrategy submissionStrategy,
        BackendReadyFramePackage? package)
    {
        long waitStarted = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            long acquireLockWaitTicks = Stopwatch.GetTimestamp() - waitStarted;
            _acquireLockWaitTicks += acquireLockWaitTicks;
            _maximumAcquireLockWaitTicks = Math.Max(
                _maximumAcquireLockWaitTicks,
                acquireLockWaitTicks);
            _acquireCount++;
            AdvancedSharedGpuSceneDatabase database = world.GpuScene.AdvancedSharedDatabase;
            AdvancedGpuScenePublicationReference requested = package is null
                ? world.GpuScene.AdvancedScenePublication
                : package.State == EBackendReadyFramePackageState.Published &&
                    package.TryGetCanonicalPublication(out AdvancedSharedGpuSceneDatabase packageDatabase,
                    out AdvancedGpuScenePublicationReference packageReference) &&
                    ReferenceEquals(packageDatabase, database)
                    ? packageReference
                    : default;
            if (package is not null && !requested.IsValid)
                return RejectRequest(world, consumers, requested,
                    "The active frame package has no canonical publication from this world GPU scene.");

            if (_publication.GpuResourcesPublished && _publication.FrameId == world.FrameId &&
                ReferenceEquals(_publishedScene, world.GpuScene) &&
                _publishedSource != requested.Publication)
                return RejectRequest(world, consumers, requested,
                    $"The world frame already prepared scene sequence {_publishedSource.Sequence}; the active package requests sequence {requested.Sequence}. Recollect a matching package.");

            if (_publication.FrameId == world.FrameId &&
                ReferenceEquals(_publishedScene, world.GpuScene) &&
                _publishedSource == requested.Publication)
            {
                _cacheHitCount++;
                if (!_publication.GpuResourcesPublished)
                {
                    // A deferred build has not begun the current visibility planner.
                    // Repeated consumers must not advance history for unpublished work.
                    _publication = _publication with
                    {
                        Consumers = _publication.Consumers | consumers,
                    };
                    return _publication;
                }
                EAdvancedPreparationConsumer addedConsumers =
                    consumers & ~_publication.Consumers;
                long viewPlanningStarted = Stopwatch.GetTimestamp();
                int viewCount = _extractor.AddVisibilityPlans(viewSet);
                _cacheHitViewCheckTicks += Stopwatch.GetTimestamp() - viewPlanningStarted;
                if (_publication.AggregateDispatchExecuted &&
                    !_extractor.DeformationJobs.IsEmpty &&
                    !_extractor.GpuDeformation.TryApplyConsumerBarriers(
                        addedConsumers))
                {
                    return _publication with
                    {
                        Consumers = _publication.Consumers | consumers,
                        VisibilityViewCount = checked((uint)viewCount),
                        VisibilityContentGeneration =
                            _extractor.VisibilityContentGeneration,
                        AggregateDispatchExecuted = false,
                    };
                }
                _publication = _publication with
                {
                    Consumers = _publication.Consumers | consumers,
                    VisibilityViewCount = checked((uint)viewCount),
                    VisibilityContentGeneration =
                        _extractor.VisibilityContentGeneration,
                };
                return _publication;
            }

            AdvancedGpuScenePublicationLease candidateLease = default;
            AdvancedGpuScenePublicationSnapshot? snapshot = null;
            if (requested.IsValid &&
                database.TryAcquirePublicationLease(requested,
                    EAdvancedGpuScenePublicationPinKind.Package, out candidateLease))
                database.TryGetPublicationSnapshot(requested, out snapshot);
            if (package is not null && snapshot is null)
            {
                candidateLease.Dispose();
                return RejectRequest(world, consumers, requested,
                    $"The active frame package publication sequence {requested.Sequence} is no longer available.");
            }

            _rebuildCount++;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long buildStarted = Stopwatch.GetTimestamp();
            try
            {
                AdvancedPreparationPublication built = _extractor.Build(
                    world, viewSet, consumers, submissionStrategy, requested, snapshot);
                _buildTicks += Stopwatch.GetTimestamp() - buildStarted;
                _buildAllocatedBytes +=
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                _extractionTicks += _extractor.LastExtractionTicks;
                _indirectPlanningTicks += _extractor.LastIndirectPlanningTicks;
                _initialViewPlanningTicks += _extractor.LastInitialViewPlanningTicks;
                long deformationStarted = Stopwatch.GetTimestamp();
                bool executed = built.GpuResourcesPublished &&
                    _extractor.GpuDeformation.TryExecute(
                        _extractor.DispatchPlanner,
                        _extractor.DeformationJobs,
                        consumers,
                        _extractor.Admission.RejectedJobCount);
                _deformationTicks += Stopwatch.GetTimestamp() - deformationStarted;
                AdvancedDeformationDispatchTelemetry telemetry =
                    _extractor.GpuDeformation.LastTelemetry;
                AdvancedGpuScenePublicationLease retiredLease = _publicationLease;
                _publicationLease = candidateLease;
                retiredLease.Dispose();
                _publication = built with
                {
                    AggregateDispatchExecuted = executed,
                    Backend = _extractor.GpuDeformation.Backend,
                    DeformationGpuMilliseconds = telemetry.GpuMilliseconds,
                };
                _publishedScene = world.GpuScene;
                _publishedSource = requested.Publication;
                return _publication;
            }
            catch
            {
                candidateLease.Dispose();
                _publicationLease.Dispose();
                _publicationLease = default;
                _publishedScene = null;
                _publishedSource = default;
                _publication = default;
                throw;
            }
        }
    }

    private static AdvancedPreparationPublication RejectRequest(
        in RenderWorldSnapshot world,
        EAdvancedPreparationConsumer consumers,
        in AdvancedGpuScenePublicationReference requested,
        string reason)
    {
        Debug.RenderingWarningEvery(
            "AdvancedPreparation.CanonicalPublicationUnavailable",
            TimeSpan.FromSeconds(30),
            "[AdvancedPipeline] Preparation cannot use this frame package: {0}", reason);
        return default(AdvancedPreparationPublication) with
        {
            FrameId = world.FrameId,
            SceneIdentity = unchecked((uint)RuntimeHelpers.GetHashCode(world.GpuScene)),
            ScenePublication = requested.Publication,
            Consumers = consumers,
            WarmedManagedAllocationFree = true,
        };
    }

    /// <summary>
    /// Copies one exact visibility publication while holding the same lock that
    /// advances the shared extractor. Deferred backends can therefore retain a
    /// coherent input image without either blocking later preparation work or
    /// reading mutable extractor columns after authoring has completed.
    /// </summary>
    internal bool TryCopyVisibilityInputs(
        AdvancedPreparationExtractor extractor,
        in AdvancedPreparationPublication publication,
        Span<AdvancedVisibilityPayload> payloads,
        Span<AdvancedVisibilityCandidate> candidates,
        Span<EAdvancedGeometryProducer> producers,
        Span<AdvancedIndirectRange> indirectRanges,
        Span<int> indirectPayloadIndices,
        Span<AdvancedDeformedArenaSlice> deformationSlices,
        out AdvancedIndirectPreparationResult indirect,
        out AdvancedGpuDeformationPublication deformationPublication)
        => TryCopyVisibilityInputs(extractor, in publication, payloads, candidates, producers,
            indirectRanges, indirectPayloadIndices, deformationSlices, default,
            out indirect, out deformationPublication, out _);

    internal bool TryCopyVisibilityInputs(
        AdvancedPreparationExtractor extractor,
        in AdvancedPreparationPublication publication,
        Span<AdvancedVisibilityPayload> payloads,
        Span<AdvancedVisibilityCandidate> candidates,
        Span<EAdvancedGeometryProducer> producers,
        Span<AdvancedIndirectRange> indirectRanges,
        Span<int> indirectPayloadIndices,
        Span<AdvancedDeformedArenaSlice> deformationSlices,
        Span<AdvancedGpuBoundsPatchRoute> boundsRoutes,
        out AdvancedIndirectPreparationResult indirect,
        out AdvancedGpuDeformationPublication deformationPublication,
        out XREngine.Rendering.Compute.PhysicsChainGpuOutputPageLease boundsPage)
    {
        long waitStarted = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            long copyLockWaitTicks = Stopwatch.GetTimestamp() - waitStarted;
            _copyLockWaitTicks += copyLockWaitTicks;
            _maximumCopyLockWaitTicks = Math.Max(
                _maximumCopyLockWaitTicks,
                copyLockWaitTicks);
            _copyCount++;
            long copyStarted = Stopwatch.GetTimestamp();
            indirect = default;
            deformationPublication = default;
            boundsPage = default;
            if (!ReferenceEquals(extractor, _extractor) ||
                !_extractor.MatchesPublication(in publication))
            {
                _copyFailureCount++;
                return false;
            }

            if (publication.DeformationJobCount != 0u &&
                !publication.AggregateDispatchExecuted)
            {
                _copyFailureCount++;
                return false;
            }

            deformationPublication = _extractor.GpuDeformationPublication;
            if (publication.DeformationJobCount != 0u &&
                (deformationPublication.FrameId != publication.FrameId ||
                 deformationPublication.JobCount != publication.DeformationJobCount))
            {
                deformationPublication = default;
                _copyFailureCount++;
                return false;
            }

            ReadOnlySpan<AdvancedVisibilityPayload> sourcePayloads =
                _extractor.VisibilityPayloads;
            ReadOnlySpan<AdvancedVisibilityCandidate> sourceCandidates =
                _extractor.VisibilityCandidates;
            ReadOnlySpan<EAdvancedGeometryProducer> sourceProducers =
                _extractor.VisibilityProducers;
            ReadOnlySpan<AdvancedIndirectRange> sourceIndirectRanges =
                _extractor.IndirectRanges;
            ReadOnlySpan<int> sourceIndirectPayloadIndices =
                _extractor.IndirectPayloadIndices;
            indirect = _extractor.IndirectResult;

            if (payloads.Length != sourcePayloads.Length ||
                candidates.Length != sourceCandidates.Length ||
                producers.Length != sourceProducers.Length ||
                indirectRanges.Length != sourceIndirectRanges.Length ||
                indirectPayloadIndices.Length !=
                    sourceIndirectPayloadIndices.Length ||
                deformationSlices.Length != sourcePayloads.Length)
            {
                indirect = default;
                _copyFailureCount++;
                return false;
            }

            sourcePayloads.CopyTo(payloads);
            sourceCandidates.CopyTo(candidates);
            sourceProducers.CopyTo(producers);
            sourceIndirectRanges.CopyTo(indirectRanges);
            sourceIndirectPayloadIndices.CopyTo(indirectPayloadIndices);
            for (int drawIndex = 0; drawIndex < deformationSlices.Length; ++drawIndex)
            {
                // Static draws consume canonical vertices and have no deformation
                // allocation. Clear reused storage so a prior skinned frame cannot
                // donate its slice to a static draw at the same dense index.
                deformationSlices[drawIndex] = default;
                if (sourcePayloads[drawIndex].Skinned &&
                    !_extractor.TryGetDrawDeformationSlice(
                        checked((uint)drawIndex), out deformationSlices[drawIndex]))
                {
                    indirect = default;
                    deformationPublication = default;
                    _copyFailureCount++;
                    return false;
                }
            }
            if (boundsRoutes.IsEmpty && sourceCandidates.Length != 0)
            {
                if (_extractor.HasGpuBoundsPublication)
                {
                    _copyFailureCount++;
                    return false;
                }
            }
            else if (!_extractor.TryCopyGpuBoundsPublication(boundsRoutes, out boundsPage))
            {
                _copyFailureCount++;
                return false;
            }
            long copyTicks = Stopwatch.GetTimestamp() - copyStarted;
            _copyTicks += copyTicks;
            _maximumCopyTicks = Math.Max(_maximumCopyTicks, copyTicks);
            _copiedBytes +=
                (long)sourcePayloads.Length *
                    (Unsafe.SizeOf<AdvancedVisibilityPayload>() +
                     Unsafe.SizeOf<AdvancedVisibilityCandidate>() +
                     Unsafe.SizeOf<EAdvancedGeometryProducer>() +
                     Unsafe.SizeOf<int>() +
                     Unsafe.SizeOf<AdvancedDeformedArenaSlice>()) +
                (long)sourceIndirectRanges.Length *
                    Unsafe.SizeOf<AdvancedIndirectRange>();
            return true;
        }
    }

    /// <summary>
    /// Publishes delayed, completion-gated visibility feedback without
    /// exposing a same-frame GPU readback path. The originating publication
    /// is required to reject feedback from an earlier scene activation.
    /// </summary>
    public void PublishVisibilityFeedback(
        in AdvancedPreparationPublication publication,
        ReadOnlySpan<AdvancedAnimationVisibilityFeedback> feedback,
        ulong completionValue)
    {
        lock (_sync)
        {
            _extractor.PublishVisibilityFeedback(
                in publication,
                feedback,
                completionValue);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _publicationLease.Dispose();
            _publicationLease = default;
            _extractor.Dispose();
            _publishedScene = null;
            _publishedSource = default;
            _publication = default;
        }
    }
}
