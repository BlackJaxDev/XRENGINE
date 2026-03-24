using System;
using System.Collections.Generic;
using System.Threading;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering;

/// <summary>
/// Lightweight scene-capture pipeline for light probes.
/// Renders lit scene geometry without viewport post-processing so probe data
/// stays stable when the main default pipelines evolve.
/// </summary>
public sealed class LightProbeRenderPipeline : RenderPipeline
{
    private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
    private readonly FarToNearRenderCommandSorter _farToNearSorter = new();

    private bool _gpuRenderDispatch = Engine.Rendering.ResolveGpuRenderDispatchPreference(Engine.EffectiveSettings.GPURenderDispatch);

    private static bool EnableComputeDependentPasses
        => VulkanFeatureProfile.EnableComputeDependentPasses;

    /// <summary>
    /// When true, the pipeline dispatches scene passes using GPU-driven rendering.
    /// </summary>
    public bool GpuRenderDispatch
    {
        get => _gpuRenderDispatch;
        set => SetField(ref _gpuRenderDispatch, Engine.Rendering.ResolveGpuRenderDispatchPreference(value));
    }

    protected override Lazy<XRMaterial> InvalidMaterialFactory
        => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);

    public override string DebugName => "LightProbeCapturePipeline";

    public LightProbeRenderPipeline() : base(true)
    {
        OverrideProtected = true;
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

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => new()
        {
            { (int)EDefaultRenderPass.PreRender, null },
            { (int)EDefaultRenderPass.Background, null },
            { (int)EDefaultRenderPass.OpaqueDeferred, _nearToFarSorter },
            { (int)EDefaultRenderPass.DeferredDecals, _farToNearSorter },
            { (int)EDefaultRenderPass.OpaqueForward, _nearToFarSorter },
            { (int)EDefaultRenderPass.MaskedForward, _nearToFarSorter },
            { (int)EDefaultRenderPass.TransparentForward, _farToNearSorter },
            { (int)EDefaultRenderPass.WeightedBlendedOitForward, null },
            { (int)EDefaultRenderPass.PerPixelLinkedListForward, null },
            { (int)EDefaultRenderPass.DepthPeelingForward, null },
            { (int)EDefaultRenderPass.PostRender, null },
        };

    private static XRMaterial MakeInvalidMaterial()
        => XRMaterial.CreateColorMaterialDeferred();

    private ViewportRenderCommandContainer CreateViewportTargetCommands()
    {
        ViewportRenderCommandContainer commands = new(this);
        AppendDirectCaptureCommands(
            commands,
            bindOutput: scope => commands.AddUsing<VPRC_BindOutputFBO>(),
            pushRenderArea: scope => commands.AddUsing<VPRC_PushViewportRenderArea>(options => options.UseInternalResolution = true));
        return commands;
    }

    private ViewportRenderCommandContainer CreateFBOTargetCommands()
    {
        ViewportRenderCommandContainer commands = new(this);
        AppendDirectCaptureCommands(
            commands,
            bindOutput: scope => commands.AddUsing<VPRC_BindOutputFBO>(),
            pushRenderArea: scope => commands.AddUsing<VPRC_PushOutputFBORenderArea>());
        return commands;
    }

    private void AppendDirectCaptureCommands(
        ViewportRenderCommandContainer commands,
        Func<ViewportRenderCommandContainer, IDisposable> bindOutput,
        Func<ViewportRenderCommandContainer, IDisposable> pushRenderArea)
    {
        bool enableComputePasses = EnableComputeDependentPasses;

        commands.Add<VPRC_SetClears>().Set(ColorF4.Black, 1.0f, 0);
        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (pushRenderArea(commands))
        {
            using (bindOutput(commands))
            {
                commands.Add<VPRC_StencilMask>().Set(~0u);
                commands.Add<VPRC_ClearByBoundFBO>();

                commands.Add<VPRC_DepthTest>().Enable = true;
                commands.Add<VPRC_DepthWrite>().Allow = false;
                commands.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, GpuRenderDispatch);

                commands.Add<VPRC_DepthFunc>().Comp = EComparison.Less;
                commands.Add<VPRC_DepthWrite>().Allow = true;
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, GpuRenderDispatch);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DeferredDecals, GpuRenderDispatch);

                if (enableComputePasses)
                    commands.Add<VPRC_ForwardPlusLightCullingPass>();

                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GpuRenderDispatch);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, GpuRenderDispatch);

                commands.Add<VPRC_DepthWrite>().Allow = false;
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, GpuRenderDispatch);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PerPixelLinkedListForward, GpuRenderDispatch);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DepthPeelingForward, GpuRenderDispatch);
                commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GpuRenderDispatch);
            }
        }

        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(GpuRenderDispatch))
            Engine.Rendering.ApplyGpuRenderDispatchToPipeline(this, GpuRenderDispatch);
    }
}