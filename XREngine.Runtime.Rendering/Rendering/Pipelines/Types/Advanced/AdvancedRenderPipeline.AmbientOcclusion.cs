using System.IO;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>
/// Runs the ambient occlusion mode that the camera's schema selects. The passes are the same
/// commands and shaders as the default pipeline, and they read the same settings. Advanced has no
/// normals before native shading, so a quad derives them from visibility depth first.
/// </summary>
public partial class AdvancedRenderPipeline
{
    private const int AoModeFieldShift = 42;
    private const ulong AoModeFieldMask = 0xFUL << AoModeFieldShift;
    private const int AoGtaoResolutionFieldShift = 46;
    private const ulong AoGtaoResolutionFieldMask = 0x3UL << AoGtaoResolutionFieldShift;

    private const int AoModeScreenSpace = (int)AmbientOcclusionSettings.EType.ScreenSpace + 1;
    private const int AoModeMultiView = (int)AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion + 1;
    private const int AoModeMultiScale = (int)AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance + 1;
    private const int AoModeHorizonBasedPlus = (int)AmbientOcclusionSettings.EType.HorizonBasedPlus + 1;
    private const int AoModeSpatialHash = (int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion + 1;
    private const int AoModeGroundTruth = (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion + 1;
    private const int AoModeVoxel = (int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion + 1;

    private const string AoNormalTextureName = "AdvancedAoNormal";
    private const string AoIntensityFBOName = "AdvancedAoIntensityFBO";
    private const string AoNormalQuadFBOName = "AdvancedAoNormalQuadFBO";
    private const string AoNormalFBOName = "AdvancedAoNormalFBO";
    private const string AoFinalizeQuadFBOName = "AdvancedAoFinalizeQuadFBO";
    private const string AoFinalizeFBOName = "AdvancedAoFinalizeFBO";

    private VPRC_SSAOPass? _ssaoAoProvider;
    private VPRC_MVAOPass? _mvaoAoProvider;
    private VPRC_MSVO? _msvoAoProvider;
    private VPRC_HBAOPlusPass? _hbaoPlusAoProvider;
    private VPRC_GTAOPass? _gtaoAoProvider;
    private VPRC_SpatialHashAOPass? _spatialHashAoProvider;

    /// <summary>
    /// True when this pipeline runs the camera's AO passes. Browser outputs keep the native GTAO
    /// stage, a minimal visibility output has no shading to occlude, and an output without post
    /// processing has no depth views for the passes.
    /// </summary>
    private bool UsesAmbientOcclusionPasses
        => !UsesMinimalVisibilityOutput && AllowsPostProcessing &&
            !Shaders.Compilation.WebPipelineRasterProgram.IsActive;

    /// <summary>Gets the AO mode for the resources of one generation. Zero means no AO resources.</summary>
    private ulong BuildAmbientOcclusionFeatureMask(XRRenderPipelineInstance instance, XRViewport? viewport)
    {
        if (!UsesAmbientOcclusionPasses || UseOpenXrVulkanDesktopStartupSafePath)
            return 0UL;

        AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings(instance, viewport);
        if (settings is null)
        {
            // A resize callback can arrive while the camera is unavailable. Keep the last layout.
            return instance.TryGetLastResourceFeatureMask(viewport, out ulong previous)
                ? previous & (AoModeFieldMask | AoGtaoResolutionFieldMask)
                : 0UL;
        }

        if (!settings.Enabled)
            return 0UL;

        ulong mode = (ulong)((int)AmbientOcclusionSettings.NormalizeType(settings.Type) + 1);
        ulong resolution = settings.GroundTruth.Resolution switch
        {
            GroundTruthAmbientOcclusionSettings.EResolution.Full => 1UL,
            GroundTruthAmbientOcclusionSettings.EResolution.Quarter => 2UL,
            _ => 0UL,
        };
        return (mode << AoModeFieldShift) | (resolution << AoGtaoResolutionFieldShift);
    }

    private static int DecodeAmbientOcclusionMode(RenderPipelineResourceProfile profile)
        => (int)((profile.FeatureMask & AoModeFieldMask) >> AoModeFieldShift);

    private static int DecodeGtaoResolutionDivisor(RenderPipelineResourceProfile profile)
        => ((profile.FeatureMask & AoGtaoResolutionFieldMask) >> AoGtaoResolutionFieldShift) switch
        {
            1UL => 1,
            2UL => 4,
            _ => 2,
        };

    private static bool AoModeRunsPasses(int mode)
        => mode is not (0 or AoModeVoxel);

    /// <summary>Gets the mode that the camera selects now, in the same form as the resource mask.</summary>
    private int EvaluateAmbientOcclusionMode()
    {
        if (!UsesAmbientOcclusionPasses || UseOpenXrVulkanDesktopStartupSafePath)
            return 0;

        AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings();
        return settings is { Enabled: true }
            ? (int)AmbientOcclusionSettings.NormalizeType(settings.Type) + 1
            : 0;
    }

    /// <summary>
    /// Runs after the native AO stage, which writes neutral AO. The chosen mode replaces it.
    /// </summary>
    private void AppendAdvancedAmbientOcclusionCommands(ViewportRenderCommandContainer commands)
    {
        if (!UsesAmbientOcclusionPasses)
            return;

        var active = commands.Add<VPRC_IfElse>();
        active.Label = "AdvancedAmbientOcclusionActive";
        active.ConditionEvaluator = () => AoModeRunsPasses(EvaluateAmbientOcclusionMode());

        var aoCommands = new ViewportRenderCommandContainer(this);
        using (aoCommands.AddUsing<VPRC_PushViewportRenderArea>(x => x.UseInternalResolution = true))
        {
            aoCommands.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(AoNormalQuadFBOName, AoNormalFBOName)
                .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                    .SampleTexture(DepthViewTextureName)
                    .UseColorTexture(AoNormalTextureName));

            var modes = aoCommands.Add<VPRC_Switch>();
            modes.SwitchEvaluator = EvaluateAmbientOcclusionMode;
            modes.Cases = new()
            {
                [AoModeScreenSpace] = CreateRawAmbientOcclusionCommands(
                    container => ConfigureSsao(container.Add<VPRC_SSAOPass>())),
                [AoModeMultiView] = CreateRawAmbientOcclusionCommands(
                    container => ConfigureMvao(container.Add<VPRC_MVAOPass>())),
                [AoModeMultiScale] = CreateRawAmbientOcclusionCommands(
                    container => ConfigureMsvo(container.Add<VPRC_MSVO>())),
                [AoModeHorizonBasedPlus] = CreateBlurredAmbientOcclusionCommands(
                    container => ConfigureHbaoPlus(container.Add<VPRC_HBAOPlusPass>()),
                    HBAOPlusRawTextureName, HBAOPlusBlurIntermediateTextureName, HBAOPlusBlurIntermediateFBOName,
                    matchDestinationRenderArea: false),
                [AoModeGroundTruth] = CreateBlurredAmbientOcclusionCommands(
                    container => ConfigureGtao(container.Add<VPRC_GTAOPass>()),
                    GTAORawTextureName, GTAOBlurIntermediateTextureName, GTAOBlurIntermediateFBOName,
                    matchDestinationRenderArea: true),
                [AoModeSpatialHash] = CreateSpatialHashAmbientOcclusionCommands(),
            };

            aoCommands.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(AoFinalizeQuadFBOName, AoFinalizeFBOName)
                .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                    .SampleTexture(AmbientOcclusionIntensityTextureName)
                    .UseColorTexture(AdvancedAmbientOcclusionContract.ResourceName));
        }
        active.TrueCommands = aoCommands;
    }

    private void ConfigureSsao(VPRC_SSAOPass pass)
    {
        pass.SetOptions(VPRC_SSAOPass.DefaultSamples, VPRC_SSAOPass.DefaultNoiseWidth, VPRC_SSAOPass.DefaultNoiseHeight,
            VPRC_SSAOPass.DefaultMinSampleDist, VPRC_SSAOPass.DefaultMaxSampleDist, Stereo);
        pass.SetGBufferInputTextureNames(AoNormalTextureName, DepthViewTextureName,
            AoNormalTextureName, AoNormalTextureName, DepthStencilTextureName,
            AoNormalTextureName);
        pass.SetOutputNames(AmbientOcclusionNoiseTextureName, AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName, AoIntensityFBOName);
        pass.GenerateNoiseKernel();
        _ssaoAoProvider = pass;
    }

    private void ConfigureMvao(VPRC_MVAOPass pass)
    {
        pass.SetOptions(VPRC_MVAOPass.DefaultSamples, VPRC_MVAOPass.DefaultNoiseWidth, VPRC_MVAOPass.DefaultNoiseHeight,
            VPRC_MVAOPass.DefaultMinSampleDist, VPRC_MVAOPass.DefaultMaxSampleDist, Stereo);
        pass.SetGBufferInputTextureNames(AoNormalTextureName, DepthViewTextureName,
            AoNormalTextureName, AoNormalTextureName, DepthStencilTextureName,
            AoNormalTextureName);
        pass.SetOutputNames(AmbientOcclusionNoiseTextureName, AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName, AoIntensityFBOName);
        pass.GenerateNoiseKernel();
        _mvaoAoProvider = pass;
    }

    private void ConfigureMsvo(VPRC_MSVO pass)
    {
        pass.SetOptions(Stereo);
        pass.SetGBufferInputTextureNames(AoNormalTextureName, DepthViewTextureName,
            AoNormalTextureName, AoNormalTextureName, DepthStencilTextureName,
            AoNormalTextureName);
        pass.SetOutputNames(AmbientOcclusionIntensityTextureName, AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName, AoIntensityFBOName);
        _msvoAoProvider = pass;
    }

    private void ConfigureHbaoPlus(VPRC_HBAOPlusPass pass)
    {
        pass.SetOptions(Stereo);
        pass.SetGBufferInputTextureNames(AoNormalTextureName, DepthViewTextureName,
            AoNormalTextureName, AoNormalTextureName, DepthStencilTextureName,
            AoNormalTextureName);
        pass.SetOutputNames(AmbientOcclusionIntensityTextureName, AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName,
            HBAOPlusBlurIntermediateFBOName, AoIntensityFBOName, HBAOPlusRawTextureName, HBAOPlusBlurIntermediateTextureName);
        _hbaoPlusAoProvider = pass;
    }

    private void ConfigureGtao(VPRC_GTAOPass pass)
    {
        pass.SetOptions(Stereo);
        pass.SetGBufferInputTextureNames(AoNormalTextureName, DepthViewTextureName,
            AoNormalTextureName, AoNormalTextureName, DepthStencilTextureName,
            AoNormalTextureName);
        pass.SetOutputNames(AmbientOcclusionIntensityTextureName, AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName,
            GTAOBlurIntermediateFBOName, AoIntensityFBOName, GTAORawTextureName, GTAOBlurIntermediateTextureName);
        _gtaoAoProvider = pass;
    }

    private ViewportRenderCommandContainer NewAmbientOcclusionBranch()
        => new(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };

    private static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor AoGenerateResources(string output)
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .UseColorTexture(output)
            .SampleTexture(AoNormalTextureName)
            .SampleTexture(DepthViewTextureName)
            .SampleTexture(AmbientOcclusionNoiseTextureName);

    /// <summary>SSAO, MVAO, and MSVO: generate into the raw texture, then blur into the intensity texture.</summary>
    private ViewportRenderCommandContainer CreateRawAmbientOcclusionCommands(Action<ViewportRenderCommandContainer> addPass)
    {
        ViewportRenderCommandContainer branch = NewAmbientOcclusionBranch();
        addPass(branch);
        branch.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName)
            .SetRenderGraphResources(AoGenerateResources(AmbientOcclusionRawTextureName));
        branch.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionBlurFBOName, AoIntensityFBOName)
            .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                .SampleTexture(AmbientOcclusionRawTextureName)
                .UseColorTexture(AmbientOcclusionIntensityTextureName));
        return branch;
    }

    /// <summary>HBAO+ and GTAO: generate, then blur horizontally and vertically.</summary>
    private ViewportRenderCommandContainer CreateBlurredAmbientOcclusionCommands(
        Action<ViewportRenderCommandContainer> addPass,
        string rawTexture,
        string intermediateTexture,
        string intermediateFbo,
        bool matchDestinationRenderArea)
    {
        ViewportRenderCommandContainer branch = NewAmbientOcclusionBranch();
        addPass(branch);
        branch.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName, matchDestinationRenderArea: matchDestinationRenderArea)
            .SetRenderGraphResources(AoGenerateResources(rawTexture));
        branch.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionBlurFBOName, intermediateFbo, matchDestinationRenderArea: matchDestinationRenderArea)
            .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                .SampleTexture(rawTexture)
                .SampleTexture(DepthViewTextureName)
                .SampleTexture(AoNormalTextureName)
                .UseColorTexture(intermediateTexture));
        branch.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(intermediateFbo, AoIntensityFBOName, matchDestinationRenderArea: matchDestinationRenderArea)
            .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                .SampleTexture(intermediateTexture)
                .SampleTexture(DepthViewTextureName)
                .SampleTexture(AoNormalTextureName)
                .UseColorTexture(AmbientOcclusionIntensityTextureName));
        return branch;
    }

    /// <summary>The spatial hash pass writes its intensity texture from compute, with no resolve quads.</summary>
    private ViewportRenderCommandContainer CreateSpatialHashAmbientOcclusionCommands()
    {
        ViewportRenderCommandContainer branch = NewAmbientOcclusionBranch();
        VPRC_SpatialHashAOPass pass = branch.Add<VPRC_SpatialHashAOPass>();
        pass.SetOptions(VPRC_SpatialHashAOPass.DefaultSamples, VPRC_SpatialHashAOPass.DefaultNoiseWidth,
            VPRC_SpatialHashAOPass.DefaultNoiseHeight, VPRC_SpatialHashAOPass.DefaultMinSampleDist,
            VPRC_SpatialHashAOPass.DefaultMaxSampleDist, Stereo);
        pass.SetGBufferInputTextureNames(AoNormalTextureName, DepthViewTextureName,
            AoNormalTextureName, AoNormalTextureName, DepthStencilTextureName,
            AoNormalTextureName);
        pass.SetOutputNames(AmbientOcclusionIntensityTextureName, AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName, AoIntensityFBOName);
        _spatialHashAoProvider = pass;
        return branch;
    }

    private IDeclaredAoResourceProvider RequireAoProvider(int mode)
        => (IDeclaredAoResourceProvider?)(mode switch
        {
            AoModeScreenSpace => _ssaoAoProvider,
            AoModeMultiView => _mvaoAoProvider,
            AoModeMultiScale => _msvoAoProvider,
            AoModeHorizonBasedPlus => _hbaoPlusAoProvider,
            AoModeGroundTruth => _gtaoAoProvider,
            AoModeSpatialHash => _spatialHashAoProvider,
            _ => null,
        }) ?? throw new InvalidOperationException($"No ambient occlusion provider is registered for mode {mode}.");

    private XRFrameBuffer CreateDeclaredAoFrameBuffer(int mode, string name)
    {
        XRRenderPipelineInstance instance = TryCurrentPipeline
            ?? throw new InvalidOperationException($"No active pipeline instance during creation of '{name}'.");
        return RequireAoProvider(mode).CreateDeclaredFrameBuffer(instance, name);
    }

    private void DeclareAmbientOcclusionResources(RenderPipelineResourceLayoutBuilder builder)
    {
        int mode = DecodeAmbientOcclusionMode(builder.Profile);
        if (mode == AoModeVoxel)
        {
            XREngine.Debug.RenderingWarningEvery(
                "AdvancedRenderPipeline.VoxelAmbientOcclusion",
                TimeSpan.FromSeconds(30),
                "VXAO is not implemented yet. Advanced renders neutral AO, as the default pipeline does.");
            return;
        }
        // A profile can carry values that no camera selects. AO output is absent when browser AO is off.
        if (!AoModeRunsPasses(mode) ||
            mode is not (AoModeScreenSpace or AoModeMultiView or AoModeMultiScale or
                AoModeHorizonBasedPlus or AoModeGroundTruth or AoModeSpatialHash) ||
            (builder.Profile.FeatureMask & WebAmbientOcclusionDisabledFeatureBit) != 0UL)
            return;

        uint layers = Math.Max(builder.Profile.ViewCount, builder.Profile.Stereo ? 2u : 1u);
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();

        DeclareLatePostColor(builder, AoNormalTextureName, internalSize, layers,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f);
        DeclareLatePostColor(builder, AmbientOcclusionIntensityTextureName, internalSize, layers,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f);

        builder.QuadMaterial(AoNormalQuadFBOName).Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(DepthViewTextureName).Factory(CreateAoNormalQuadFbo).Add();
        DeclareLatePostDestination(builder, AoNormalFBOName, AoNormalTextureName, internalSize,
            () => CreateAoColorFbo(AoNormalFBOName, AoNormalTextureName));

        builder.QuadMaterial(AoFinalizeQuadFBOName).Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(AmbientOcclusionIntensityTextureName).Factory(CreateAoFinalizeQuadFbo).Add();
        DeclareLatePostDestination(builder, AoFinalizeFBOName, AdvancedAmbientOcclusionContract.ResourceName, internalSize,
            () => CreateAoColorFbo(AoFinalizeFBOName, AdvancedAmbientOcclusionContract.ResourceName));

        DeclareLatePostDestination(builder, AoIntensityFBOName, AmbientOcclusionIntensityTextureName, internalSize,
            () => CreateAoColorFbo(AoIntensityFBOName, AmbientOcclusionIntensityTextureName));

        builder.Texture(AmbientOcclusionNoiseTextureName)
            .Size(RenderResourceSizePolicy.Absolute(4u, 4u))
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.RG, EPixelFormat.Rg, EPixelType.Float)
            .SizedFormat(ESizedInternalFormat.Rg32f)
            .Optional()
            .Add();

        switch (mode)
        {
            case AoModeScreenSpace or AoModeMultiView or AoModeMultiScale:
                DeclareLatePostColor(builder, AmbientOcclusionRawTextureName, internalSize, layers,
                    EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f);
                DeclareAoSourceFrameBuffer(builder, mode, AmbientOcclusionBlurFBOName, AmbientOcclusionRawTextureName, internalSize);
                break;
            case AoModeHorizonBasedPlus:
                DeclareAoScratchPair(builder, mode, HBAOPlusRawTextureName, HBAOPlusBlurIntermediateTextureName,
                    HBAOPlusBlurIntermediateFBOName, internalSize, layers);
                break;
            case AoModeGroundTruth:
                DeclareAoScratchPair(builder, mode, GTAORawTextureName, GTAOBlurIntermediateTextureName,
                    GTAOBlurIntermediateFBOName,
                    RenderResourceSizePolicy.Internal(1.0f / DecodeGtaoResolutionDivisor(builder.Profile)), layers);
                break;
            case AoModeSpatialHash:
                DeclareAoSourceFrameBuffer(builder, mode, AmbientOcclusionBlurFBOName, AmbientOcclusionIntensityTextureName, internalSize);
                DeclareSpatialHashAoResources(builder, layers);
                break;
        }

        // The generation pass reads the scratch textures, so it follows them.
        builder.FrameBuffer(AmbientOcclusionFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .DependsOn(DepthViewTextureName)
            .Color(0, AoNormalTextureName)
            .Factory(() => CreateDeclaredAoFrameBuffer(mode, AmbientOcclusionFBOName))
            .Add();
    }

    private void DeclareAoSourceFrameBuffer(RenderPipelineResourceLayoutBuilder builder, int mode, string fboName,
        string colorTexture, RenderResourceSizePolicy size)
        => builder.FrameBuffer(fboName)
            .Size(size)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(DepthViewTextureName, AoNormalTextureName)
            .Color(0, colorTexture)
            .Factory(() => CreateDeclaredAoFrameBuffer(mode, fboName))
            .Add();

    private void DeclareAoScratchPair(RenderPipelineResourceLayoutBuilder builder, int mode, string rawTexture,
        string intermediateTexture, string intermediateFbo, RenderResourceSizePolicy size, uint layers)
    {
        DeclareLatePostColor(builder, rawTexture, size, layers,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f);
        DeclareLatePostColor(builder, intermediateTexture, size, layers,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f);
        DeclareAoSourceFrameBuffer(builder, mode, AmbientOcclusionBlurFBOName, rawTexture, size);
        DeclareAoSourceFrameBuffer(builder, mode, intermediateFbo, intermediateTexture, size);
    }

    private void DeclareSpatialHashAoResources(RenderPipelineResourceLayoutBuilder builder, uint layers)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        const RenderPipelineResourceUsage historyUsage =
            RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage;

        foreach ((string name, EPixelInternalFormat format, EPixelFormat pixelFormat, EPixelType type, ESizedInternalFormat sized) in new[]
        {
            (VPRC_SpatialHashAOPass.HistoryAoTextureAName, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f),
            (VPRC_SpatialHashAOPass.HistoryAoTextureBName, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f),
            (VPRC_SpatialHashAOPass.HistoryDepthTextureAName, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f),
            (VPRC_SpatialHashAOPass.HistoryDepthTextureBName, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f),
        })
        {
            string captured = name;
            builder.Texture(captured)
                .Size(internalSize)
                .Usage(historyUsage)
                .Format(format, pixelFormat, type)
                .SizedFormat(sized)
                .Layers(layers)
                .StereoCompatible(builder.Profile.Stereo)
                .RequiresStorageUsage()
                .Factory(() => _spatialHashAoProvider!.CreateDeclaredTexture(TryCurrentPipeline!, captured)!)
                .Add();
        }

        uint pixelCount = Math.Max(builder.Profile.InternalWidth * builder.Profile.InternalHeight, 1u);
        uint capacity = VPRC_SpatialHashAOPass.NextPowerOfTwo(pixelCount * VPRC_SpatialHashAOPass.HashMapScale);
        if (capacity == 0u)
            capacity = 1024u;

        DeclareSpatialHashAoBuffer(builder, VPRC_SpatialHashAOPass.HashBufferName, sizeof(uint), capacity);
        DeclareSpatialHashAoBuffer(builder, VPRC_SpatialHashAOPass.HashTimeBufferName, sizeof(uint), capacity);
        DeclareSpatialHashAoBuffer(builder, VPRC_SpatialHashAOPass.SpatialBufferName, 2u * sizeof(uint), capacity);
    }

    private void DeclareSpatialHashAoBuffer(RenderPipelineResourceLayoutBuilder builder, string name, uint stride, uint capacity)
        => builder.Buffer(name)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)capacity * stride, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .Elements(stride, capacity)
            .Access(EBufferAccessPattern.ReadWrite)
            .Factory(() => _spatialHashAoProvider!.CreateDeclaredBuffer(TryCurrentPipeline!, name)!)
            .Add();

    private XRFrameBuffer CreateAoColorFbo(string name, string textureName)
    {
        if (GetTexture<XRTexture>(textureName) is not IFrameBufferAttachement attachment)
            throw new InvalidOperationException($"Ambient occlusion texture '{textureName}' must be FBO-attachable.");

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1)) { Name = name };
    }

    private static RenderingParameters CreateAoQuadRenderParameters()
        => new()
        {
            DepthTest =
            {
                Enabled = ERenderParamUsage.Unchanged,
                UpdateDepth = false,
                Function = EComparison.Always,
            }
        };

    private XRFrameBuffer CreateAoNormalQuadFbo()
    {
        XRShader shader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, Stereo ? "AONormalFromDepthStereo.fs" : "AONormalFromDepth.fs"),
            EShaderType.Fragment);
        XRTexture depth = GetTexture<XRTexture>(DepthViewTextureName)
            ?? throw new InvalidOperationException("Ambient occlusion normals require the depth view.");
        XRMaterial material = new([depth], shader)
        {
            RenderOptions = CreateAoQuadRenderParameters()
        };
        XRQuadFrameBuffer quad = new(material, true, false, Stereo) { Name = AoNormalQuadFBOName };
        quad.SettingUniforms += SetAoNormalUniforms;
        return quad;
    }

    private static void SetAoNormalUniforms(XRRenderProgram program)
    {
        XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (instance is null)
            return;

        SetAoScreenUniforms(program, instance);
        XRCamera? camera = instance.RenderState.SceneCamera
            ?? instance.RenderState.RenderingCamera
            ?? instance.LastSceneCamera
            ?? instance.LastRenderingCamera;
        if (camera is null)
            return;

        camera.SetUniforms(program);
        if (RuntimeEngine.Rendering.State.IsStereoPass)
            instance.RenderState.StereoRightEyeCamera?.SetUniforms(program, false);
    }

    private XRFrameBuffer CreateAoFinalizeQuadFbo()
    {
        XRShader shader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, Stereo ? "AOFinalizeStereo.fs" : "AOFinalize.fs"),
            EShaderType.Fragment);
        XRTexture intensity = GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName)
            ?? throw new InvalidOperationException("Ambient occlusion finalize requires the intensity texture.");
        XRMaterial material = new([intensity], shader)
        {
            RenderOptions = CreateAoQuadRenderParameters()
        };
        XRQuadFrameBuffer quad = new(material, true, false, Stereo) { Name = AoFinalizeQuadFBOName };
        quad.SettingUniforms += SetAoFinalizeUniforms;
        return quad;
    }

    private void SetAoFinalizeUniforms(XRRenderProgram program)
    {
        if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is { } instance)
            SetAoScreenUniforms(program, instance);
        AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings();
        program.Uniform("AmbientOcclusionPower", settings?.Power ?? 1.0f);
    }

    /// <summary>The AO shaders find each pixel's texture coordinate from these uniforms.</summary>
    private static void SetAoScreenUniforms(XRRenderProgram program, XRRenderPipelineInstance instance)
    {
        var region = instance.RenderState.CurrentRenderRegion;
        program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)region.Width);
        program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)region.Height);
        program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), System.Numerics.Vector2.Zero);
    }

    /// <summary>
    /// Gets how native shading applies AO, from the settings that the default pipeline's light
    /// combine reads. Only GTAO has multi-bounce and specular occlusion settings.
    /// </summary>
    internal (bool MultiBounce, bool SpecularOcclusion) ResolveAmbientOcclusionShadingOptions(
        XRRenderPipelineInstance instance, XRViewport? viewport)
    {
        AmbientOcclusionSettings? settings = ResolveAmbientOcclusionSettings(instance, viewport);
        return settings is not null &&
            AmbientOcclusionSettings.NormalizeType(settings.Type) == AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion
            ? (settings.GroundTruth.MultiBounceEnabled, settings.GroundTruth.SpecularOcclusionEnabled)
            : (false, false);
    }
}
