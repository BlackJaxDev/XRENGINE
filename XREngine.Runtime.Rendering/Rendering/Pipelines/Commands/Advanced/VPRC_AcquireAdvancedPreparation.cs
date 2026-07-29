namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Acquires one shared world-preparation publication before an output-local
/// desktop or eye command chain consumes geometry.
/// </summary>
public sealed class VPRC_AcquireAdvancedPreparation : ViewportRenderCommand
{
    public override string GpuProfilingName => "Advanced preparation acquire";
    public override string CpuProfilingName => "AdvancedPreparation.Acquire";

    public AdvancedPreparationPublication LastPublication { get; private set; }

    protected override void Execute()
    {
        XRRenderPipelineInstance.RenderingState state =
            ActivePipelineInstance.RenderState;
        if (state.WorldSnapshot is not RenderWorldSnapshot world)
            return;

        LastPublication = AdvancedSharedPreparationService.Instance.Acquire(
            world,
            state.FrameViewSet,
            EAdvancedPreparationConsumer.Visibility |
            EAdvancedPreparationConsumer.Depth |
            EAdvancedPreparationConsumer.Velocity |
            EAdvancedPreparationConsumer.MaterialReconstruction |
            EAdvancedPreparationConsumer.DirectionalShadow |
            EAdvancedPreparationConsumer.PointShadow |
            EAdvancedPreparationConsumer.SpotShadow |
            EAdvancedPreparationConsumer.Probe |
            EAdvancedPreparationConsumer.Capture);
    }
}
