using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    private const string WebTonemapFBOName = "WebTonemapMaterial";
    private ShaderProgramArtifact? _webTonemapArtifact;

    /// <summary>
    /// Exact, verified package artifact used by the WebGPU output's Mobius tonemap pass.
    /// Immutable after its first package binding so generations cannot mix shader identities.
    /// </summary>
    [YamlIgnore]
    public ShaderProgramArtifact? WebTonemapArtifact
    {
        get => _webTonemapArtifact;
        init => BindWebTonemapArtifact(value);
    }

    /// <summary>
    /// Binds the package's exact output module to a newly hydrated pipeline without replacing
    /// its authored settings. A different identity requires a different pipeline asset.
    /// </summary>
    public void BindWebTonemapArtifact(ShaderProgramArtifact? artifact)
    {
        if (artifact is null)
            throw new ArgumentNullException(nameof(artifact));
        if (_webTonemapArtifact is { } installed)
        {
            if (installed.Identity != artifact.Identity)
                throw new InvalidOperationException("WebGPU.DefaultPipeline.TonemapIdentityChanged: replace the pipeline asset to install a different output module.");
            return;
        }
        if (artifact.DescriptorBytes.IsDefaultOrEmpty)
            throw new InvalidDataException("WebGPU.DefaultPipeline.TonemapDescriptorMissing: supply a verified cooked package artifact.");
        ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
        if (verified.Identity != artifact.Identity || verified.Target != ShaderCompileTarget.WebGPUWgsl ||
            verified.Pass != "tonemap" || verified.VertexEntryPoint is null || verified.FragmentEntryPoint is null)
            throw new InvalidDataException("WebGPU.DefaultPipeline.TonemapArtifactInvalid: the artifact must be a complete WebGPU tonemap program.");
        SetField(ref _webTonemapArtifact, verified, nameof(WebTonemapArtifact));
    }

    private static bool UsesWebOutputTier
        => OperatingSystem.IsBrowser() || AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU;

    private void RequireSupportedOutputResources()
    {
        if (!UsesWebOutputTier)
            return;
        if (AbstractRenderer.Current?.BackendId != RendererBackendId.WebGPU)
            throw new NotSupportedException("WebGPU.DefaultPipeline.OutputUnavailable: bind the output's WebGPU renderer before building resources.");
        if (WebTonemapArtifact is null)
            throw new NotSupportedException("WebGPU.DefaultPipeline.TonemapArtifactMissing: the package must supply its exact cooked tonemap artifact.");
    }

    private void DeclareWebResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourceProfile profile = builder.Profile;
        if (profile.Stereo || profile.ViewCount != 1 || profile.OutputHDR ||
            profile.AntiAliasingMode != EAntiAliasingMode.None || profile.OutputColorFormat != EPixelInternalFormat.Rgba8)
            throw new NotSupportedException("WebGPU.DefaultPipeline.ProfileUnsupported: the forward-lit output requires mono SDR presentation, AA None, and one sample.");

        builder.Texture(HDRSceneTextureName)
            .Size(RenderResourceSizePolicy.Internal())
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Factory(CreateWebHdrTexture)
            .Add();
        builder.Texture(DepthStencilTextureName)
            .Size(RenderResourceSizePolicy.Internal())
            .Usage(RenderPipelineResourceUsage.DepthStencilAttachment)
            .Format(EPixelInternalFormat.DepthComponent32, EPixelFormat.DepthComponent, EPixelType.Float)
            .SizedFormat(ESizedInternalFormat.DepthComponent32f)
            .Factory(CreateWebDepthTexture)
            .Add();
        builder.FrameBuffer(ForwardPassFBOName)
            .Size(RenderResourceSizePolicy.Internal())
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, HDRSceneTextureName)
            .Depth(DepthStencilTextureName)
            .Factory(CreateWebForwardFbo)
            .Add();
        builder.QuadMaterial(WebTonemapFBOName)
            .DependsOn(HDRSceneTextureName)
            .Factory(CreateWebTonemapMaterial)
            .Add();
        builder.External("$ExternalOutput")
            .Contract(ExternalRenderResourceKind.FrameBuffer,
                profile.ExternalTargetKind == RenderPipelineExternalTargetKind.Window
                    ? ExternalRenderResourceOwnership.Window : ExternalRenderResourceOwnership.Caller,
                ExternalRenderResourceSynchronization.FrameBoundary)
            .Add();
    }

    private XRTexture CreateWebHdrTexture()
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, EFrameBufferAttachment.ColorAttachment0);
        texture.Name = HDRSceneTextureName;
        texture.SamplerName = "SourceTexture";
        texture.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
        texture.Resizable = false;
        texture.AutoGenerateMipmaps = false;
        texture.MaxAnisotropy = 1.0f;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = texture.VWrap = ETexWrapMode.ClampToEdge;
        return texture;
    }

    private XRTexture CreateWebDepthTexture()
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
            EPixelInternalFormat.DepthComponent32, EPixelFormat.DepthComponent, EPixelType.Float, EFrameBufferAttachment.DepthAttachment);
        texture.Name = DepthStencilTextureName;
        texture.SizedInternalFormat = ESizedInternalFormat.DepthComponent32f;
        texture.Resizable = false;
        texture.AutoGenerateMipmaps = false;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        return texture;
    }

    private XRFrameBuffer CreateWebForwardFbo()
    {
        XRFrameBuffer framebuffer = new(
            (GetTexture<XRTexture2D>(HDRSceneTextureName)!, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (GetTexture<XRTexture2D>(DepthStencilTextureName)!, EFrameBufferAttachment.DepthAttachment, 0, -1))
        { Name = ForwardPassFBOName };
        try
        {
            // A newly created logical FBO defaults to complete. Materialize its physical
            // attachment plan before the pending generation validates and commits it.
            AbstractRenderer.Current!.GetOrCreateAPIRenderObject(framebuffer, generateNow: true);
            return framebuffer;
        }
        catch
        {
            framebuffer.Destroy(true);
            throw;
        }
    }

    private XRFrameBuffer CreateWebTonemapMaterial()
    {
        ShaderProgramArtifact artifact = WebTonemapArtifact
            ?? throw new InvalidOperationException("WebGPU.DefaultPipeline.TonemapArtifactMissing: no cooked output program is installed.");
        XRShader vertex = new(EShaderType.Vertex) { CookedArtifact = artifact };
        XRShader fragment = new(EShaderType.Fragment) { CookedArtifact = artifact };
        XRMaterial material = new([GetTexture<XRTexture2D>(HDRSceneTextureName)!], vertex, fragment)
        {
            Name = WebTonemapFBOName,
            RenderOptions = new RenderingParameters
            {
                DepthTest = { Enabled = ERenderParamUsage.Disabled, UpdateDepth = false, Function = EComparison.Always },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
            },
        };
        try
        {
            XRQuadFrameBuffer quad = new(material, deriveRenderTargetsFromMaterial: false, prepareForInitialRendering: false)
            { Name = WebTonemapFBOName };
            quad.SettingUniforms += SetWebTonemapUniforms;
            quad.Destroyed += DestroyWebTonemapResources;
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

    private void DestroyWebTonemapResources(XRObjectBase resource)
    {
        XRQuadFrameBuffer quad = (XRQuadFrameBuffer)resource;
        quad.SettingUniforms -= SetWebTonemapUniforms;
        quad.Destroyed -= DestroyWebTonemapResources;
        XRMaterial? material = quad.Material;
        if (material is null)
            return;
        material.Destroy(true);
        foreach (XRShader shader in material.Shaders)
            shader.Destroy(true);
    }

    private ViewportRenderCommandContainer CreateWebOutputCommands()
    {
        ViewportRenderCommandContainer commands = new(this);
        commands.Add<VPRC_Manual>().ManualAction = ValidateWebFrameRequirements;
        commands.Add<VPRC_ColorMask>().Set(true, true, true, true);
        commands.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
        commands.Add<VPRC_DepthWrite>().Allow = true;
        commands.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, EMeshSubmissionStrategy.CpuDirect);
        using (commands.AddUsing<VPRC_PushViewportRenderArea>(command => command.UseInternalResolution = true))
        using (commands.AddUsing<VPRC_BindFBOByName>(command => command.SetOptions(ForwardPassFBOName, clearStencil: false)))
        {
            commands.Add<VPRC_DepthTest>().Enable = true;
            commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, EMeshSubmissionStrategy.CpuDirect);
            commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, EMeshSubmissionStrategy.CpuDirect);
        }
        using (commands.AddUsing<VPRC_PushOutputFBORenderArea>())
        using (commands.AddUsing<VPRC_BindOutputFBO>(command => command.SetOptions(clearColor: false, clearDepth: false, clearStencil: false)))
        {
            commands.Add<VPRC_DepthTest>().Enable = false;
            commands.Add<VPRC_DepthWrite>().Allow = false;
            VPRC_RenderQuadToFBO tonemap = commands.Add<VPRC_RenderQuadToFBO>();
            tonemap.RequiredForOutput = true;
            tonemap.SetTargets(WebTonemapFBOName)
                .ConfigureRenderGraphResources(static resources => resources.SampleTexture(HDRSceneTextureName));
        }
        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, EMeshSubmissionStrategy.CpuDirect);
        return commands;
    }

    private static void DescribeWebRenderPasses(RenderPassMetadataCollection metadata)
    {
        metadata.ForPass((int)EDefaultRenderPass.PreRender, nameof(EDefaultRenderPass.PreRender), ERenderGraphPassStage.Graphics);
        metadata.ForPass((int)EDefaultRenderPass.OpaqueDeferred, nameof(EDefaultRenderPass.OpaqueDeferred), ERenderGraphPassStage.Graphics)
            .DependsOn((int)EDefaultRenderPass.PreRender);
        metadata.ForPass((int)EDefaultRenderPass.OpaqueForward, nameof(EDefaultRenderPass.OpaqueForward), ERenderGraphPassStage.Graphics)
            .DependsOn((int)EDefaultRenderPass.OpaqueDeferred);
        string tonemap = VPRC_RenderQuadToFBO.BuildQuadBlitPassName(WebTonemapFBOName, RenderGraphResourceNames.OutputRenderTarget);
        if (metadata.TryGetPassIndex(tonemap, out int index))
        {
            metadata.ForPass(index, tonemap, ERenderGraphPassStage.Graphics).DependsOn((int)EDefaultRenderPass.OpaqueForward);
            metadata.ForPass((int)EDefaultRenderPass.PostRender, nameof(EDefaultRenderPass.PostRender), ERenderGraphPassStage.Graphics).DependsOn(index);
        }
    }

    private void ValidateWebFrameRequirements()
    {
        RequireSupportedOutputResources();
        if (MeshSubmissionStrategy != EMeshSubmissionStrategy.CpuDirect)
            throw new NotSupportedException("WebGPU.DefaultPipeline.SubmissionUnsupported: explicitly select CpuDirect for this output.");
        if (GlobalIlluminationMode != EGlobalIlluminationMode.None)
            throw new NotSupportedException("WebGPU.DefaultPipeline.GlobalIlluminationUnsupported: explicitly select global ambient lighting (GI None); probe, IBL, and advanced GI variants are not available.");
        XRRenderPipelineInstance instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline!;
        if (instance.RenderState.ScreenSpaceUserInterface is { IsActive: true })
            throw new NotSupportedException("WebGPU.DefaultPipeline.UiUnsupported: engine screen-space UI needs cooked WebGPU material variants.");
        ReadOnlySpan<int> unsupportedPasses =
        [
            (int)EDefaultRenderPass.Background, (int)EDefaultRenderPass.DeferredDecals,
            (int)EDefaultRenderPass.MaskedForward, (int)EDefaultRenderPass.TransparentForward,
            (int)EDefaultRenderPass.WeightedBlendedOitForward, (int)EDefaultRenderPass.PerPixelLinkedListForward,
            (int)EDefaultRenderPass.DepthPeelingForward, (int)EDefaultRenderPass.OnTopForward,
            (int)EDefaultRenderPass.PostBloomForward, (int)EDefaultRenderPass.PostMotionBlurForward,
            (int)EDefaultRenderPass.PostDepthOfFieldForward,
        ];
        foreach (int pass in unsupportedPasses)
            if (instance.ActiveMeshRenderCommands.HasRenderingCommands(pass))
                throw new NotSupportedException($"WebGPU.DefaultPipeline.PassUnsupported: pass '{(EDefaultRenderPass)pass}' has no cooked output route.");

        PipelinePostProcessState? state = (instance.RenderState.SceneCamera ?? instance.LastSceneCamera)?.GetPostProcessState(this);
        if (GetSettings<BloomSettings>(state) is { Enabled: true } ||
            GetSettings<AmbientOcclusionSettings>(state) is { Enabled: true } ||
            GetSettings<MotionBlurSettings>(state) is { Enabled: true } ||
            GetSettings<DepthOfFieldSettings>(state) is { Enabled: true } ||
            GetSettings<VolumetricFogSettings>(state) is { Enabled: true } ||
            GetSettings<VignetteSettings>(state) is { Enabled: true } ||
            GetSettings<ChromaticAberrationSettings>(state) is { Enabled: true } ||
            GetSettings<FogSettings>(state) is { DepthFogIntensity: > 0 } ||
            GetSettings<LensDistortionSettings>(state) is { Intensity: not 0 } ||
            GetSettings<GpuBvhDebugSettings>(state) is { Enabled: true } or { MeshletDebugDisplayEnabled: true } or { FullOverdrawEnabled: true } ||
            ShouldRunAtmosphericScattering() || HasFullPipelineDebugVisualization())
            throw new NotSupportedException("WebGPU.DefaultPipeline.EffectUnsupported: this output supports direct forward lighting and Mobius tonemapping; disable unsupported camera effects explicitly.");
        ColorGradingSettings? color = GetSettings<ColorGradingSettings>(state);
        if (color is not null && (color.AutoExposure || color.ExposureMode != ColorGradingSettings.ExposureControlMode.Artist ||
            !float.IsFinite(color.Exposure) || !float.IsFinite(color.Gamma) || color.Exposure < 0 || color.Gamma <= 0 ||
            color.Contrast != 1 || color.Saturation != 1 || color.Brightness != 1 || color.Hue != 1 ||
            (Vector3)color.Tint != Vector3.One))
            throw new NotSupportedException("WebGPU.DefaultPipeline.ColorGradingUnsupported: use manual artist exposure and gamma with neutral color grading.");
        if (GetSettings<TonemappingSettings>(state) is { Tonemapping: not ETonemappingType.Mobius })
            throw new NotSupportedException("WebGPU.DefaultPipeline.TonemapUnsupported: the supplied output route supports Mobius tonemapping.");
    }

    private void SetWebTonemapUniforms(XRRenderProgram program)
    {
        PipelinePostProcessState? state = ResolveCurrentSettingsCamera()?.GetPostProcessState(this);
        ColorGradingSettings? color = GetSettings<ColorGradingSettings>(state);
        TonemappingSettings? tonemap = GetSettings<TonemappingSettings>(state);
        program.Uniform("TonemapParameters", new Vector4(color?.Exposure ?? 1.0f, color?.Gamma ?? 2.2f,
            tonemap?.MobiusTransition ?? TonemappingSettings.DefaultMobiusTransition, 0.0f));
    }
}
