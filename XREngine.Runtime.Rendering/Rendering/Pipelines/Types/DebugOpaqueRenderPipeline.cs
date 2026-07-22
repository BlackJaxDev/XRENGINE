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
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>
/// Lightweight render pipeline for diagnostic worlds. Renders the ordinary scene passes and globally batched debug
/// primitives without the default pipeline's lighting, ambient occlusion, bloom, temporal upscaling, or post-processing.
/// </summary>
public sealed class DebugOpaqueRenderPipeline : RenderPipeline
{
    private const string SceneColorTextureName = "DebugOpaqueSceneColor";
    private const string DepthStencilTextureName = "DebugOpaqueDepthStencil";
    private const string SceneFBOName = "DebugOpaqueSceneFBO";
    private const string DebugOverlayPassName = "DebugOpaqueOverlay";

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
        InitializeCommandChain();
    }

    protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy size = RenderResourceSizePolicy.Internal();

        builder.Texture(SceneColorTextureName)
            .Size(size)
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Factory(CreateSceneColorTexture)
            .Add();

        builder.Texture(DepthStencilTextureName)
            .Size(size)
            .Usage(RenderPipelineResourceUsage.DepthStencilAttachment)
            .Format(EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248)
            .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
            .Factory(CreateDepthStencilTexture)
            .Add();

        builder.FrameBuffer(SceneFBOName)
            .Size(size)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, SceneColorTextureName)
            .DepthStencil(DepthStencilTextureName)
            .Factory(CreateSceneFBO)
            .Add();
    }

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer container = new(this);

        AppendSceneCommands(container);

        var present = container.Add<VPRC_RenderToWindow>();
        present.SourceFBOName = SceneFBOName;
        present.FlipSourceYOnVulkan = true;

        container.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        container.Add<VPRC_RenderScreenSpaceUI>();
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

    private void AppendSceneCommands(ViewportRenderCommandContainer commands)
    {
        commands.Add<VPRC_SetClears>().Set(ColorF4.Black, 1.0f, 0);
        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (commands.AddUsing<VPRC_PushViewportRenderArea>(options => options.UseInternalResolution = true))
        {
            using (commands.AddUsing<VPRC_BindFBOByName>(options => options.SetOptions(SceneFBOName)))
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
                commands.Add<VPRC_RenderDebugShapes>().RenderGraphPassName = DebugOverlayPassName;
                commands.Add<VPRC_RenderDebugPhysics>().RenderGraphPassName = DebugOverlayPassName;
            }
        }
    }

    private XRTexture CreateSceneColorTexture()
    {
        var texture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Name = SceneColorTextureName;
        texture.SamplerName = "HDRSceneTex";
        texture.Resizable = true;
        texture.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        return texture;
    }

    private XRTexture CreateDepthStencilTexture()
    {
        var texture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.Depth24Stencil8,
            EPixelFormat.DepthStencil,
            EPixelType.UnsignedInt248,
            EFrameBufferAttachment.DepthStencilAttachment);
        texture.Name = DepthStencilTextureName;
        texture.Resizable = true;
        texture.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        return texture;
    }

    private XRFrameBuffer CreateSceneFBO()
    {
        if (GetTexture<XRTexture>(SceneColorTextureName) is not IFrameBufferAttachement colorAttachment)
            throw new InvalidOperationException("Debug scene color texture must be FBO-attachable.");

        if (GetTexture<XRTexture>(DepthStencilTextureName) is not IFrameBufferAttachement depthAttachment)
            throw new InvalidOperationException("Debug depth/stencil texture must be FBO-attachable.");

        return new XRFrameBuffer(
            (colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = SceneFBOName,
        };
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(GpuRenderDispatch) || propName == nameof(MeshSubmissionStrategy))
            RuntimeEngine.Rendering.ApplyMeshSubmissionStrategyToPipeline(this, MeshSubmissionStrategy);
    }
}
