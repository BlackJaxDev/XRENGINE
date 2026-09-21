using XREngine.Components.Lights;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Captures the active skybox into DDGI's octahedral environment target from a
/// graphics render-graph scope before compute hit shading samples it.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_DDGIEnvironmentPass : ViewportRenderCommand
{
    private const string AllocationScopeName = "DDGI.VPRC_DDGIEnvironmentPass";
    private int _passIndex = int.MinValue;

    protected override bool ShouldExecuteThisFrame()
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline is
            IGlobalIlluminationPipelineProvider { UsesDDGI: true };

    protected override void Execute()
    {
        if (ActivePipelineInstance.Pipeline is not IGlobalIlluminationPipelineProvider { UsesDDGI: true })
            return;

        if (_passIndex == int.MinValue && ParentPipeline?.TryGetRenderPassIndex(nameof(VPRC_DDGIEnvironmentPass), out int passIndex) == true)
            _passIndex = passIndex;
        if (_passIndex == int.MinValue)
        {
            Debug.RenderingWarningEvery(
                "DDGI.Environment.MissingPassMetadata",
                TimeSpan.FromSeconds(2),
                "DDGI cannot capture environment radiance because its render-graph pass is missing.");
            return;
        }

        var world = ActivePipelineInstance.RenderState.WindowViewport?.World
            ?? RuntimeEngine.Rendering.State.RenderingWorld;
        if (world is null)
            return;

#if !XRE_PUBLISHED
        using var allocationScope = RuntimeRenderingHostServices.Profiling.EnableThreadAllocationTracking
            ? DDGIManagedAllocationDiagnostics.Begin(AllocationScopeName)
            : default;
#endif
        using var scope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_passIndex);
        DDGIEnvironmentResources.Prepare(ActivePipelineInstance, world);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        => context.GetOrCreateSyntheticPass(nameof(VPRC_DDGIEnvironmentPass), ERenderGraphPassStage.Graphics)
            .UseEngineDescriptors()
            .UseMaterialDescriptors()
            .UseColorAttachment(
                MakeTextureResource(DDGIEnvironmentResources.TextureName),
                ERenderGraphAccess.Write,
                ERenderPassLoadOp.DontCare,
                ERenderPassStoreOp.Store);
}
