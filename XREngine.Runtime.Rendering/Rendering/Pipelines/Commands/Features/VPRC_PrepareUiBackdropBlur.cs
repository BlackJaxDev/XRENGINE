using System;
using System.IO;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Pipeline-agnostic command that runs an on-demand 3-mip Dual-Kawase downsample/upsample
/// pyramid over the active pipeline's resolved scene color texture when visible screen-space UI
/// requests backdrop blur. Populates mip 0 (mild, 1/2 res), mip 1 (medium, 1/4 res), and mip 2
/// (deep, 1/8 res) of <see cref="UiBackdropBlurResources.OutputTextureName"/> so each UI quad
/// can sample its own continuous blur radius via <c>textureLod</c>. Supports mono 2D and stereo
/// layered multiview targets, and skips all GPU work when no visible UI quad requests blur.
/// </summary>
[RenderPipelineScriptCommand]
[RenderCommandInfo(ERenderCommandCategory.PostProcess, DoesGpuWork = true)]
public sealed class VPRC_PrepareUiBackdropBlur : ViewportRenderCommand
{
    private const string Downsample1PassName = "UiBackdropBlur.DS1";
    private const string Downsample2PassName = "UiBackdropBlur.DS2";
    private const string Downsample3PassName = "UiBackdropBlur.DS3";
    private const string Upsample2PassName = "UiBackdropBlur.US2";
    private const string Upsample1PassName = "UiBackdropBlur.US1";

    private const float ReferenceMaxBlurRadiusPx = 28.0f;

    /// <summary>
    /// Ordered fallback list of managed scene-color texture names to blur when
    /// <see cref="XRRenderPipelineInstance.RenderingState.ResolvedWindowPresentTexture"/> is null
    /// (for example on stereo/VR or custom pipelines).
    /// </summary>
    public string[]? SourceTextureNames { get; set; }

    /// <summary>
    /// Alias for <see cref="SourceTextureNames"/>.
    /// </summary>
    public string[]? FallbackSourceTextureNames
    {
        get => SourceTextureNames;
        set => SourceTextureNames = value;
    }

    /// <summary>
    /// Managed output texture name populated by this command. Defaults to
    /// <see cref="UiBackdropBlurResources.OutputTextureName"/>.
    /// </summary>
    public string OutputTextureName { get; set; } = UiBackdropBlurResources.OutputTextureName;

    private XRQuadFrameBuffer? _ds1Quad;
    private XRTexture? _ds1BoundSource;
    private bool _ds1Stereo;

    private XRQuadFrameBuffer? _ds2Quad;
    private XRTexture? _ds2BoundSource;
    private bool _ds2Stereo;

    private XRQuadFrameBuffer? _ds3Quad;
    private XRTexture? _ds3BoundSource;
    private bool _ds3Stereo;

    private XRQuadFrameBuffer? _us2Quad;
    private XRTexture? _us2BoundSource;
    private bool _us2Stereo;

    private XRQuadFrameBuffer? _us1Quad;
    private XRTexture? _us1BoundSource;
    private bool _us1Stereo;

    private XRTextureViewBase? _mip2SourceView;
    private XRTexture? _mip2SourceOwner;

    private bool _quadsPrewarmed;

    public override void DescribeRequirements(RenderPipelineRequirements requirements)
        => requirements.RequireOperation("fullscreen-quad");

    protected override bool ShouldExecuteThisFrame()
    {
        if (WebPipelineRasterProgram.IsActive)
            return false;

        XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (instance is null)
            return false;

        XRTexture? outputTex = instance.GetTexture<XRTexture>(OutputTextureName);
        XRTexture? halfScratch = instance.GetTexture<XRTexture>(UiBackdropBlurResources.HalfScratchTextureName);
        XRTexture? quarterScratch = instance.GetTexture<XRTexture>(UiBackdropBlurResources.QuarterScratchTextureName);
        if (outputTex is null || halfScratch is null || quarterScratch is null)
            return false;

        bool stereo = outputTex is XRTexture2DArray;
        bool targetsMatch = ReferenceEquals(_ds2BoundSource, halfScratch) &&
            ReferenceEquals(_ds3BoundSource, quarterScratch) &&
            ReferenceEquals(_mip2SourceOwner, outputTex) &&
            _ds1Stereo == stereo;
        if (!_quadsPrewarmed || !targetsMatch)
            return true;

        var ui = instance.RenderState.ScreenSpaceUserInterface;
        if (ui is null || !ui.IsActive)
            return false;

        return ui is XREngine.Components.UICanvasComponent canvas &&
            canvas.BatchCollector is { Enabled: true } collector &&
            collector.MaxRequestedBackdropBlurRadius > 0.001f;
    }

    protected override void Execute()
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        instance.RenderState.UiBackdropBlurTexture = null;

        if (WebPipelineRasterProgram.IsActive)
            return;

        XRTexture? outputTex = instance.GetTexture<XRTexture>(OutputTextureName);
        XRTexture? halfScratch = instance.GetTexture<XRTexture>(UiBackdropBlurResources.HalfScratchTextureName);
        XRTexture? quarterScratch = instance.GetTexture<XRTexture>(UiBackdropBlurResources.QuarterScratchTextureName);
        XRFrameBuffer? halfScratchFbo = instance.GetFBO<XRFrameBuffer>(UiBackdropBlurResources.HalfScratchFboName);
        XRFrameBuffer? quarterScratchFbo = instance.GetFBO<XRFrameBuffer>(UiBackdropBlurResources.QuarterScratchFboName);
        XRFrameBuffer? mip0Fbo = instance.GetFBO<XRFrameBuffer>(UiBackdropBlurResources.Mip0FboName);
        XRFrameBuffer? mip1Fbo = instance.GetFBO<XRFrameBuffer>(UiBackdropBlurResources.Mip1FboName);
        XRFrameBuffer? mip2Fbo = instance.GetFBO<XRFrameBuffer>(UiBackdropBlurResources.Mip2FboName);
        if (outputTex is null || halfScratch is null || quarterScratch is null ||
            halfScratchFbo is null || quarterScratchFbo is null || mip0Fbo is null)
        {
            return;
        }

        bool stereo = outputTex is XRTexture2DArray;
        if (!TryResolveSourceTexture(instance, stereo, out XRTexture? sourceTex, out bool flipSourceY))
            return;

        (uint halfW, uint halfH) = GetTextureDimensions(outputTex, mipLevel: 0);
        uint mipCount = UiBackdropBlurResources.ResolveMipLevelCount(halfW, halfH);
        bool hasThreeMips = mipCount >= 3u && mip1Fbo is not null && mip2Fbo is not null;

        EnsureQuad(ref _ds1Quad, ref _ds1BoundSource, ref _ds1Stereo, sourceTex!, isUpsample: false, stereo, "UiBackdropBlurDS1");
        EnsureQuad(ref _us1Quad, ref _us1BoundSource, ref _us1Stereo, halfScratch, isUpsample: true, stereo, "UiBackdropBlurUS1");

        bool allReady = PrepareQuad(_ds1Quad!, stereo) & PrepareQuad(_us1Quad!, stereo);

        if (hasThreeMips)
        {
            XRTexture mip2View = EnsureMip2SourceView(outputTex);
            EnsureQuad(ref _ds2Quad, ref _ds2BoundSource, ref _ds2Stereo, halfScratch, isUpsample: false, stereo, "UiBackdropBlurDS2");
            EnsureQuad(ref _ds3Quad, ref _ds3BoundSource, ref _ds3Stereo, quarterScratch, isUpsample: false, stereo, "UiBackdropBlurDS3");
            EnsureQuad(ref _us2Quad, ref _us2BoundSource, ref _us2Stereo, mip2View, isUpsample: true, stereo, "UiBackdropBlurUS2");

            allReady &= PrepareQuad(_ds2Quad!, stereo) &
                PrepareQuad(_ds3Quad!, stereo) &
                PrepareQuad(_us2Quad!, stereo);
        }

        _quadsPrewarmed = allReady;
        if (!allReady)
            return;

        var ui = instance.RenderState.ScreenSpaceUserInterface;
        float maxRadius = ui?.IsActive == true &&
            ui is XREngine.Components.UICanvasComponent canvas &&
            canvas.BatchCollector is { Enabled: true } collector
            ? collector.MaxRequestedBackdropBlurRadius
            : 0.0f;
        if (maxRadius <= 0.001f)
            return;

        float blurFactor = Math.Clamp(maxRadius / ReferenceMaxBlurRadiusPx, 0.0f, 1.0f);
        (uint srcW, uint srcH) = GetTextureDimensions(sourceTex!, mipLevel: 0);
        (uint quarterW, uint quarterH) = GetTextureDimensions(outputTex, mipLevel: 1);
        (uint eighthW, uint eighthH) = GetTextureDimensions(outputTex, mipLevel: 2);

        // On Vulkan with a negative-height Y-up viewport, every fullscreen FBO-to-FBO pass maps
        // FragPos.y = +1 to texture row 0, so sampling another FBO texture must invert V on each
        // intermediate pass to keep all pyramid mips upright.
        bool flipIntermediateY = RenderClipSpacePolicy.RequiresVulkanFramebufferTexturePresentationYFlip();

        // Pass 1: SceneColor -> HalfScratch (1/2 res)
        RunPass(instance, Downsample1PassName, _ds1Quad!, srcW, srcH, halfScratchFbo, halfW, halfH, 0.9f * blurFactor, flipSourceY);

        if (hasThreeMips)
        {
            // Pass 2: HalfScratch (1/2) -> QuarterScratch (1/4)
            RunPass(instance, Downsample2PassName, _ds2Quad!, halfW, halfH, quarterScratchFbo, quarterW, quarterH, 1.25f * blurFactor, flipIntermediateY);
            // Pass 3: QuarterScratch (1/4) -> UiBackdropBlurTexture mip 2 (1/8, deep blur)
            RunPass(instance, Downsample3PassName, _ds3Quad!, quarterW, quarterH, mip2Fbo!, eighthW, eighthH, 1.55f * blurFactor, flipIntermediateY);
            // Pass 4: UiBackdropBlurTexture mip 2 (1/8) -> mip 1 (1/4, smooth medium Dual-Kawase blur)
            RunPass(instance, Upsample2PassName, _us2Quad!, eighthW, eighthH, mip1Fbo!, quarterW, quarterH, 1.15f * blurFactor, flipIntermediateY);
        }

        // Pass 5: HalfScratch (1/2) -> UiBackdropBlurTexture mip 0 (1/2, smooth mild blur)
        RunPass(instance, Upsample1PassName, _us1Quad!, halfW, halfH, mip0Fbo, halfW, halfH, 0.75f * blurFactor, flipIntermediateY);

        instance.RenderState.UiBackdropBlurTexture = outputTex;
    }

    private XRTexture EnsureMip2SourceView(XRTexture outputTex)
    {
        if (_mip2SourceView is not null && ReferenceEquals(_mip2SourceOwner, outputTex))
            return _mip2SourceView;

        _mip2SourceView?.Destroy();
        _mip2SourceView = null;
        _mip2SourceOwner = null;

        if (outputTex is XRTexture2DArray arrayTex)
        {
            _mip2SourceView = new XRTexture2DArrayView(
                arrayTex,
                minLevel: 2u,
                numLevels: 1u,
                minLayer: 0u,
                numLayers: Math.Max(arrayTex.Depth, 1u),
                arrayTex.SizedInternalFormat,
                array: arrayTex.Depth > 1u,
                arrayTex.MultiSample)
            {
                Name = $"{OutputTextureName}.Mip2.SourceView",
                SamplerName = "SourceTexture",
                MagFilter = ETexMagFilter.Linear,
                MinFilter = ETexMinFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
            };
        }
        else if (outputTex is XRTexture2D tex2D)
        {
            _mip2SourceView = new XRTexture2DView(
                tex2D,
                minLevel: 2u,
                numLevels: 1u,
                tex2D.SizedInternalFormat,
                array: false,
                tex2D.MultiSample)
            {
                Name = $"{OutputTextureName}.Mip2.SourceView",
                SamplerName = "SourceTexture",
                MagFilter = ETexMagFilter.Linear,
                MinFilter = ETexMinFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
            };
        }
        else
        {
            throw new InvalidOperationException($"Unsupported UI backdrop blur texture type '{outputTex.GetType().Name}'.");
        }

        _mip2SourceOwner = outputTex;
        return _mip2SourceView;
    }

    private static bool PrepareQuad(XRQuadFrameBuffer quad, bool stereo)
    {
        XRMeshRenderer mesh = quad.FullScreenMesh;
        mesh.GetVersion(forceNoStereo: !stereo).Generate();
        return mesh.TryPrepareForRendering(forceNoStereo: !stereo);
    }

    private bool TryResolveSourceTexture(
        XRRenderPipelineInstance instance,
        bool requireStereoArray,
        out XRTexture? texture,
        out bool flipYOnVulkan)
    {
        if (!requireStereoArray && instance.RenderState.ResolvedWindowPresentTexture is { } presentTex)
        {
            texture = presentTex;
            flipYOnVulkan = instance.RenderState.ResolvedWindowPresentFlipY;
            return true;
        }

        if (SourceTextureNames is { Length: > 0 } names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                XRTexture? candidate = instance.GetTexture<XRTexture>(names[i]);
                if (candidate is null)
                    continue;
                if (requireStereoArray && candidate is not XRTexture2DArray)
                    continue;
                if (!requireStereoArray && candidate is not XRTexture2D)
                    continue;

                texture = candidate;
                flipYOnVulkan = RenderClipSpacePolicy.RequiresVulkanFramebufferTexturePresentationYFlip();
                return true;
            }
        }

        texture = null;
        flipYOnVulkan = false;
        return false;
    }

    private static (uint Width, uint Height) GetTextureDimensions(XRTexture texture, int mipLevel)
    {
        Vector3 dims = texture.WidthHeightDepth;
        uint baseW = Math.Max(1u, (uint)dims.X);
        uint baseH = Math.Max(1u, (uint)dims.Y);
        int shift = Math.Clamp(mipLevel, 0, 30);
        return (Math.Max(1u, baseW >> shift), Math.Max(1u, baseH >> shift));
    }

    private void RunPass(
        XRRenderPipelineInstance instance,
        string passName,
        XRQuadFrameBuffer quad,
        uint sourceWidth,
        uint sourceHeight,
        XRFrameBuffer targetFbo,
        uint targetWidth,
        uint targetHeight,
        float blurOffset,
        bool flipSourceY)
    {
        XRMaterial? material = quad.Material;
        if (material is null)
            return;

        Vector2 sourceTexelSize = new(
            1.0f / Math.Max(1u, sourceWidth),
            1.0f / Math.Max(1u, sourceHeight));
        material.SetVector2("SourceTexelSize", sourceTexelSize);
        material.SetFloat("BlurOffset", blurOffset);
        if (material.Parameter<ShaderInt>("FlipSourceYOnVulkan") is not null)
            material.SetInt("FlipSourceYOnVulkan", flipSourceY ? 1 : 0);

        int passIndex = ResolveActiveRenderGraphPassIndex(passName);
        using var passScope = passIndex != int.MinValue
            ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
            : default;
        BoundingRectangle targetRect = new(0, 0, (int)Math.Max(1u, targetWidth), (int)Math.Max(1u, targetHeight));
        using (instance.RenderState.PushRenderArea(targetRect))
        using (targetFbo.BindForWritingState())
        {
            quad.EnqueueRender();
        }
    }

    private static void EnsureQuad(
        ref XRQuadFrameBuffer? quad,
        ref XRTexture? boundSource,
        ref bool boundStereo,
        XRTexture sourceTexture,
        bool isUpsample,
        bool stereo,
        string name)
    {
        if (quad is not null && ReferenceEquals(boundSource, sourceTexture) && boundStereo == stereo)
            return;

        if (quad is not null)
        {
            XRMaterial? oldMat = quad.Material;
            quad.Destroy();
            oldMat?.Destroy();
            quad = null;
        }

        string shaderFile = (isUpsample, stereo) switch
        {
            (false, false) => "UiBackdropBlurDownsample.fs",
            (true, false) => "UiBackdropBlurUpsample.fs",
            (false, true) => "UiBackdropBlurDownsampleStereo.fs",
            (true, true) => "UiBackdropBlurUpsampleStereo.fs",
        };
        string shaderPath = Path.Combine("UI", shaderFile);
        XRShader fragmentShader = XRShader.EngineShader(shaderPath, EShaderType.Fragment);
        ShaderVar[] uniforms =
        [
            new ShaderVector2(Vector2.One, "SourceTexelSize"),
            new ShaderFloat(1.0f, "BlurOffset"),
            new ShaderInt(0, "FlipSourceYOnVulkan"),
        ];

        XRMaterial material = new(uniforms, [sourceTexture], [fragmentShader])
        {
            Name = $"{name}Material",
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                StencilTest = new StencilTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                MissingTextureFallback = EMissingTextureFallback.Black,
            },
        };

        quad = new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false, useMultiview: stereo)
        {
            Name = name,
        };
        quad.FullScreenMesh.GenerationPriority = EMeshGenerationPriority.RenderPipeline;
        quad.FullScreenMesh.CaptureUniformsOnRender = true;
        quad.FullScreenMesh.EnsureRenderPipelineVersionsCreated();
        boundSource = sourceTexture;
        boundStereo = stereo;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        bool declared = context.ResourceLayout is null ||
            (context.HasResource(OutputTextureName) &&
             context.HasResource(UiBackdropBlurResources.HalfScratchTextureName) &&
             context.HasResource(UiBackdropBlurResources.QuarterScratchTextureName));

        if (!declared)
        {
            context.ReserveSyntheticPassIndex(Downsample1PassName);
            context.ReserveSyntheticPassIndex(Downsample2PassName);
            context.ReserveSyntheticPassIndex(Downsample3PassName);
            context.ReserveSyntheticPassIndex(Upsample2PassName);
            context.ReserveSyntheticPassIndex(Upsample1PassName);
            return;
        }

        uint mipCount = UiBackdropBlurResources.MaxPyramidMipCount;
        if (context.ResourceProfile is { } profile)
        {
            (uint halfW, uint halfH) = RenderResourceSizePolicy.Window(0.5f).Resolve(profile);
            mipCount = UiBackdropBlurResources.ResolveMipLevelCount(halfW, halfH);
        }

        string outputRes = MakeTextureResource(OutputTextureName);
        string halfScratchRes = MakeTextureResource(UiBackdropBlurResources.HalfScratchTextureName);
        string quarterScratchRes = MakeTextureResource(UiBackdropBlurResources.QuarterScratchTextureName);

        var ds1 = context.GetOrCreateSyntheticPass(Downsample1PassName, ERenderGraphPassStage.Graphics);
        if (SourceTextureNames is { Length: > 0 } sources)
        {
            for (int i = 0; i < sources.Length; i++)
            {
                if (context.HasResource(sources[i]))
                    ds1.SampleTexture(MakeTextureResource(sources[i]));
            }
        }
        ds1.UseColorAttachment(halfScratchRes, ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);

        if (mipCount >= 3u)
        {
            var ds2 = context.GetOrCreateSyntheticPass(Downsample2PassName, ERenderGraphPassStage.Graphics);
            ds2.SampleTexture(halfScratchRes);
            ds2.UseColorAttachment(quarterScratchRes, ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);

            var ds3 = context.GetOrCreateSyntheticPass(Downsample3PassName, ERenderGraphPassStage.Graphics);
            ds3.SampleTexture(quarterScratchRes);
            ds3.UseColorAttachmentMip(outputRes, 2u, ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);

            var us2 = context.GetOrCreateSyntheticPass(Upsample2PassName, ERenderGraphPassStage.Graphics);
            us2.SampleTextureMip(outputRes, 2u);
            us2.UseColorAttachmentMip(outputRes, 1u, ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
        }
        else
        {
            context.ReserveSyntheticPassIndex(Downsample2PassName);
            context.ReserveSyntheticPassIndex(Downsample3PassName);
            context.ReserveSyntheticPassIndex(Upsample2PassName);
        }

        var us1 = context.GetOrCreateSyntheticPass(Upsample1PassName, ERenderGraphPassStage.Graphics);
        us1.SampleTexture(halfScratchRes);
        us1.UseColorAttachmentMip(outputRes, 0u, ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
    }

    internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
    {
        DestroyQuad(ref _ds1Quad, ref _ds1BoundSource);
        DestroyQuad(ref _ds2Quad, ref _ds2BoundSource);
        DestroyQuad(ref _ds3Quad, ref _ds3BoundSource);
        DestroyQuad(ref _us2Quad, ref _us2BoundSource);
        DestroyQuad(ref _us1Quad, ref _us1BoundSource);
        _mip2SourceView?.Destroy();
        _mip2SourceView = null;
        _mip2SourceOwner = null;
        _quadsPrewarmed = false;
        base.ReleaseContainerResources(instance);
    }

    private static void DestroyQuad(ref XRQuadFrameBuffer? quad, ref XRTexture? source)
    {
        if (quad is not null)
        {
            XRMaterial? mat = quad.Material;
            quad.Destroy();
            mat?.Destroy();
            quad = null;
        }
        source = null;
    }
}
