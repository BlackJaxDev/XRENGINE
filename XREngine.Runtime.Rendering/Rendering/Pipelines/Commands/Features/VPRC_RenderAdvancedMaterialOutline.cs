using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Draws collected material outline companions over native HDR and depth.</summary>
public sealed class VPRC_RenderAdvancedMaterialOutline : ViewportRenderCommand
{
    private const int OutlineRenderPass = (int)EDefaultRenderPass.MaskedForward;
    private readonly Predicate<RenderCommand> _filter;
    private int _passIndex = OutlineRenderPass;
    private bool _stereo;

    public VPRC_RenderAdvancedMaterialOutline() => _filter = IsEligible;

    public bool Stereo
    {
        get => _stereo;
        set => SetField(ref _stereo, value);
    }

    public override bool NeedsCollecVisible => true;

    protected override bool ShouldExecuteThisFrame()
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.ActiveMeshRenderCommands
            .HasRenderingCommands(OutlineRenderPass) == true;

    protected override void Execute()
    {
        var state = ActivePipelineInstance.RenderState;
        if (state.CurrentRenderTargetBinding?.Name != AdvancedRenderPipeline.ForwardPassFBOName)
            throw new InvalidOperationException("Advanced material outlines require the canonical HDR and native depth framebuffer.");

        using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_passIndex);
        ActivePipelineInstance.ActiveMeshRenderCommands.RenderCPUFiltered(OutlineRenderPass, _filter);
    }

    private bool IsEligible(RenderCommand command)
    {
        if (command is not RenderCommandMesh3D meshCommand ||
            meshCommand.RenderSupplementalMaterialPass != EMaterialPassIdentity.Outline)
            return false;

        if (meshCommand.OwnerRenderInfo is not RenderInfo3D { OwnerRenderableMesh: { } owner } ||
            !ReferenceEquals(owner.MaterialOutlineCommand, meshCommand))
            throw new InvalidOperationException("The collected material outline has no matching renderable owner.");

        var state = ActivePipelineInstance.RenderState;
        if (Stereo || state.OverrideMaterial is not null || state.GlobalMaterialOverride is not null ||
            state.UseDepthNormalMaterialVariants || state.UseMotionVectorMaterialVariant ||
            state.AdvancedLateTemporalOutput != EAdvancedLateTemporalOutput.None ||
            (RenderPipeline.ResolveEffectiveAntiAliasingModeForFrame() == EAntiAliasingMode.Msaa &&
             RenderPipeline.ResolveEffectiveMsaaSampleCountForFrame() > 1u))
            throw new InvalidOperationException("Advanced material outlines do not support stereo output, material overrides, temporal replay, or multisample output.");

        if (state.WindowViewport?.MeshSubmissionStrategyOverride is { } strategy && strategy != EMeshSubmissionStrategy.CpuDirect)
            throw new InvalidOperationException("Advanced material outlines require filtered CPU-direct submission.");

        if (!meshCommand.TryGetCpuOcclusionSnapshot(out var mesh, out _, out var materialOverride, out var optionsOverride, out _) ||
            mesh?.Mesh?.HasNormals != true || mesh?.Mesh?.Type != EPrimitiveType.Triangles ||
            mesh?.MeshDeformEnabled != false ||
            materialOverride?.UberOutlineSourceMaterial is not { } source ||
            !source.IsResolvedOutlinePassVariant(materialOverride) ||
            !source.PassSet.TryGetPass(EMaterialPassIdentity.Outline, out MaterialPassDefinition pass) ||
            !pass.Enabled || pass.RenderPass != OutlineRenderPass ||
            materialOverride?.RenderPass != OutlineRenderPass ||
            !ReferenceEquals(pass.RenderOptions, optionsOverride) ||
            materialOverride?.BillboardMode != EMeshBillboardMode.None)
            throw new InvalidOperationException("The collected material outline has unsupported geometry, material, or pass state.");

        return true;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        var builder = context.Metadata.ForPass(OutlineRenderPass, "AdvancedMaterialOutline", ERenderGraphPassStage.Graphics);
        _passIndex = builder.PassIndex;
        builder.UseEngineDescriptors().UseMaterialDescriptors();
        if (context.CurrentRenderTarget is not { } target)
            return;

        builder.UseColorAttachment(MakeFboColorResource(target.Name), target.ColorAccess,
                target.ConsumeColorLoadOp(), target.GetColorStoreOp());
        UseRenderTargetDepthStencilAttachments(builder, target,
            target.ConsumeDepthLoadOp(), target.ConsumeStencilLoadOp());
    }
}
