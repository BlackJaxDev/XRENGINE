using System.Numerics;
using System.Diagnostics.CodeAnalysis;
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
    protected override void OnBindingWebPipelineArtifacts(WebPipelineArtifactCatalog artifacts)
    {
        if (artifacts.TryResolve("tonemap", out ShaderProgramArtifact? tonemap))
            BindWebTonemapArtifact(tonemap);
    }

    /// <summary>Returns the exact program for a pipeline-owned pass when its optional feature is available.</summary>
    public override bool TryGetWebPipelineArtifact(string pass, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        if (pass == "tonemap" && _webTonemapArtifact is { } tonemap)
        {
            artifact = tonemap;
            return true;
        }
        return base.TryGetWebPipelineArtifact(pass, out artifact);
    }

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
        if (WebPipelineArtifacts is { } catalog && catalog.TryResolve("tonemap", out ShaderProgramArtifact? declared) &&
            declared.Identity != artifact.Identity)
            throw new InvalidOperationException("WebGPU.DefaultPipeline.TonemapIdentityChanged: the catalog owns a different output module.");
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
        if (WebTonemapArtifact is null && !TryGetWebPipelineArtifact("tonemap-auto-exposure", out _))
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
            .Usage(RenderPipelineResourceUsage.DepthStencilAttachment | RenderPipelineResourceUsage.SampledTexture)
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
        DeclareWebEffectResources(builder);
        var tonemap = builder.QuadMaterial(WebTonemapFBOName).DependsOn(HDRSceneTextureName);
        if (WebBloomEnabled(profile))
            tonemap.DependsOn(WebBloomCombinedTextureName);
        bool autoExposure = WebAutoExposureEnabled(profile);
        if (autoExposure)
        {
            builder.Texture(AutoExposureTextureName).Size(RenderResourceSizePolicy.Absolute(1, 1))
                .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage)
                .Format(EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float).SizedFormat(ESizedInternalFormat.R32f)
                .RequiresStorageUsage(true).History(RenderResourceHistoryPolicy.PreserveWhenCompatible)
                .Factory(CreateWebAutoExposureTexture).Add();
            tonemap.DependsOn(AutoExposureTextureName);
        }
        tonemap.Factory(() => CreateWebTonemapMaterial(autoExposure)).Add();
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

    private XRTexture CreateWebAutoExposureTexture()
        => new XRTexture2D(1, 1, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, 1)
        {
            Name = AutoExposureTextureName, SamplerName = AutoExposureTextureName,
            SizedInternalFormat = ESizedInternalFormat.R32f, Resizable = false,
            RequiresStorageUsage = true, AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.Nearest, MagFilter = ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge, VWrap = ETexWrapMode.ClampToEdge,
        };

    private XRFrameBuffer CreateWebTonemapMaterial(bool autoExposure)
    {
        ShaderProgramArtifact artifact = autoExposure ? GetRequiredWebPipelineArtifact("tonemap-auto-exposure") : WebTonemapArtifact
            ?? throw new InvalidOperationException("WebGPU.DefaultPipeline.TonemapArtifactMissing: no cooked output program is installed.");
        if (!WebPipelineArtifactCatalog.IsCompleteRasterProgram(artifact))
            throw new NotSupportedException("WebGPU.DefaultPipeline.TonemapProgramShape: the selected tonemap requires a complete raster program.");
        XRShader vertex = new(EShaderType.Vertex) { CookedArtifact = artifact };
        XRShader fragment = new(EShaderType.Fragment) { CookedArtifact = artifact };
        XRMaterial material = new(Array.Empty<XRTexture?>(), vertex, fragment)
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
        {
            VPRC_IfElse aoChoice = commands.Add<VPRC_IfElse>();
            aoChoice.Label = "WebGtao";
            aoChoice.ConditionEvaluator = ShouldUseWebGtao;
            ViewportRenderCommandContainer aoCommands = new(this);
            AppendWebGtaoCommands(aoCommands);
            aoChoice.TrueCommands = aoCommands;

            using (commands.AddUsing<VPRC_BindFBOByName>(command =>
            {
                command.SetOptions(ForwardPassFBOName, clearColor: true, clearDepth: false, clearStencil: false);
                command.DynamicClearDepth = () => !ShouldUseWebGtao();
                command.DescribeClearDepth = context => context.ResourceLayout is null
                    ? !ShouldUseWebGtao()
                    : !context.HasResource(WebNormalFboName);
            }))
            {
            // Opaque materials may leave blend state unchanged. The preceding frame's
            // display overlays must not turn those surfaces into alpha-blended draws.
            commands.Add<VPRC_Manual>().ManualAction = static () => RuntimeEngine.Rendering.State.EnableBlend(false);
            commands.Add<VPRC_DepthTest>().Enable = true;
            VPRC_RenderMeshesPass deferred = commands.Add<VPRC_RenderMeshesPass>();
            deferred.SetOptions((int)EDefaultRenderPass.OpaqueDeferred, EMeshSubmissionStrategy.CpuDirect);
            deferred.SetSampledTexturesWhenDeclared(WebGtaoFinalTextureName);
            VPRC_RenderMeshesPass forward = commands.Add<VPRC_RenderMeshesPass>();
            forward.SetOptions((int)EDefaultRenderPass.OpaqueForward, EMeshSubmissionStrategy.CpuDirect);
            forward.SetSampledTexturesWhenDeclared(WebGtaoFinalTextureName);
            VPRC_RenderMeshesPass masked = commands.Add<VPRC_RenderMeshesPass>();
            masked.SetOptions((int)EDefaultRenderPass.MaskedForward, EMeshSubmissionStrategy.CpuDirect);
            masked.SetSampledTexturesWhenDeclared(WebGtaoFinalTextureName);
            // Far-depth backgrounds fill only uncovered pixels. They must precede
            // sorted blending so transparent surfaces composite over authored HDR sky.
            commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, EMeshSubmissionStrategy.CpuDirect);
            // The canonical pass collection owns stable far-to-near ordering and
            // authored sort priority. Blending occurs in HDR before bloom/tonemap.
            VPRC_RenderMeshesPass transparent = commands.Add<VPRC_RenderMeshesPass>();
            transparent.SetOptions((int)EDefaultRenderPass.TransparentForward, EMeshSubmissionStrategy.CpuDirect);
            transparent.SetSampledTexturesWhenDeclared(WebGtaoFinalTextureName);
            // A depth-tested debug primitive has no display-overlay equivalent. If one is
            // submitted, its explicit WebGPU material rejection preserves that distinction.
            commands.Add<VPRC_RenderDebugShapes>().DepthTested = true;
            }

            VPRC_IfElse bloomChoice = commands.Add<VPRC_IfElse>();
            bloomChoice.Label = "WebBloom";
            bloomChoice.ConditionEvaluator = ShouldUseWebBloom;
            ViewportRenderCommandContainer bloomCommands = new(this);
            AppendWebBloomCommands(bloomCommands);
            bloomChoice.TrueCommands = bloomCommands;

            VPRC_IfElse exposureChoice = commands.Add<VPRC_IfElse>();
            exposureChoice.Label = "WebAutoExposure";
            exposureChoice.ConditionEvaluator = ShouldUseWebAutoExposure;
            ViewportRenderCommandContainer exposureCommands = new(this);
            VPRC_ExposureUpdate exposure = exposureCommands.Add<VPRC_ExposureUpdate>();
            exposure.SetOptions(HDRSceneTextureName, false);
            exposure.GpuProgramBinding = "auto-exposure";
            exposureChoice.TrueCommands = exposureCommands;
        }
        using (commands.AddUsing<VPRC_PushOutputFBORenderArea>())
        using (commands.AddUsing<VPRC_BindOutputFBO>(command => command.SetOptions(clearColor: false, clearDepth: false, clearStencil: false)))
        {
            commands.Add<VPRC_DepthTest>().Enable = false;
            commands.Add<VPRC_DepthWrite>().Allow = false;
            VPRC_IfElse debugBloomChoice = commands.Add<VPRC_IfElse>();
            debugBloomChoice.Label = "WebDebugBloomOnly";
            debugBloomChoice.ConditionEvaluator = ShouldUseWebDebugBloomOnly;
            ViewportRenderCommandContainer debugBloomCommands = new(this);
            VPRC_RenderQuadToFBO debugBloom = debugBloomCommands.Add<VPRC_RenderQuadToFBO>();
            debugBloom.RequiredForOutput = true;
            debugBloom.RequiredDeclaredResourceName = WebBloomCombinedTextureName;
            debugBloom.SetTargets(WebBloomDebugOutputQuadName)
                .ConfigureRenderGraphResources(static resources => resources.SampleTexture(WebBloomCombinedTextureName));
            debugBloomChoice.TrueCommands = debugBloomCommands;
            ViewportRenderCommandContainer tonemapCommands = new(this);
            VPRC_RenderQuadToFBO tonemap = tonemapCommands.Add<VPRC_RenderQuadToFBO>();
            tonemap.RequiredForOutput = true;
            tonemap.SetTargets(WebTonemapFBOName)
                .ConfigureRenderGraphResources(static resources =>
                {
                    resources.SampleTexture(HDRSceneTextureName);
                    resources.SampleTextureWhenDeclared(WebBloomCombinedTextureName);
                    resources.SampleTextureWhenDeclared(AutoExposureTextureName);
                });
            debugBloomChoice.FalseCommands = tonemapCommands;
            commands.Add<VPRC_Manual>().ManualAction = RenderWebDebugDrawCallbacks;
            commands.Add<VPRC_RenderDebugShapes>().DepthTested = false;
            commands.Add<VPRC_RenderScreenSpaceUI>();
        }
        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, EMeshSubmissionStrategy.CpuDirect);
        return commands;
    }

    private static void DescribeWebRenderPasses(RenderPassMetadataCollection metadata)
    {
        metadata.ForPass((int)EDefaultRenderPass.PreRender, nameof(EDefaultRenderPass.PreRender), ERenderGraphPassStage.Graphics);
        int beforeForward = (int)EDefaultRenderPass.PreRender;
        string prepass = $"ForwardDepthNormalPrePass_{WebNormalFboName}";
        beforeForward = LinkWebPass(metadata, prepass, beforeForward);
        beforeForward = LinkWebQuadPass(metadata, WebGtaoGenerateQuadName, WebGtaoRawFboName, beforeForward);
        beforeForward = LinkWebQuadPass(metadata, WebGtaoHorizontalQuadName, WebGtaoHorizontalFboName, beforeForward);
        beforeForward = LinkWebQuadPass(metadata, WebGtaoVerticalQuadName, WebGtaoFinalFboName, beforeForward);
        metadata.ForPass((int)EDefaultRenderPass.OpaqueDeferred, nameof(EDefaultRenderPass.OpaqueDeferred), ERenderGraphPassStage.Graphics)
            .DependsOn(beforeForward);
        metadata.ForPass((int)EDefaultRenderPass.OpaqueForward, nameof(EDefaultRenderPass.OpaqueForward), ERenderGraphPassStage.Graphics)
            .DependsOn((int)EDefaultRenderPass.OpaqueDeferred);
        metadata.ForPass((int)EDefaultRenderPass.MaskedForward, nameof(EDefaultRenderPass.MaskedForward), ERenderGraphPassStage.Graphics)
            .DependsOn((int)EDefaultRenderPass.OpaqueForward);
        metadata.ForPass((int)EDefaultRenderPass.Background, nameof(EDefaultRenderPass.Background), ERenderGraphPassStage.Graphics)
            .DependsOn((int)EDefaultRenderPass.MaskedForward);
        metadata.ForPass((int)EDefaultRenderPass.TransparentForward, nameof(EDefaultRenderPass.TransparentForward), ERenderGraphPassStage.Graphics)
            .DependsOn((int)EDefaultRenderPass.Background);

        int beforeOutput = (int)EDefaultRenderPass.TransparentForward;
        beforeOutput = LinkWebQuadPass(metadata, WebBloomCopyQuadName, WebBloomMipFboNames[0], beforeOutput);
        for (int level = 1; level <= 4; level++)
            beforeOutput = LinkWebQuadPass(metadata, WebBloomDownQuadNames[level], WebBloomMipFboNames[level], beforeOutput);
        for (int level = 3; level >= 1; level--)
            beforeOutput = LinkWebQuadPass(metadata, WebBloomUpQuadNames[level], WebBloomMipFboNames[level], beforeOutput);
        beforeOutput = LinkWebQuadPass(metadata, WebBloomCombineQuadName, WebBloomCombinedFboName, beforeOutput);
        beforeOutput = LinkWebPass(metadata, nameof(VPRC_ExposureUpdate), beforeOutput, ERenderGraphPassStage.Compute);
        int tonemap = LinkWebQuadPass(metadata, WebTonemapFBOName, RenderGraphResourceNames.OutputRenderTarget, beforeOutput);
        int debugBloom = LinkWebQuadPass(metadata, WebBloomDebugOutputQuadName, RenderGraphResourceNames.OutputRenderTarget, beforeOutput);
        var overlay = metadata.ForPass((int)EDefaultRenderPass.OnTopForward, nameof(EDefaultRenderPass.OnTopForward), ERenderGraphPassStage.Graphics)
            .DependsOn(tonemap);
        if (debugBloom != beforeOutput)
            overlay.DependsOn(debugBloom);
        int afterOverlay = LinkWebPass(metadata, nameof(VPRC_RenderScreenSpaceUI), (int)EDefaultRenderPass.OnTopForward);
        metadata.ForPass((int)EDefaultRenderPass.PostRender, nameof(EDefaultRenderPass.PostRender), ERenderGraphPassStage.Graphics)
            .DependsOn(afterOverlay);
    }

    private static int LinkWebQuadPass(RenderPassMetadataCollection metadata, string sourceQuad,
        string destination, int dependency)
        => LinkWebPass(metadata, VPRC_RenderQuadToFBO.BuildQuadBlitPassName(sourceQuad, destination), dependency);

    private static int LinkWebPass(RenderPassMetadataCollection metadata, string name, int dependency,
        ERenderGraphPassStage stage = ERenderGraphPassStage.Graphics)
    {
        if (!metadata.TryGetPassIndex(name, out int index))
            return dependency;
        metadata.ForPass(index, name, stage).DependsOn(dependency);
        return index;
    }

    private static void RenderWebDebugDrawCallbacks()
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.ActiveMeshRenderCommands.RenderPublishedDebugDrawCallbacks();

    private void ValidateWebFrameRequirements()
    {
        RequireSupportedOutputResources();
        if (MeshSubmissionStrategy != EMeshSubmissionStrategy.CpuDirect)
            throw new NotSupportedException("WebGPU.DefaultPipeline.SubmissionUnsupported: explicitly select CpuDirect for this output.");
        ValidateWebGlobalIllumination();
        XRRenderPipelineInstance instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline!;
        if (instance.RenderState.ScreenSpaceUserInterface is { IsActive: true, IsScreenSpace: false })
            throw new NotSupportedException("WebGPU.DefaultPipeline.UiSpaceUnsupported: this output admits screen-space UI only.");
        ReadOnlySpan<int> unsupportedPasses =
        [
            (int)EDefaultRenderPass.DeferredDecals,
            (int)EDefaultRenderPass.WeightedBlendedOitForward, (int)EDefaultRenderPass.PerPixelLinkedListForward,
            (int)EDefaultRenderPass.DepthPeelingForward,
            (int)EDefaultRenderPass.PostBloomForward, (int)EDefaultRenderPass.PostMotionBlurForward,
            (int)EDefaultRenderPass.PostDepthOfFieldForward,
        ];
        foreach (int pass in unsupportedPasses)
            if (!IsWebSceneMeshPassSupported(pass) && instance.ActiveMeshRenderCommands.HasRenderingCommands(pass))
                throw new NotSupportedException($"WebGPU.DefaultPipeline.PassUnsupported: pass '{(EDefaultRenderPass)pass}' has no cooked output route.");
        instance.ActiveMeshRenderCommands.ValidatePublishedDebugDrawCallbacks();
        if (instance.ActiveMeshRenderCommands.HasRenderingMeshCommands((int)EDefaultRenderPass.OnTopForward))
            throw new NotSupportedException("WebGPU.DefaultPipeline.PassUnsupported: OnTopForward admits the published display debug callbacks, not scene mesh materials.");

        PipelinePostProcessState? state = (instance.RenderState.SceneCamera ?? instance.LastSceneCamera)?.GetPostProcessState(this);
        if (GetWebPostProcessRejection(state, out string selectedPass) is { } reason)
            throw new NotSupportedException($"WebGPU.DefaultPipeline.EffectUnsupported: selected pass '{selectedPass}': {reason}");
        if (ShouldRunAtmosphericScattering() || GetWebPipelineFeatureRejection() is not null)
            throw new NotSupportedException("WebGPU.DefaultPipeline.EffectUnsupported: a selected pipeline feature has no cooked WebGPU pass.");
    }

    private void SetWebTonemapUniforms(XRRenderProgram program)
    {
        PipelinePostProcessState? state = ResolveCurrentSettingsCamera()?.GetPostProcessState(this);
        ColorGradingSettings? color = GetSettings<ColorGradingSettings>(state);
        TonemappingSettings? tonemap = GetSettings<TonemappingSettings>(state);
        program.Uniform("TonemapParameters", new Vector4(color?.Exposure ?? 1.0f, color?.Gamma ?? 2.2f,
            tonemap?.MobiusTransition ?? TonemappingSettings.DefaultMobiusTransition,
            color?.UseGpuAutoExposureThisFrame == true ? 1.0f : 0.0f));
        program.Sampler("SourceTexture", RequireWebEffectTexture(
            ShouldUseWebBloom() ? WebBloomCombinedTextureName : HDRSceneTextureName), 0);
        if (color is { RequiresAutoExposure: true })
            program.Sampler(AutoExposureTextureName, RequireWebEffectTexture(AutoExposureTextureName), 1);
    }
}
