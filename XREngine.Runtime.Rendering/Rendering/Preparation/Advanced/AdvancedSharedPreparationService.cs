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
    private readonly AdvancedPreparationExtractor _extractor;
    private GPUScene? _publishedScene;
    private AdvancedPreparationPublication _publication;

    public AdvancedSharedPreparationService(AdvancedPreparationOptions options)
        => _extractor = new AdvancedPreparationExtractor(options);

    public static AdvancedSharedPreparationService Instance => Shared.Value;

    public AdvancedPreparationExtractor Extractor => _extractor;

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
                service._extractor.GpuDeformation.LastOutputReuseAuthority);
    }

    public AdvancedPreparationPublication Acquire(
        in RenderWorldSnapshot world,
        RenderFrameViewSet? viewSet,
        EAdvancedPreparationConsumer consumers)
    {
        lock (_sync)
        {
            if (_publication.FrameId == world.FrameId &&
                ReferenceEquals(_publishedScene, world.GpuScene))
            {
                EAdvancedPreparationConsumer addedConsumers =
                    consumers & ~_publication.Consumers;
                int viewCount = _extractor.AddVisibilityPlans(viewSet);
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

            _publication = _extractor.Build(world, viewSet, consumers);
            bool executed = _publication.GpuResourcesPublished &&
                _extractor.GpuDeformation.TryExecute(
                    _extractor.DispatchPlanner,
                    _extractor.DeformationJobs,
                    consumers,
                    _extractor.Admission.RejectedJobCount);
            AdvancedDeformationDispatchTelemetry telemetry =
                _extractor.GpuDeformation.LastTelemetry;
            _publication = _publication with
            {
                AggregateDispatchExecuted = executed,
                Backend = _extractor.GpuDeformation.Backend,
                DeformationGpuMilliseconds =
                    telemetry.GpuMilliseconds,
            };
            _publishedScene = world.GpuScene;
            return _publication;
        }
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
    {
        lock (_sync)
        {
            indirect = default;
            deformationPublication = default;
            if (!ReferenceEquals(extractor, _extractor) ||
                !_extractor.MatchesPublication(in publication))
            {
                return false;
            }

            if (publication.DeformationJobCount != 0u &&
                !publication.AggregateDispatchExecuted)
                return false;

            deformationPublication = _extractor.GpuDeformationPublication;
            if (publication.DeformationJobCount != 0u &&
                (deformationPublication.FrameId != publication.FrameId ||
                 deformationPublication.JobCount != publication.DeformationJobCount))
            {
                deformationPublication = default;
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
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Publishes delayed, completion-gated visibility feedback without
    /// exposing a same-frame GPU readback path.
    /// </summary>
    public void PublishVisibilityFeedback(
        ulong frameId,
        ReadOnlySpan<AdvancedAnimationVisibilityFeedback> feedback,
        ulong completionValue)
    {
        lock (_sync)
        {
            _extractor.PublishVisibilityFeedback(
                frameId,
                feedback,
                completionValue);
        }
    }

    public void Dispose()
        => _extractor.Dispose();
}
