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
                    !_extractor.DeformationJobs.IsEmpty)
                {
                    _extractor.GpuDeformation.ApplyConsumerBarriers(
                        addedConsumers);
                }
                _publication = _publication with
                {
                    Consumers = _publication.Consumers | consumers,
                    VisibilityViewCount = checked((uint)viewCount),
                };
                return _publication;
            }

            _publication = _extractor.Build(world, viewSet, consumers);
            bool executed = _extractor.GpuDeformation.TryExecute(
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
