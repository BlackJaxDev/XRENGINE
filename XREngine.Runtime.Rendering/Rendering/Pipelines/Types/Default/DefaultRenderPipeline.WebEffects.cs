using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Scene;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline : IRenderPipelineAmbientOcclusionProvider
{
    public const string WebNormalTextureName = "WebNormalTexture";
    public const string WebNormalFboName = "WebNormalFBO";
    public const string WebGtaoRawTextureName = "WebGtaoRawTexture";
    public const string WebGtaoHorizontalTextureName = "WebGtaoHorizontalTexture";
    public const string WebGtaoFinalTextureName = "WebGtaoFinalTexture";
    public const string WebBloomCombinedTextureName = "WebBloomCombinedTexture";

    private const string WebGtaoRawFboName = "WebGtaoRawFBO";
    private const string WebGtaoHorizontalFboName = "WebGtaoHorizontalFBO";
    private const string WebGtaoFinalFboName = "WebGtaoFinalFBO";
    private const string WebGtaoGenerateQuadName = "WebGtaoGenerateQuad";
    private const string WebGtaoHorizontalQuadName = "WebGtaoHorizontalQuad";
    private const string WebGtaoVerticalQuadName = "WebGtaoVerticalQuad";
    private const string WebBloomCombinedFboName = "WebBloomCombinedFBO";
    private const string WebBloomCopyQuadName = "WebBloomCopyQuad";
    private const string WebBloomCombineQuadName = "WebBloomCombineQuad";
    private const string WebBloomDebugOutputQuadName = "WebBloomDebugOutputQuad";
    private static readonly string[] WebBloomMipTextureNames =
        ["WebBloomMip0", "WebBloomMip1", "WebBloomMip2", "WebBloomMip3", "WebBloomMip4"];
    private static readonly string[] WebBloomMipSamplerNames =
        ["BloomMip0", "BloomMip1", "BloomMip2", "BloomMip3", "BloomMip4"];
    private static readonly string[] WebBloomMipFboNames =
        ["WebBloomMip0FBO", "WebBloomMip1FBO", "WebBloomMip2FBO", "WebBloomMip3FBO", "WebBloomMip4FBO"];
    private static readonly string[] WebBloomDownQuadNames =
        ["", "WebBloomDown1Quad", "WebBloomDown2Quad", "WebBloomDown3Quad", "WebBloomDown4Quad"];
    private static readonly string[] WebBloomUpQuadNames =
        ["", "WebBloomUp1Quad", "WebBloomUp2Quad", "WebBloomUp3Quad", ""];
    private XRMaterial? _webDepthNormalPrePassMaterial;

    private ulong BuildWebResourceFeatureMask(XRRenderPipelineInstance instance, XRViewport? viewport)
    {
        XRCamera? camera = instance.RenderState.SceneCamera ?? instance.RenderState.RenderingCamera
            ?? instance.LastSceneCamera ?? instance.LastRenderingCamera ?? viewport?.ActiveCamera;
        PipelinePostProcessState? state = camera?.GetPostProcessState(this);
        AmbientOcclusionSettings? ao = GetSettings<AmbientOcclusionSettings>(state);
        BloomSettings? bloom = GetSettings<BloomSettings>(state);
        DefaultPipelineResourceFeature mask = DefaultPipelineResourceFeature.WebForwardLit;
        if (RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.EnableGtao && ao is { Enabled: true })
        {
            if (GetWebAmbientOcclusionRejection(ao) is { } reason)
                throw new NotSupportedException($"WebGPU.DefaultPipeline.AmbientOcclusionModeUnsupported: {reason}");
            mask |= DefaultPipelineResourceFeature.WebGtaoEnabled;
            mask |= ao.GroundTruth.Resolution switch
            {
                GroundTruthAmbientOcclusionSettings.EResolution.Full => DefaultPipelineResourceFeature.GtaoFullResolution,
                GroundTruthAmbientOcclusionSettings.EResolution.Half => DefaultPipelineResourceFeature.None,
                GroundTruthAmbientOcclusionSettings.EResolution.Quarter => DefaultPipelineResourceFeature.GtaoQuarterResolution,
                _ => throw new NotSupportedException($"WebGPU.DefaultPipeline.GtaoResolutionUnsupported: {ao.GroundTruth.Resolution}."),
            };
        }
        if (RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.EnableBloom && bloom is { Enabled: true })
            mask |= DefaultPipelineResourceFeature.WebBloomEnabled;
        if (GetSettings<ColorGradingSettings>(state) is { RequiresAutoExposure: true })
            mask |= DefaultPipelineResourceFeature.WebAutoExposureEnabled;
        return (ulong)mask;
    }

    private static bool WebGtaoEnabled(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.WebGtaoEnabled) != 0;

    private static bool WebBloomEnabled(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.WebBloomEnabled) != 0;

    private static bool WebAutoExposureEnabled(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.WebAutoExposureEnabled) != 0;

    private bool ShouldUseWebAutoExposure()
        => GetSettings<ColorGradingSettings>(ResolveCurrentSettingsCamera()?.GetPostProcessState(this)) is { RequiresAutoExposure: true };

    private static uint WebGtaoDivisor(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.GtaoQuarterResolution) != 0 ? 4u :
            (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.GtaoFullResolution) != 0 ? 1u : 2u;

    private static uint DivideRoundedUp(uint size, uint divisor)
        => Math.Max(1u, (size + divisor - 1u) / divisor);

    private static int WebBloomMaximumLevel(RenderPipelineResourceProfile profile)
        => checked((int)ResolveBloomMipLevelCount(profile.InternalWidth, profile.InternalHeight) - 1);

    private void DeclareWebEffectResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourceProfile profile = builder.Profile;
        if (WebGtaoEnabled(profile))
        {
            bool multisampled = WebMsaaEnabled(profile);
            string normal = multisampled ? WebMsaaNormalTextureName : WebNormalTextureName;
            string depth = multisampled ? WebMsaaDepthTextureName : DepthStencilTextureName;
            DeclareWebColorTexture(builder, WebNormalTextureName, RenderResourceSizePolicy.Internal(),
                () => CreateWebColorTexture(WebNormalTextureName, InternalWidth, InternalHeight));
            builder.FrameBuffer(WebNormalFboName).Size(RenderResourceSizePolicy.Internal())
                .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
                .Color(0, normal).Depth(depth)
                .Factory(() => CreateWebColorFbo(WebNormalFboName, normal, depth)).Add();
            if (multisampled)
                DeclareWebMsaaNormalResolve(builder);

            uint divisor = WebGtaoDivisor(profile);
            RenderResourceSizePolicy reduced = RenderResourceSizePolicy.InternalDividedRoundedUp(divisor);
            uint reducedWidth = DivideRoundedUp(profile.InternalWidth, divisor);
            uint reducedHeight = DivideRoundedUp(profile.InternalHeight, divisor);
            DeclareWebColorTexture(builder, WebGtaoRawTextureName, reduced,
                () => CreateWebColorTexture(WebGtaoRawTextureName, reducedWidth, reducedHeight));
            DeclareWebColorTexture(builder, WebGtaoHorizontalTextureName, reduced,
                () => CreateWebColorTexture(WebGtaoHorizontalTextureName, reducedWidth, reducedHeight));
            DeclareWebColorTexture(builder, WebGtaoFinalTextureName, RenderResourceSizePolicy.Internal(),
                () => CreateWebColorTexture(WebGtaoFinalTextureName, InternalWidth, InternalHeight));
            DeclareWebColorFbo(builder, WebGtaoRawFboName, WebGtaoRawTextureName, reduced);
            DeclareWebColorFbo(builder, WebGtaoHorizontalFboName, WebGtaoHorizontalTextureName, reduced);
            DeclareWebColorFbo(builder, WebGtaoFinalFboName, WebGtaoFinalTextureName, RenderResourceSizePolicy.Internal());
            builder.QuadMaterial(WebGtaoGenerateQuadName)
                .DependsOn(DepthStencilTextureName).DependsOn(WebNormalTextureName)
                .Factory(() => CreateWebEffectQuad(WebGtaoGenerateQuadName, "gtao-generate", SetWebGtaoGenerateUniforms)).Add();
            builder.QuadMaterial(WebGtaoHorizontalQuadName)
                .DependsOn(WebGtaoRawTextureName).DependsOn(DepthStencilTextureName).DependsOn(WebNormalTextureName)
                .Factory(() => CreateWebEffectQuad(WebGtaoHorizontalQuadName, "gtao-blur-horizontal", SetWebGtaoHorizontalUniforms)).Add();
            builder.QuadMaterial(WebGtaoVerticalQuadName)
                .DependsOn(WebGtaoHorizontalTextureName).DependsOn(DepthStencilTextureName).DependsOn(WebNormalTextureName)
                .Factory(() => CreateWebEffectQuad(WebGtaoVerticalQuadName, "gtao-blur-vertical", SetWebGtaoVerticalUniforms)).Add();
        }

        if (!WebBloomEnabled(profile))
            return;
        int maxLevel = WebBloomMaximumLevel(profile);
        for (int level = 0; level <= maxLevel; level++)
        {
            int selectedLevel = level;
            string textureName = WebBloomMipTextureNames[selectedLevel];
            uint width = Math.Max(1u, profile.InternalWidth >> selectedLevel);
            uint height = Math.Max(1u, profile.InternalHeight >> selectedLevel);
            RenderResourceSizePolicy size = RenderResourceSizePolicy.Absolute(width, height);
            DeclareWebColorTexture(builder, textureName, size,
                () => CreateWebColorTexture(textureName, width, height));
            DeclareWebColorFbo(builder, WebBloomMipFboNames[selectedLevel], textureName, size);
        }
        DeclareWebColorTexture(builder, WebBloomCombinedTextureName, RenderResourceSizePolicy.Internal(),
            () => CreateWebColorTexture(WebBloomCombinedTextureName, InternalWidth, InternalHeight));
        DeclareWebColorFbo(builder, WebBloomCombinedFboName, WebBloomCombinedTextureName, RenderResourceSizePolicy.Internal());
        builder.QuadMaterial(WebBloomCopyQuadName).DependsOn(HDRSceneTextureName)
            .Factory(() => CreateWebEffectQuad(WebBloomCopyQuadName, "bloom-copy", SetWebBloomCopyUniforms)).Add();
        for (int level = 1; level <= maxLevel; level++)
        {
            int selectedLevel = level;
            string downQuad = WebBloomDownQuadNames[level];
            builder.QuadMaterial(downQuad).DependsOn(WebBloomMipTextureNames[level - 1])
                .Factory(() => CreateWebEffectQuad(downQuad, "bloom-downsample",
                    program => SetWebBloomDownUniforms(program, selectedLevel))).Add();
            if (level < maxLevel)
            {
                string upQuad = WebBloomUpQuadNames[level];
                builder.QuadMaterial(upQuad).DependsOn(WebBloomMipTextureNames[level + 1])
                    .Factory(() => CreateWebEffectQuad(upQuad, "bloom-upsample",
                        program => SetWebBloomUpUniforms(program, selectedLevel), additive: true)).Add();
            }
        }
        var combine = builder.QuadMaterial(WebBloomCombineQuadName).DependsOn(HDRSceneTextureName);
        for (int level = 0; level <= maxLevel; level++)
            combine.DependsOn(WebBloomMipTextureNames[level]);
        combine.Factory(() => CreateWebEffectQuad(WebBloomCombineQuadName, "bloom-combine", SetWebBloomCombineUniforms)).Add();
        builder.QuadMaterial(WebBloomDebugOutputQuadName).DependsOn(WebBloomCombinedTextureName)
            .Factory(() => CreateWebEffectQuad(WebBloomDebugOutputQuadName, "bloom-copy", SetWebBloomDebugOutputUniforms)).Add();
    }

    private static void DeclareWebColorTexture(RenderPipelineResourceLayoutBuilder builder, string name,
        RenderResourceSizePolicy size, Func<XRTexture> factory)
        => builder.Texture(name).Size(size)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f).Factory(factory).Add();

    private void DeclareWebColorFbo(RenderPipelineResourceLayoutBuilder builder, string fboName,
        string textureName, RenderResourceSizePolicy size)
        => builder.FrameBuffer(fboName).Size(size).Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, textureName).Factory(() => CreateWebColorFbo(fboName, textureName)).Add();

    private XRTexture CreateWebColorTexture(string name, uint width, uint height, uint samples = 1)
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(Math.Max(width, 1u), Math.Max(height, 1u),
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, EFrameBufferAttachment.ColorAttachment0);
        texture.Name = name;
        texture.MultiSampleCount = samples;
        texture.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        texture.Resizable = false;
        texture.AutoGenerateMipmaps = false;
        texture.MaxAnisotropy = 1.0f;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = texture.VWrap = ETexWrapMode.ClampToEdge;
        return texture;
    }

    private XRFrameBuffer CreateWebColorFbo(string name, string textureName, string? depthName = null)
    {
        XRTexture2D color = GetTexture<XRTexture2D>(textureName)!;
        XRFrameBuffer framebuffer = depthName is null
            ? new XRFrameBuffer((color, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            : new XRFrameBuffer((color, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (GetTexture<XRTexture2D>(depthName)!, EFrameBufferAttachment.DepthAttachment, 0, -1));
        framebuffer.Name = name;
        // The pending generation retains this logical owner before the renderer starts
        // asynchronous attachment preparation, so Pending cannot abandon its GPU request.
        return framebuffer;
    }

    private XRQuadFrameBuffer CreateWebEffectQuad(string name, string pass,
        DelSetUniforms publishUniforms, bool additive = false, bool writesDepth = false)
    {
        ShaderProgramArtifact artifact = GetRequiredWebPipelineArtifact(pass);
        XRShader vertex = new(EShaderType.Vertex) { CookedArtifact = artifact };
        XRShader fragment = new(EShaderType.Fragment) { CookedArtifact = artifact };
        XRMaterial material = new(Array.Empty<XRTexture?>(), vertex, fragment)
        {
            Name = name,
            RenderOptions = new RenderingParameters
            {
                DepthTest = { Enabled = writesDepth ? ERenderParamUsage.Enabled : ERenderParamUsage.Disabled,
                    UpdateDepth = writesDepth, Function = EComparison.Always },
                BlendModeAllDrawBuffers = additive
                    ? new BlendMode
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        RgbSrcFactor = EBlendingFactor.One,
                        RgbDstFactor = EBlendingFactor.One,
                        AlphaSrcFactor = EBlendingFactor.One,
                        AlphaDstFactor = EBlendingFactor.One,
                        RgbEquation = EBlendEquationMode.FuncAdd,
                        AlphaEquation = EBlendEquationMode.FuncAdd,
                    }
                    : BlendMode.Disabled(),
            },
        };
        try
        {
            XRQuadFrameBuffer quad = new(material, deriveRenderTargetsFromMaterial: false,
                prepareForInitialRendering: false) { Name = name };
            quad.SettingUniforms += publishUniforms;
            quad.Destroyed += resource =>
            {
                XRQuadFrameBuffer destroyed = (XRQuadFrameBuffer)resource;
                destroyed.SettingUniforms -= publishUniforms;
                XRMaterial? ownedMaterial = destroyed.Material;
                if (ownedMaterial is null)
                    return;
                ownedMaterial.Destroy(true);
                foreach (XRShader shader in ownedMaterial.Shaders)
                    shader.Destroy(true);
            };
            return quad;
        }
        catch
        {
            material.Destroy(true);
            vertex.Destroy(true);
            fragment.Destroy(true);
            throw;
        }
    }

    private XRMaterial GetWebDepthNormalPrePassMaterial()
    {
        if (_webDepthNormalPrePassMaterial is not null)
            return _webDepthNormalPrePassMaterial;
        ShaderProgramArtifact artifact = GetRequiredWebPipelineArtifact("depth-normal");
        XRShader vertex = new(EShaderType.Vertex) { CookedArtifact = artifact };
        XRShader fragment = new(EShaderType.Fragment) { CookedArtifact = artifact };
        XRMaterial material = new(Array.Empty<XRTexture?>(), vertex, fragment)
        {
            Name = "WebDepthNormalPrePassMaterial",
            RenderOptions = new RenderingParameters
            {
                DepthTest = { Enabled = ERenderParamUsage.Enabled, UpdateDepth = true, Function = EComparison.Lequal },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.None,
            },
        };
        SetField(ref _webDepthNormalPrePassMaterial, material, publishNotifications: false);
        return material;
    }

    private void DestroyWebDepthNormalPrePassMaterial()
    {
        XRMaterial? material = _webDepthNormalPrePassMaterial;
        if (material is null)
            return;
        SetField(ref _webDepthNormalPrePassMaterial, null, publishNotifications: false);
        material.Destroy(true);
        foreach (XRShader shader in material.Shaders)
            shader.Destroy(true);
    }

    private static XRRenderPipelineInstance RequireWebEffectInstance()
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline
           ?? throw new InvalidOperationException("WebGPU.DefaultPipeline.EffectPipelineMissing: no active pipeline instance.");

    private static XRTexture2D RequireWebEffectTexture(string name)
        => RequireWebEffectInstance().GetTexture<XRTexture2D>(name)
           ?? throw new InvalidOperationException($"WebGPU.DefaultPipeline.EffectTextureMissing: '{name}' is not in the committed generation.");

    private AmbientOcclusionSettings RequireWebGtaoSettings()
    {
        AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings();
        if (settings is not { Enabled: true } ||
            AmbientOcclusionSettings.NormalizeType(settings.Type) != AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion)
            throw new InvalidOperationException("WebGPU.DefaultPipeline.GtaoSettingsMismatch: active settings require a different resource generation.");
        return settings;
    }

    private BloomSettings RequireWebBloomSettings()
    {
        PipelinePostProcessState? state = ResolveCurrentSettingsCamera()?.GetPostProcessState(this);
        BloomSettings? settings = GetSettings<BloomSettings>(state);
        if (settings is not { Enabled: true })
            throw new InvalidOperationException("WebGPU.DefaultPipeline.BloomSettingsMismatch: active settings require a different resource generation.");
        return settings;
    }

    private void SetWebGtaoGenerateUniforms(XRRenderProgram program)
    {
        AmbientOcclusionSettings settings = RequireWebGtaoSettings();
        XRCamera camera = ResolveCurrentSettingsCamera()
            ?? throw new InvalidOperationException("WebGPU.DefaultPipeline.GtaoCameraMissing: GTAO requires a scene camera.");
        XRTexture2D raw = RequireWebEffectTexture(WebGtaoRawTextureName);
        XRTexture2D depth = RequireWebEffectTexture(DepthStencilTextureName);
        RenderFrameViewSelection view = WebPipelineRasterProgram.CaptureView(camera, new Vector2(raw.Width, raw.Height));
        if (!Matrix4x4.Invert(view.ProjectionMatrix, out Matrix4x4 inverseProjection))
            throw new NotSupportedException("WebGPU.DefaultPipeline.GtaoProjectionInvalid: the captured projection must be invertible.");
        program.Uniform("ViewMatrix", view.View.ViewMatrix);
        program.Uniform("InverseProjMatrix", inverseProjection);
        program.Uniform("ProjMatrix", view.ProjectionMatrix);
        program.Uniform("OutputSize", new Vector2(raw.Width, raw.Height));
        program.Uniform("DepthSize", new Vector2(depth.Width, depth.Height));
        program.Uniform("Radius", MathF.Max(settings.Radius, 0.001f));
        program.Uniform("Bias", MathF.Max(settings.Bias, 0.0f));
        program.Uniform("FalloffStartRatio", MathF.Max(settings.GroundTruth.FalloffStartRatio, 0.001f));
        program.Uniform("ThicknessHeuristic", Math.Clamp(settings.GroundTruth.ThicknessHeuristic, 0.0f, 1.0f));
        program.Uniform("VisibilityBitmaskThickness", MathF.Max(settings.GroundTruth.VisibilityBitmaskThickness, 0.0001f));
        program.Uniform("SliceCount", Math.Max(settings.GroundTruth.SliceCount, 1));
        program.Uniform("StepsPerSlice", Math.Max(settings.GroundTruth.StepsPerSlice, 1));
        program.Uniform("UseInputNormals", settings.GroundTruth.UseInputNormals ? 1 : 0);
        program.Uniform("UseVisibilityBitmask", settings.GroundTruth.UseVisibilityBitmask ? 1 : 0);
        program.Uniform("DepthMode", view.View.ReversedDepth ? 1 : 0);
        program.Uniform("OutputOrigin", Vector2.Zero);
        program.Sampler("DepthView", depth, 0);
        program.Sampler("Normal", RequireWebEffectTexture(WebNormalTextureName), 1);
    }

    private void SetWebGtaoHorizontalUniforms(XRRenderProgram program)
        => SetWebGtaoBlurUniforms(program, horizontal: true);

    private void SetWebGtaoVerticalUniforms(XRRenderProgram program)
        => SetWebGtaoBlurUniforms(program, horizontal: false);

    private void SetWebGtaoBlurUniforms(XRRenderProgram program, bool horizontal)
    {
        AmbientOcclusionSettings settings = RequireWebGtaoSettings();
        XRCamera camera = ResolveCurrentSettingsCamera()
            ?? throw new InvalidOperationException("WebGPU.DefaultPipeline.GtaoCameraMissing: GTAO requires a scene camera.");
        XRTexture2D output = RequireWebEffectTexture(horizontal ? WebGtaoHorizontalTextureName : WebGtaoFinalTextureName);
        XRTexture2D input = RequireWebEffectTexture(horizontal ? WebGtaoRawTextureName : WebGtaoHorizontalTextureName);
        XRTexture2D depth = RequireWebEffectTexture(DepthStencilTextureName);
        RenderFrameViewSelection view = WebPipelineRasterProgram.CaptureView(camera, new Vector2(output.Width, output.Height));
        program.Uniform("OutputSize", new Vector2(output.Width, output.Height));
        program.Uniform("DepthSize", new Vector2(depth.Width, depth.Height));
        program.Uniform("TexelSize", new Vector2(1.0f / output.Width, 1.0f / output.Height));
        program.Uniform("BlurDirection", horizontal ? Vector2.UnitX : Vector2.UnitY);
        program.Uniform("DenoiseRadius", Math.Clamp(settings.GroundTruth.DenoiseRadius, 0, 16));
        program.Uniform("DenoiseSharpness", MathF.Max(settings.GroundTruth.DenoiseSharpness, 0.001f));
        program.Uniform("DenoiseEnabled", settings.GroundTruth.DenoiseEnabled ? 1 : 0);
        program.Uniform("UseInputNormals", settings.GroundTruth.UseInputNormals ? 1 : 0);
        program.Uniform("UseNormalWeightedBlur", settings.GroundTruth.UseNormalWeightedBlur ? 1 : 0);
        program.Uniform("DepthMode", view.View.ReversedDepth ? 1 : 0);
        program.Uniform("OutputOrigin", Vector2.Zero);
        program.Sampler("GTAOInputTexture", input, 0);
        program.Sampler("DepthView", depth, 1);
        program.Sampler("Normal", RequireWebEffectTexture(WebNormalTextureName), 2);
    }

    private void SetWebBloomCopyUniforms(XRRenderProgram program)
    {
        XRTexture2D output = RequireWebEffectTexture(WebBloomMipTextureNames[0]);
        program.Uniform("BloomOutputArea", new Vector4(0, 0, output.Width, output.Height));
        program.Sampler("SourceTexture", RequireWebEffectTexture(HDRSceneTextureName), 0);
    }

    private void SetWebBloomDownUniforms(XRRenderProgram program, int level)
    {
        BloomSettings settings = RequireWebBloomSettings();
        XRTexture2D source = RequireWebEffectTexture(WebBloomMipTextureNames[level - 1]);
        XRTexture2D output = RequireWebEffectTexture(WebBloomMipTextureNames[level]);
        program.Uniform("BloomOutputArea", new Vector4(0, 0, output.Width, output.Height));
        program.Uniform("BloomSourceTexelThreshold", new Vector4(
            1.0f / source.Width, 1.0f / source.Height, MathF.Max(settings.Threshold, 0.0f),
            Math.Clamp(settings.SoftKnee, 0.0f, 1.0f)));
        program.Uniform("BloomDownsampleControls", new Vector4(
            MathF.Max(settings.Intensity, 0.0f), level == 1 ? 1.0f : 0.0f,
            level == 1 ? 1.0f : 0.0f, 0.0f));
        program.Uniform("BloomLuminance", new Vector4(RuntimeEngine.Rendering.Settings.DefaultLuminance, 0.0f));
        program.Sampler("SourceTexture", source, 0);
    }

    private void SetWebBloomUpUniforms(XRRenderProgram program, int level)
    {
        BloomSettings settings = RequireWebBloomSettings();
        XRTexture2D source = RequireWebEffectTexture(WebBloomMipTextureNames[level + 1]);
        XRTexture2D output = RequireWebEffectTexture(WebBloomMipTextureNames[level]);
        program.Uniform("BloomOutputArea", new Vector4(0, 0, output.Width, output.Height));
        program.Uniform("BloomUpsampleSourceTexel", new Vector4(
            1.0f / source.Width, 1.0f / source.Height, MathF.Max(settings.Radius, 0.1f),
            Math.Clamp(settings.Scatter, 0.0f, 1.0f)));
        program.Sampler("SourceTexture", source, 0);
    }

    private void SetWebBloomCombineUniforms(XRRenderProgram program)
    {
        BloomSettings settings = RequireWebBloomSettings();
        XRTexture2D output = RequireWebEffectTexture(WebBloomCombinedTextureName);
        program.Uniform("BloomOutputArea", new Vector4(0, 0, output.Width, output.Height));
        XRTexture2D last = RequireWebEffectTexture(WebBloomMipTextureNames[0]);
        for (int level = 0; level < WebBloomMipTextureNames.Length; level++)
        {
            XRTexture2D? mip = RequireWebEffectInstance().GetTexture<XRTexture2D>(WebBloomMipTextureNames[level]);
            if (mip is not null)
                last = mip;
            program.Sampler(WebBloomMipSamplerNames[level], last, level + 1);
        }
        int startMip = Math.Clamp(settings.StartMip, 0, 4);
        int endMip = Math.Clamp(settings.EndMip, startMip, 4);
        program.Uniform("BloomCombineControls", new Vector4(
            MathF.Max(settings.Strength, 0.0f),
            startMip, endMip, settings.DebugBloomOnly ? 1.0f : 0.0f));
        program.Uniform("BloomWeights0To3", new Vector4(
            settings.Lod0Weight, settings.Lod1Weight, settings.Lod2Weight, settings.Lod3Weight));
        program.Uniform("BloomWeight4", new Vector4(settings.Lod4Weight, 0, 0, 0));
        program.Sampler("HdrTexture", RequireWebEffectTexture(HDRSceneTextureName), 0);
    }

    private void SetWebBloomDebugOutputUniforms(XRRenderProgram program)
    {
        var area = RuntimeEngine.Rendering.State.RenderArea;
        program.Uniform("BloomOutputArea", new Vector4(area.X, area.Y, area.Width, area.Height));
        program.Sampler("SourceTexture", RequireWebEffectTexture(WebBloomCombinedTextureName), 0);
    }

    private bool ShouldUseWebGtao()
        => RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.EnableGtao &&
            ResolveAmbientOcclusionSettings() is { Enabled: true };

    private bool ShouldUseWebBloom()
        => RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.EnableBloom &&
            GetSettings<BloomSettings>(ResolveCurrentSettingsCamera()?.GetPostProcessState(this)) is { Enabled: true };

    private bool ShouldUseWebDebugBloomOnly()
        => RuntimeEngine.Rendering.Settings.BrowserWebGpuQuality.EnableBloom &&
            GetSettings<BloomSettings>(ResolveCurrentSettingsCamera()?.GetPostProcessState(this)) is
            { Enabled: true, DebugBloomOnly: true };

    private void ValidateWebGlobalIllumination()
    {
        if (GlobalIlluminationMode == EGlobalIlluminationMode.None)
            return;
        if (GlobalIlluminationMode != EGlobalIlluminationMode.LightProbesAndIbl)
            throw new NotSupportedException($"WebGPU.DefaultPipeline.GlobalIlluminationUnsupported: '{GlobalIlluminationMode}' has no cooked WebGPU path.");
        IRuntimeRenderWorld world = RuntimeEngine.Rendering.State.RenderingWorld
            ?? throw new InvalidOperationException("WebGPU.DefaultPipeline.RenderWorldMissing: forward ambient lighting requires a live world.");
        foreach (var probe in world.Lights.LightProbes)
            if (probe is not XREngine.Components.Capture.Lights.PublishedRetainedLightProbeComponent { PublishedRetainedIbl: not null } ||
                probe.AutoCaptureOnActivate || probe.RealtimeCapture || !probe.TryGetActiveIblOutput(out var generation) ||
                generation.Provenance != XREngine.Components.Capture.Lights.ELightProbeIblProvenance.RetainedCookedData)
                throw new NotSupportedException("WebGPU.DefaultPipeline.LightProbeProducerUnsupported: the selected world requires target-cooked retained probe data with both authored capture switches disabled.");
        // Receiver admission occurs for every selected lit draw, including mixed
        // material worlds, when its physical lighting bindings are published.
    }

    private static VPRC_RenderQuadToFBO AddWebEffectQuad(ViewportRenderCommandContainer commands, string quadName,
        string destinationFboName, params string[] sampledTextures)
    {
        VPRC_RenderQuadToFBO command = commands.Add<VPRC_RenderQuadToFBO>();
        command.RequiredForOutput = true;
        command.RequiredDeclaredResourceName = destinationFboName;
        command.SetTargets(quadName, destinationFboName, matchDestinationRenderArea: true)
            .ConfigureRenderGraphResources(resources =>
            {
                foreach (string texture in sampledTextures)
                    resources.SampleTexture(texture);
            });
        return command;
    }

    private void AppendWebGtaoCommands(ViewportRenderCommandContainer commands)
    {
        using (commands.AddUsing<VPRC_BindFBOByName>(command =>
            command.SetOptions(WebNormalFboName, clearColor: true, clearDepth: true, clearStencil: false)))
        {
            commands.Add<VPRC_DepthTest>().Enable = true;
            VPRC_ForwardDepthNormalPrePass prepass = commands.Add<VPRC_ForwardDepthNormalPrePass>();
            prepass.SetOptions(
                [(int)EDefaultRenderPass.OpaqueDeferred, (int)EDefaultRenderPass.OpaqueForward, (int)EDefaultRenderPass.MaskedForward],
                MeshSubmissionStrategy);
        }
        AppendWebDepthNormalResolveCommands(commands);
        commands.Add<VPRC_DepthTest>().Enable = false;
        commands.Add<VPRC_DepthWrite>().Allow = false;
        AddWebEffectQuad(commands, WebGtaoGenerateQuadName, WebGtaoRawFboName,
            DepthStencilTextureName, WebNormalTextureName);
        AddWebEffectQuad(commands, WebGtaoHorizontalQuadName, WebGtaoHorizontalFboName,
            WebGtaoRawTextureName, DepthStencilTextureName, WebNormalTextureName);
        AddWebEffectQuad(commands, WebGtaoVerticalQuadName, WebGtaoFinalFboName,
            WebGtaoHorizontalTextureName, DepthStencilTextureName, WebNormalTextureName);
        commands.Add<VPRC_DepthWrite>().Allow = true;
    }

    private bool HasWebBloomLevel(int level)
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.GetTexture<XRTexture2D>(WebBloomMipTextureNames[level]) is not null;

    private void AppendWebBloomCommands(ViewportRenderCommandContainer commands)
    {
        commands.Add<VPRC_DepthTest>().Enable = false;
        commands.Add<VPRC_DepthWrite>().Allow = false;
        AddWebEffectQuad(commands, WebBloomCopyQuadName, WebBloomMipFboNames[0], HDRSceneTextureName);
        for (int level = 1; level <= 4; level++)
        {
            int selectedLevel = level;
            VPRC_IfElse levelChoice = commands.Add<VPRC_IfElse>();
            levelChoice.Label = $"WebBloomDown{selectedLevel}";
            levelChoice.ConditionEvaluator = () => HasWebBloomLevel(selectedLevel);
            ViewportRenderCommandContainer levelCommands = new(this);
            AddWebEffectQuad(levelCommands, WebBloomDownQuadNames[level], WebBloomMipFboNames[level],
                WebBloomMipTextureNames[level - 1]);
            levelChoice.TrueCommands = levelCommands;
        }
        for (int level = 3; level >= 1; level--)
        {
            int selectedLevel = level;
            VPRC_IfElse levelChoice = commands.Add<VPRC_IfElse>();
            levelChoice.Label = $"WebBloomUp{selectedLevel}";
            levelChoice.ConditionEvaluator = () => HasWebBloomLevel(selectedLevel + 1);
            ViewportRenderCommandContainer levelCommands = new(this);
            VPRC_RenderQuadToFBO up = AddWebEffectQuad(levelCommands, WebBloomUpQuadNames[level], WebBloomMipFboNames[level],
                WebBloomMipTextureNames[level + 1]);
            up.RequiredDeclaredResourceName = WebBloomMipTextureNames[level + 1];
            levelChoice.TrueCommands = levelCommands;
        }
        VPRC_RenderQuadToFBO combine = commands.Add<VPRC_RenderQuadToFBO>();
        combine.RequiredForOutput = true;
        combine.SetTargets(WebBloomCombineQuadName, WebBloomCombinedFboName, matchDestinationRenderArea: true)
            .ConfigureRenderGraphResources(resources =>
            {
                resources.SampleTexture(HDRSceneTextureName);
                for (int level = 0; level < WebBloomMipTextureNames.Length; level++)
                    resources.SampleTextureWhenDeclared(WebBloomMipTextureNames[level]);
            });
        combine.RequiredDeclaredResourceName = WebBloomCombinedFboName;
        commands.Add<VPRC_DepthWrite>().Allow = true;
    }

    /// <summary>Returns the generation-owned full-resolution AO surface and its ambient-only controls.</summary>
    public bool TryGetWebAmbientOcclusion(out XRTexture2D? finalAo, out float power, out bool multiBounce)
    {
        finalAo = null;
        power = 1.0f;
        multiBounce = false;
        AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings();
        if (settings is not { Enabled: true })
            return false;
        if (AmbientOcclusionSettings.NormalizeType(settings.Type) != AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion)
            throw new NotSupportedException($"WebGPU.DefaultPipeline.AmbientOcclusionModeUnsupported: {settings.Type} requires a cooked WebGPU effect route.");
        XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (instance is null || !ReferenceEquals(instance.Pipeline, this))
            throw new InvalidOperationException("WebGPU.DefaultPipeline.AmbientOcclusionPipelineMissing: no matching active pipeline instance.");
        finalAo = instance.GetTexture<XRTexture2D>(WebGtaoFinalTextureName)
            ?? throw new InvalidOperationException("WebGPU.DefaultPipeline.AmbientOcclusionResourceMissing: the committed generation has no final AO texture.");
        power = MathF.Max(settings.Power, 0.001f);
        multiBounce = settings.GroundTruth.MultiBounceEnabled;
        return true;
    }

    bool IRenderPipelineAmbientOcclusionProvider.TryGetAmbientOcclusion(out XRTexture2D? visibility, out float power, out bool multiBounce)
        => TryGetWebAmbientOcclusion(out visibility, out power, out multiBounce);
}
