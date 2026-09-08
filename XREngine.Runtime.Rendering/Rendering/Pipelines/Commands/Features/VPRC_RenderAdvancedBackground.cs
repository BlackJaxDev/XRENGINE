using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Executes the explicitly authored CPU-direct background lane after native opaque
/// compute shading. Far-depth admission keeps native geometry and identity intact.
/// </summary>
public sealed class VPRC_RenderAdvancedBackground : ViewportRenderCommand
{
    private readonly Predicate<RenderCommand> _filter;
    private bool _stereo;
    private int _passIndex = int.MinValue;

    public VPRC_RenderAdvancedBackground() => _filter = IsEligible;

    public bool Stereo
    {
        get => _stereo;
        set => SetField(ref _stereo, value);
    }

    protected override bool ShouldExecuteThisFrame()
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.ActiveMeshRenderCommands
            .HasRenderingCommands((int)EDefaultRenderPass.Background) == true;

    protected override void Execute()
    {
        var state = ActivePipelineInstance.RenderState;
        if (state.CurrentRenderTargetBinding?.Name != AdvancedRenderPipeline.ForwardPassFBOName)
            throw new InvalidOperationException("Advanced background drawing requires the canonical HDR and native depth framebuffer.");

        if (state.OverrideMaterial is not null || state.GlobalMaterialOverride is not null ||
            state.UseDepthNormalMaterialVariants || state.UseMotionVectorMaterialVariant ||
            state.AdvancedLateTemporalOutput != EAdvancedLateTemporalOutput.None)
        {
            ReportRejection(state.OverrideMaterial, "Pipeline material overrides and temporal replay variants have no background admission receipt.");
            return;
        }

        if (state.WindowViewport?.MeshSubmissionStrategyOverride is { } strategy && strategy != EMeshSubmissionStrategy.CpuDirect)
        {
            ReportRejection(null, "The viewport submission override conflicts with the authored CPU-direct background lane.");
            return;
        }

        using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_passIndex);
        ActivePipelineInstance.ActiveMeshRenderCommands.RenderCPUFiltered((int)EDefaultRenderPass.Background, _filter);
    }

    private bool IsEligible(RenderCommand command)
    {
        if (command is not RenderCommandMesh3D meshCommand ||
            !meshCommand.TryGetCpuOcclusionSnapshot(out var mesh, out _, out var materialOverride, out var optionsOverride, out _))
        {
            ReportRejection(null, "Background callbacks require an explicit far-depth mesh command.");
            return false;
        }

        XRMaterial? material = materialOverride ?? mesh?.Material;
        string? reason = null;
        if (material is null || optionsOverride is not null || !material.TryValidateAdvancedBackground(Stereo, out reason))
        {
            ReportRejection(material, optionsOverride is not null
                ? "Per-draw state overrides have no background admission receipt."
                : reason ?? "The background command has no material.");
            return false;
        }
        // A renderer can issue several material subdraws. A receipt on its primary
        // material alone cannot authorize a different submesh to modify native state.
        if (materialOverride is null)
            for (int i = 0; i < mesh!.Submeshes.Count; ++i)
            {
                XRMaterial? submaterial = mesh.Submeshes[i].Material;
                if (submaterial is null || !submaterial.TryValidateAdvancedBackground(Stereo, out reason))
                {
                    ReportRejection(submaterial, reason ?? "The background submesh has no admitted material.");
                    return false;
                }
            }
        return true;
    }

    private static void ReportRejection(XRMaterial? material, string reason)
    {
        if (Debug.ShouldLogEvery("AdvancedBackground.Rejected", TimeSpan.FromSeconds(2)))
            Debug.RenderingWarning("[AdvancedBackground] Material '{0}' rejected: {1}", material?.Name ?? "<none>", reason);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        if (context.CurrentRenderTarget is not { } target)
            return;
        var builder = context.GetOrCreateSyntheticPass("AdvancedAuthoredBackground", ERenderGraphPassStage.Graphics);
        _passIndex = builder.PassIndex;
        builder.UseEngineDescriptors().UseMaterialDescriptors()
            .UseColorAttachment(MakeTextureResource(AdvancedRenderPipeline.HDRSceneTextureName),
                target.ColorAccess, target.ConsumeColorLoadOp(), target.GetColorStoreOp())
            .UseDepthAttachment(MakeTextureResource(AdvancedVisibilityResourceNames.DepthStencil),
                ERenderGraphAccess.Read, target.ConsumeDepthLoadOp(), target.GetDepthStoreOp());
    }
}
