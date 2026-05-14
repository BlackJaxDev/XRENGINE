using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using XREngine.Extensions;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering;

/// <summary>
/// Minimal render pipeline for debugging opaque geometry. Renders background, deferred opaque, and forward opaque passes only.
/// </summary>
public sealed class DebugOpaqueRenderPipeline : RenderPipeline
{
    private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();

    private EMeshSubmissionStrategy _meshSubmissionStrategy = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();

    /// <summary>
    /// When true, the pipeline dispatches opaque passes using GPU-driven rendering.
    /// </summary>
    public bool GpuRenderDispatch
    {
        get => MeshSubmissionStrategy != EMeshSubmissionStrategy.CpuDirect;
        set => MeshSubmissionStrategy = value
            ? RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(true)
            : EMeshSubmissionStrategy.CpuDirect;
    }

    public EMeshSubmissionStrategy MeshSubmissionStrategy
    {
        get => _meshSubmissionStrategy;
        set => SetField(ref _meshSubmissionStrategy, value);
    }

    protected override Lazy<XRMaterial> InvalidMaterialFactory
        => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);

    public override string DebugName => "DebugOpaquePipeline";

    public DebugOpaqueRenderPipeline() : base(true)
    {
        CommandChain = GenerateCommandChain();
    }

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer container = new(this);
        var ifElse = container.Add<VPRC_IfElse>();
        ifElse.ConditionEvaluator = () => State.WindowViewport is not null;
        ifElse.TrueCommands = CreateViewportTargetCommands();
        ifElse.FalseCommands = CreateFBOTargetCommands();
        return container;
    }

    private readonly FarToNearRenderCommandSorter _farToNearSorter = new();

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => new()
        {
            { (int)EDefaultRenderPass.PreRender, null },
            { (int)EDefaultRenderPass.Background, null },
            { (int)EDefaultRenderPass.OpaqueDeferred, _nearToFarSorter },
            { (int)EDefaultRenderPass.OpaqueForward, _nearToFarSorter },
            { (int)EDefaultRenderPass.MaskedForward, _nearToFarSorter },
            { (int)EDefaultRenderPass.WeightedBlendedOitForward, null },
            { (int)EDefaultRenderPass.TransparentForward, _farToNearSorter },
            { (int)EDefaultRenderPass.OnTopForward, null },
            { (int)EDefaultRenderPass.PostRender, null },
        };

    private static XRMaterial MakeInvalidMaterial()
        => XRMaterial.CreateColorMaterialDeferred();

    private ViewportRenderCommandContainer CreateViewportTargetCommands()
    {
        ViewportRenderCommandContainer commands = new(this);

        commands.Add<VPRC_SetClears>().Set(ColorF4.Black, 1.0f, 0);
        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (commands.AddUsing<VPRC_PushViewportRenderArea>(options => options.UseInternalResolution = true))
        {
            using (commands.AddUsing<VPRC_BindOutputFBO>())
            {
                commands.Add<VPRC_StencilMask>().Set(~0u);
                commands.Add<VPRC_ClearByBoundFBO>();

                commands.Add<VPRC_DepthTest>().Enable = true;
                commands.Add<VPRC_DepthWrite>().Allow = false;
                // Skybox renders at maximum depth (1.0). Use Lequal so fragments at
                // the cleared depth value still pass (1.0 <= 1.0 vs 1.0 < 1.0 = fail).
                commands.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, MeshSubmissionStrategy);

                commands.Add<VPRC_DepthFunc>().Comp = EComparison.Less;
                commands.Add<VPRC_DepthWrite>().Allow = true;
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, MeshSubmissionStrategy);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, MeshSubmissionStrategy);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, MeshSubmissionStrategy);

                // Transparent pass for world-space UI canvas quads and other alpha-blended geometry.
                commands.Add<VPRC_DepthWrite>().Allow = false;
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, false);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, false);
                commands.Add<VPRC_DepthWrite>().Allow = true;

                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, false);
                commands.Add<VPRC_RenderDebugShapes>();
                commands.Add<VPRC_RenderDebugPhysics>();
            }
        }

        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        commands.Add<VPRC_RenderScreenSpaceUI>();
        return commands;
    }

    private ViewportRenderCommandContainer CreateFBOTargetCommands()
    {
        ViewportRenderCommandContainer commands = new(this);

        commands.Add<VPRC_SetClears>().Set(ColorF4.Black, 1.0f, 0);
        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (commands.AddUsing<VPRC_PushOutputFBORenderArea>())
        {
            using (commands.AddUsing<VPRC_BindOutputFBO>())
            {
                commands.Add<VPRC_StencilMask>().Set(~0u);
                commands.Add<VPRC_ClearByBoundFBO>();

                commands.Add<VPRC_DepthTest>().Enable = true;
                commands.Add<VPRC_DepthWrite>().Allow = false;
                // Same Lequal fix for FBO target path.
                commands.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, MeshSubmissionStrategy);

                commands.Add<VPRC_DepthFunc>().Comp = EComparison.Less;
                commands.Add<VPRC_DepthWrite>().Allow = true;
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, MeshSubmissionStrategy);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, MeshSubmissionStrategy);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, MeshSubmissionStrategy);

                // Transparent pass for world-space UI canvas quads and other alpha-blended geometry.
                commands.Add<VPRC_DepthWrite>().Allow = false;
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, false);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, false);
                commands.Add<VPRC_DepthWrite>().Allow = true;

                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, false);
                commands.Add<VPRC_RenderDebugShapes>();
                commands.Add<VPRC_RenderDebugPhysics>();
            }
        }

        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        commands.Add<VPRC_RenderScreenSpaceUI>();
        return commands;
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(GpuRenderDispatch) || propName == nameof(MeshSubmissionStrategy))
            RuntimeEngine.Rendering.ApplyMeshSubmissionStrategyToPipeline(this, MeshSubmissionStrategy);
    }
}
