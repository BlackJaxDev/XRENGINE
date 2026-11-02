using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Initializes this pipeline's GPU BRDF lookup before PBR consumers run.</summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_PrecomputeBRDF : ViewportRenderCommand
{
    protected override void Execute()
    {
        if (ActivePipelineInstance.Pipeline is IAdvancedRenderStageFamilyHost)
        {
            AdvancedRenderPipelineOutputBinding binding = ActivePipelineInstance.AdvancedOutputBinding;
            AdvancedVisibilityFamilyReservation reservation = binding.Reservation;
            // Visibility preparation refreshes this reservation later in the command
            // chain. Do not submit an auxiliary producer into a stale scene family.
            if (!binding.IsBound || AbstractRenderer.Current is not IRuntimeRendererHost renderer ||
                !renderer.IsAdvancedVisibilityFamilyReservationCurrent(in reservation))
                return;
        }

        if (ParentPipeline?.TryGetRenderPassIndex(nameof(VPRC_PrecomputeBRDF), out int passIndex) != true)
        {
            Debug.RenderingWarningEvery("BRDF.MissingPassMetadata", TimeSpan.FromSeconds(5),
                "BRDF lookup initialization requires its declared graphics pass.");
            return;
        }

        using var pass = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex);
        BrdfIntegrationResources.Prepare(ActivePipelineInstance);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        => context.GetOrCreateSyntheticPass(nameof(VPRC_PrecomputeBRDF), ERenderGraphPassStage.Graphics)
            .UseEngineDescriptors()
            .UseMaterialDescriptors()
            .UseColorAttachment(MakeTextureResource(BrdfIntegrationResources.TextureName),
                ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
}
