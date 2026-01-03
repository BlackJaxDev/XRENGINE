using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

/// <summary>
/// Visualization modes for the Surfel Debug pipeline.
/// </summary>
public enum ESurfelDebugVisualization
{
    /// <summary>
    /// Standard lit scene rendering (no debug overlay).
    /// </summary>
    Normal,

    /// <summary>
    /// Visualize mesh transform IDs as hashed random colors.
    /// </summary>
    TransformId,

    /// <summary>
    /// Visualize surfel positions as colored circles on the scene geometry.
    /// </summary>
    SurfelCircles,

    /// <summary>
    /// Visualize surfel grid cell occupancy.
    /// </summary>
    SurfelGridCells,
}

/// <summary>
/// A minimal render pipeline for debugging and visualizing SurfelGI information.
/// Supports visualization of transform IDs and surfel positions without post-processing overhead.
/// </summary>
public sealed class SurfelDebugRenderPipeline : RenderPipeline
{
    public const string SceneShaderPath = "Scene3D";

    private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
    private readonly FarToNearRenderCommandSorter _farToNearSorter = new();

    // Texture names
    public const string DepthStencilTextureName = "DepthStencil";
    public const string DepthViewTextureName = "DepthView";
    public const string AlbedoOpacityTextureName = "AlbedoOpacity";
    public const string NormalTextureName = "Normal";
    public const string TransformIdTextureName = "TransformId";
    public const string HDRSceneTextureName = "HDRSceneTex";
    public const string SurfelDebugOutputTextureName = "SurfelDebugOutput";
    public const string SurfelGITextureName = "SurfelGITexture";

    // FBO names
    public const string GBufferFBOName = "GBufferFBO";
    public const string ForwardPassFBOName = "ForwardPassFBO";
    public const string TransformIdDebugFBOName = "TransformIdDebugFBO";
    public const string SurfelDebugFBOName = "SurfelDebugFBO";
    public const string SurfelGICompositeFBOName = "SurfelGICompositeFBO";

    private bool _gpuRenderDispatch = Engine.EffectiveSettings.GPURenderDispatch;

    /// <summary>
    /// When true, the pipeline dispatches render passes using GPU-driven rendering.
    /// </summary>
    public bool GpuRenderDispatch
    {
        get => _gpuRenderDispatch;
        set => SetField(ref _gpuRenderDispatch, value);
    }

    private ESurfelDebugVisualization _visualizationMode = ESurfelDebugVisualization.Normal;

    /// <summary>
    /// The current debug visualization mode.
    /// </summary>
    public ESurfelDebugVisualization VisualizationMode
    {
        get => _visualizationMode;
        set
        {
            if (SetField(ref _visualizationMode, value))
            {
                Engine.InvokeOnMainThread(() =>
                {
                    CommandChain = GenerateCommandChain();
                    foreach (var instance in Instances)
                        instance.DestroyCache();
                }, true);
            }
        }
    }

    protected override Lazy<XRMaterial> InvalidMaterialFactory
        => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);

    public override string DebugName => "SurfelDebugPipeline";

    public SurfelDebugRenderPipeline() : base(true)
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

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => new()
        {
            { (int)EDefaultRenderPass.PreRender, null },
            { (int)EDefaultRenderPass.Background, null },
            { (int)EDefaultRenderPass.OpaqueDeferred, _nearToFarSorter },
            { (int)EDefaultRenderPass.DeferredDecals, _farToNearSorter },
            { (int)EDefaultRenderPass.OpaqueForward, _nearToFarSorter },
            { (int)EDefaultRenderPass.TransparentForward, _farToNearSorter },
            { (int)EDefaultRenderPass.OnTopForward, null },
            { (int)EDefaultRenderPass.PostRender, null },
        };

    private static XRMaterial MakeInvalidMaterial()
        => XRMaterial.CreateColorMaterialDeferred();

    private ViewportRenderCommandContainer CreateViewportTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);

        // Cache textures
        CacheTextures(c);

        c.Add<VPRC_SetClears>().Set(ColorF4.Black, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            // GBuffer pass for deferred geometry (captures transform IDs, normals, albedo)
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                GBufferFBOName,
                CreateGBufferFBO,
                GetDesiredFBOSizeInternal);

            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(GBufferFBOName)))
            {
                c.Add<VPRC_StencilMask>().Set(~0u);
                c.Add<VPRC_ClearByBoundFBO>();
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, GpuRenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DeferredDecals, GpuRenderDispatch);
            }

            // Forward pass FBO
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                ForwardPassFBOName,
                CreateForwardPassFBO,
                GetDesiredFBOSizeInternal);

            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardPassFBOName, true, true, false, false)))
            {
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, GpuRenderDispatch);

                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_ForwardPlusLightCullingPass>();
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GpuRenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GpuRenderDispatch);

                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GpuRenderDispatch);

                c.Add<VPRC_RenderDebugShapes>();
            }

            // Run the Surfel GI pass (populates surfel buffers)
            c.Add<VPRC_SurfelGIPass>();

            // Visualization selection
            var visualizationSwitch = c.Add<VPRC_Switch>();
            visualizationSwitch.SwitchEvaluator = () => (int)VisualizationMode;
            visualizationSwitch.Cases = new Dictionary<int, ViewportRenderCommandContainer>
            {
                [(int)ESurfelDebugVisualization.TransformId] = CreateTransformIdVisualizationCommands(),
                [(int)ESurfelDebugVisualization.SurfelCircles] = CreateSurfelCircleVisualizationCommands(),
                [(int)ESurfelDebugVisualization.SurfelGridCells] = CreateSurfelGridVisualizationCommands(),
            };
            // Default (Normal) just outputs the forward pass result
            visualizationSwitch.DefaultCase = CreateNormalOutputCommands();
        }

        // Final output to screen
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                var outputChoice = c.Add<VPRC_Switch>();
                outputChoice.SwitchEvaluator = () => (int)VisualizationMode;
                outputChoice.Cases = new Dictionary<int, ViewportRenderCommandContainer>
                {
                    [(int)ESurfelDebugVisualization.TransformId] = CreateTransformIdOutputCommands(),
                    [(int)ESurfelDebugVisualization.SurfelCircles] = CreateSurfelDebugOutputCommands(),
                    [(int)ESurfelDebugVisualization.SurfelGridCells] = CreateSurfelDebugOutputCommands(),
                };
                outputChoice.DefaultCase = CreateForwardPassOutputCommands();
            }
        }

        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        return c;
    }

    private ViewportRenderCommandContainer CreateFBOTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);

        c.Add<VPRC_SetClears>().Set(ColorF4.Black, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (c.AddUsing<VPRC_PushOutputFBORenderArea>())
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                c.Add<VPRC_StencilMask>().Set(~0u);
                c.Add<VPRC_ClearByBoundFBO>();
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, GpuRenderDispatch);
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, GpuRenderDispatch);
                c.Add<VPRC_ForwardPlusLightCullingPass>();
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GpuRenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GpuRenderDispatch);
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GpuRenderDispatch);
            }
        }
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        return c;
    }

    #region Visualization Command Containers

    private ViewportRenderCommandContainer CreateNormalOutputCommands()
    {
        return new ViewportRenderCommandContainer(this);
    }

    private ViewportRenderCommandContainer CreateTransformIdVisualizationCommands()
    {
        var c = new ViewportRenderCommandContainer(this);
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TransformIdDebugFBOName,
            CreateTransformIdDebugFBO,
            GetDesiredFBOSizeInternal);
        return c;
    }

    private ViewportRenderCommandContainer CreateSurfelCircleVisualizationCommands()
    {
        var c = new ViewportRenderCommandContainer(this);
        // Use the compute-based debug visualization that can access surfel buffers
        var debugVis = c.Add<VPRC_SurfelDebugVisualization>();
        debugVis.Mode = VPRC_SurfelDebugVisualization.EVisualizationMode.SurfelCircles;
        debugVis.DepthTextureName = DepthViewTextureName;
        debugVis.NormalTextureName = NormalTextureName;
        debugVis.AlbedoTextureName = AlbedoOpacityTextureName;
        debugVis.TransformIdTextureName = TransformIdTextureName;
        debugVis.HDRSceneTextureName = HDRSceneTextureName;
        debugVis.OutputTextureName = SurfelDebugOutputTextureName;
        return c;
    }

    private ViewportRenderCommandContainer CreateSurfelGridVisualizationCommands()
    {
        var c = new ViewportRenderCommandContainer(this);
        // Use the compute-based debug visualization that can access surfel buffers
        var debugVis = c.Add<VPRC_SurfelDebugVisualization>();
        debugVis.Mode = VPRC_SurfelDebugVisualization.EVisualizationMode.GridHeatmap;
        debugVis.DepthTextureName = DepthViewTextureName;
        debugVis.NormalTextureName = NormalTextureName;
        debugVis.AlbedoTextureName = AlbedoOpacityTextureName;
        debugVis.TransformIdTextureName = TransformIdTextureName;
        debugVis.HDRSceneTextureName = HDRSceneTextureName;
        debugVis.OutputTextureName = SurfelDebugOutputTextureName;
        return c;
    }

    private ViewportRenderCommandContainer CreateForwardPassOutputCommands()
    {
        var c = new ViewportRenderCommandContainer(this);
        c.Add<VPRC_RenderQuadFBO>().FrameBufferName = ForwardPassFBOName;
        return c;
    }

    private ViewportRenderCommandContainer CreateTransformIdOutputCommands()
    {
        var c = new ViewportRenderCommandContainer(this);
        c.Add<VPRC_RenderQuadToFBO>().SetTargets(TransformIdDebugFBOName, null);
        return c;
    }

    private ViewportRenderCommandContainer CreateSurfelDebugOutputCommands()
    {
        var c = new ViewportRenderCommandContainer(this);
        // Create an FBO that uses the surfel debug output texture
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            SurfelDebugFBOName,
            CreateSurfelDebugOutputFBO,
            GetDesiredFBOSizeInternal);
        c.Add<VPRC_RenderQuadToFBO>().SetTargets(SurfelDebugFBOName, null);
        return c;
    }

    #endregion

    #region Texture Creation

    private void CacheTextures(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthStencilTextureName,
            CreateDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthViewTextureName,
            CreateDepthViewTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AlbedoOpacityTextureName,
            CreateAlbedoOpacityTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            NormalTextureName,
            CreateNormalTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TransformIdTextureName,
            CreateTransformIdTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HDRSceneTextureName,
            CreateHDRSceneTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            SurfelDebugOutputTextureName,
            CreateSurfelDebugOutputTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);
    }

    private XRTexture CreateDepthStencilTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Depth24Stencil8,
            EPixelFormat.DepthStencil,
            EPixelType.UnsignedInt248,
            EFrameBufferAttachment.DepthStencilAttachment);
        t.Resizable = false;
        t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.Name = DepthStencilTextureName;
        t.SamplerName = DepthStencilTextureName;
        return t;
    }

    private XRTexture CreateDepthViewTexture()
    {
        return new XRTexture2DView(
            GetTexture<XRTexture2D>(DepthStencilTextureName)!,
            0u, 1u,
            ESizedInternalFormat.Depth24Stencil8,
            false, false)
        {
            DepthStencilViewFormat = EDepthStencilFmt.Depth,
            Name = DepthViewTextureName,
            SamplerName = DepthViewTextureName,
        };
    }

    private XRTexture CreateAlbedoOpacityTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat);
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.Name = AlbedoOpacityTextureName;
        t.SamplerName = AlbedoOpacityTextureName;
        return t;
    }

    private XRTexture CreateNormalTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Rgb16f,
            EPixelFormat.Rgb,
            EPixelType.HalfFloat);
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.Name = NormalTextureName;
        t.SamplerName = NormalTextureName;
        return t;
    }

    private XRTexture CreateTransformIdTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.R32ui,
            EPixelFormat.RedInteger,
            EPixelType.UnsignedInt);
        t.MinFilter = ETexMinFilter.Nearest;
        t.MagFilter = ETexMagFilter.Nearest;
        t.Name = TransformIdTextureName;
        t.SamplerName = TransformIdTextureName;
        return t;
    }

    private XRTexture CreateHDRSceneTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.HalfFloat);
        t.MinFilter = ETexMinFilter.Linear;
        t.MagFilter = ETexMagFilter.Linear;
        t.Name = HDRSceneTextureName;
        t.SamplerName = HDRSceneTextureName;
        return t;
    }

    private XRTexture CreateSurfelDebugOutputTexture()
    {
        var t = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth, InternalHeight,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte);
        t.MinFilter = ETexMinFilter.Linear;
        t.MagFilter = ETexMagFilter.Linear;
        t.Name = SurfelDebugOutputTextureName;
        t.SamplerName = SurfelDebugOutputTextureName;
        return t;
    }

    #endregion

    #region FBO Creation

    private XRFrameBuffer CreateGBufferFBO()
    {
        if (GetTexture<XRTexture>(AlbedoOpacityTextureName) is not IFrameBufferAttachement albedoAttach)
            throw new InvalidOperationException("Albedo texture must be FBO-attachable.");

        if (GetTexture<XRTexture>(NormalTextureName) is not IFrameBufferAttachement normalAttach)
            throw new InvalidOperationException("Normal texture must be FBO-attachable.");

        if (GetTexture<XRTexture>(TransformIdTextureName) is not IFrameBufferAttachement transformIdAttach)
            throw new InvalidOperationException("TransformId texture must be FBO-attachable.");

        if (GetTexture<XRTexture>(DepthStencilTextureName) is not IFrameBufferAttachement depthAttach)
            throw new InvalidOperationException("Depth/Stencil texture must be FBO-attachable.");

        return new XRFrameBuffer(
            (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (transformIdAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
            (depthAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = GBufferFBOName
        };
    }

    private XRFrameBuffer CreateForwardPassFBO()
    {
        if (GetTexture<XRTexture>(HDRSceneTextureName) is not IFrameBufferAttachement hdrAttach)
            throw new InvalidOperationException("HDR scene texture must be FBO-attachable.");

        if (GetTexture<XRTexture>(DepthStencilTextureName) is not IFrameBufferAttachement depthAttach)
            throw new InvalidOperationException("Depth/Stencil texture must be FBO-attachable.");

        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "PassthroughHDR.fs"), EShaderType.Fragment);
        XRMaterial mat = new([GetTexture<XRTexture>(HDRSceneTextureName)], shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(mat)
        {
            Name = ForwardPassFBOName
        };
        fbo.SetRenderTargets(
            (hdrAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));

        return fbo;
    }

    private XRFrameBuffer CreateTransformIdDebugFBO()
    {
        XRTexture transformIdTexture = GetTexture<XRTexture>(TransformIdTextureName)!;
        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DebugTransformId.fs"), EShaderType.Fragment);
        XRMaterial mat = new([transformIdTexture], shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(mat)
        {
            Name = TransformIdDebugFBOName
        };
        fbo.SettingUniforms += TransformIdDebugFBO_SettingUniforms;
        return fbo;
    }

    private void TransformIdDebugFBO_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? transformId = GetTexture<XRTexture>(TransformIdTextureName);
        if (transformId is null)
            return;

        program.Sampler(TransformIdTextureName, transformId, 0);
        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);
    }

    private XRFrameBuffer CreateSurfelDebugFBO(ESurfelDebugVisualization mode)
    {
        XRTexture depthTexture = GetTexture<XRTexture>(DepthViewTextureName)!;
        XRTexture normalTexture = GetTexture<XRTexture>(NormalTextureName)!;
        XRTexture albedoTexture = GetTexture<XRTexture>(AlbedoOpacityTextureName)!;
        XRTexture transformIdTexture = GetTexture<XRTexture>(TransformIdTextureName)!;
        XRTexture hdrSceneTexture = GetTexture<XRTexture>(HDRSceneTextureName)!;

        string shaderName = mode switch
        {
            ESurfelDebugVisualization.SurfelCircles => "DebugSurfelCircles.fs",
            ESurfelDebugVisualization.SurfelGridCells => "DebugSurfelGrid.fs",
            _ => "DebugSurfelCircles.fs"
        };

        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, shaderName), EShaderType.Fragment);
        XRMaterial mat = new([depthTexture, normalTexture, albedoTexture, transformIdTexture, hdrSceneTexture], shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(mat)
        {
            Name = SurfelDebugFBOName
        };
        fbo.SettingUniforms += SurfelDebugFBO_SettingUniforms;
        return fbo;
    }

    private XRFrameBuffer CreateSurfelDebugOutputFBO()
    {
        // Simple passthrough FBO for the compute-generated surfel debug output
        XRTexture outputTexture = GetTexture<XRTexture>(SurfelDebugOutputTextureName)!;

        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "HudFBO.fs"), EShaderType.Fragment);
        XRMaterial mat = new([outputTexture], shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(mat)
        {
            Name = SurfelDebugFBOName
        };
        return fbo;
    }

    private void SurfelDebugFBO_SettingUniforms(XRRenderProgram program)
    {
        program.Sampler(DepthViewTextureName, GetTexture<XRTexture>(DepthViewTextureName)!, 0);
        program.Sampler(NormalTextureName, GetTexture<XRTexture>(NormalTextureName)!, 1);
        program.Sampler(AlbedoOpacityTextureName, GetTexture<XRTexture>(AlbedoOpacityTextureName)!, 2);
        program.Sampler(TransformIdTextureName, GetTexture<XRTexture>(TransformIdTextureName)!, 3);
        program.Sampler(HDRSceneTextureName, GetTexture<XRTexture>(HDRSceneTextureName)!, 4);

        program.Uniform("ScreenWidth", (float)InternalWidth);
        program.Uniform("ScreenHeight", (float)InternalHeight);

        var camera = State.SceneCamera;
        if (camera is not null)
        {
            Matrix4x4.Invert(camera.ProjectionMatrix, out var invProj);
            program.Uniform("InvProjectionMatrix", invProj);
            program.Uniform("CameraToWorldMatrix", camera.Transform.RenderMatrix);
        }
    }

    #endregion

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(GpuRenderDispatch))
            Engine.Rendering.ApplyGpuRenderDispatchToPipeline(this, GpuRenderDispatch);
    }
}
