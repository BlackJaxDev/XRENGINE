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
    // Half-resolution GTAO inputs: one depth sample and its normal per 2x2 block.
    private const string AoHalfDepthTextureName = "AdvancedAoHalfDepth";
    // Positive linear view depth at GTAO resolution (0 for the far plane). Only GTAO generation reads it.
    private const string AoHalfLinearDepthTextureName = "AdvancedAoHalfLinearDepth";
    private const string AoPrepareQuadFBOName = "AdvancedAoPrepareQuadFBO";
    private const string AoPrepareFBOName = "AdvancedAoPrepareFBO";
    private const string AoUpsampleQuadFBOName = "AdvancedAoUpsampleQuadFBO";
    // Resolution field values. Half runs GTAO on half-resolution inputs and upsamples once.
    private const ulong AoGtaoResolutionHalf = 0UL;
    private const ulong AoGtaoResolutionFull = 1UL;
    private const ulong AoGtaoResolutionQuarter = 2UL;

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
        if (!UsesAmbientOcclusionPasses || ExcludesAmbientOcclusionForEyeResources(viewport))
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
            GroundTruthAmbientOcclusionSettings.EResolution.Full => AoGtaoResolutionFull,
            GroundTruthAmbientOcclusionSettings.EResolution.Quarter => AoGtaoResolutionQuarter,
            _ => AoGtaoResolutionHalf,
        };
        return (mode << AoModeFieldShift) | (resolution << AoGtaoResolutionFieldShift);
    }

    /// <summary>
    /// True when the resources of this pipeline serve a VR eye frame whose eye feature profile
    /// turns ambient occlusion off. The resource key reads only the explicit viewport and this
    /// pipeline's stereo flag, so a key built inside the eye render and one built outside it
    /// (resize, resource requests) get the same AO mask for the same eye. Only a call without a
    /// viewport reads the render that runs on this thread.
    /// </summary>
    private bool ExcludesAmbientOcclusionForEyeResources(XRViewport? viewport)
        => !VrEyeFrameRules.AllowsAmbientOcclusion(viewport, Stereo);

    private static int DecodeAmbientOcclusionMode(RenderPipelineResourceProfile profile)
        => (int)((profile.FeatureMask & AoModeFieldMask) >> AoModeFieldShift);

    private static int DecodeGtaoResolutionDivisor(RenderPipelineResourceProfile profile)
        => ((profile.FeatureMask & AoGtaoResolutionFieldMask) >> AoGtaoResolutionFieldShift) switch
        {
            AoGtaoResolutionFull => 1,
            AoGtaoResolutionQuarter => 4,
            _ => 2,
        };

    /// <summary>
    /// True when GTAO runs at half resolution on half-resolution inputs. A prepare pass then
    /// replaces the full-resolution AO normal pass, generation and both blur passes read the
    /// reduced depth and normals, and one depth-aware upsample writes the full-resolution AO.
    /// </summary>
    private static bool UsesReducedGtaoInputs(RenderPipelineResourceProfile profile)
        => DecodeAmbientOcclusionMode(profile) == AoModeGroundTruth &&
            ((profile.FeatureMask & AoGtaoResolutionFieldMask) >> AoGtaoResolutionFieldShift) == AoGtaoResolutionHalf;

    /// <summary>True when the current resource generation declares the reduced GTAO inputs.</summary>
    private bool CurrentGenerationUsesReducedGtaoInputs()
        => TryCurrentPipeline?.Resources.TryGetFrameBuffer(AoPrepareFBOName, out XRFrameBuffer? prepare) == true &&
            prepare is not null;

    private static bool AoModeRunsPasses(int mode)
        => mode is not (0 or AoModeVoxel);

    /// <summary>
    /// True when the AO passes of this frame write the whole native AO image after the native AO
    /// stage: the passes run (<see cref="AppendAdvancedAmbientOcclusionCommands"/>), and the
    /// finalize framebuffer exists in the current resource generation. The native stage then
    /// skips its neutral fill, because the finalize quad overwrites it before shading reads it.
    /// AO off, VXAO, an eye frame whose eye feature profile turns AO off, a missing camera and
    /// a missing finalize target all keep the fill.
    /// </summary>
    internal bool AmbientOcclusionPassesReplaceNeutralFill(XRRenderPipelineInstance instance)
        => UsesAmbientOcclusionPasses && VrEyeFrameRules.AllowsAmbientOcclusion() &&
            AoModeRunsPasses(EvaluateAmbientOcclusionMode()) &&
            instance.Resources.TryGetFrameBuffer(AoFinalizeFBOName, out XRFrameBuffer? finalize) &&
            finalize is not null;

    /// <summary>Gets the mode that the camera selects now, in the same form as the resource mask.</summary>
    private int EvaluateAmbientOcclusionMode()
    {
        if (!UsesAmbientOcclusionPasses || !VrEyeFrameRules.AllowsAmbientOcclusion())
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
            var reducedInputs = aoCommands.Add<VPRC_IfElse>();
            reducedInputs.Label = "AdvancedAmbientOcclusionReducedInputs";
            reducedInputs.ConditionEvaluator = CurrentGenerationUsesReducedGtaoInputs;
            var prepare = new ViewportRenderCommandContainer(this);
            prepare.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(AoPrepareQuadFBOName, AoPrepareFBOName, matchDestinationRenderArea: true)
                .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                    .SampleTexture(DepthViewTextureName)
                    .UseColorTexture(AoNormalTextureName)
                    .UseColorTexture(AoHalfDepthTextureName)
                    .UseColorTexture(AoHalfLinearDepthTextureName));
            var normals = new ViewportRenderCommandContainer(this);
            normals.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(AoNormalQuadFBOName, AoNormalFBOName)
                .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                    .SampleTexture(DepthViewTextureName)
                    .UseColorTexture(AoNormalTextureName));
            reducedInputs.TrueCommands = prepare;
            reducedInputs.FalseCommands = normals;

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
                [AoModeGroundTruth] = CreateGtaoCommands(),
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
        pass.ReducedInputDepthTextureName = AoHalfDepthTextureName;
        pass.ReducedInputLinearDepthTextureName = AoHalfLinearDepthTextureName;
        _gtaoAoProvider = pass;
    }

    /// <summary>
    /// GTAO commands. With reduced inputs, generation and both blur passes run at half
    /// resolution and read the half-resolution depth and normals. The vertical blur writes back
    /// into the raw texture, and the upsample pass writes the full-resolution AO texture.
    /// Without them (full or quarter resolution), the passes read the full-resolution inputs and
    /// the vertical blur upsamples.
    /// </summary>
    private ViewportRenderCommandContainer CreateGtaoCommands()
    {
        ViewportRenderCommandContainer branch = NewAmbientOcclusionBranch();
        ConfigureGtao(branch.Add<VPRC_GTAOPass>());
        var reducedInputs = branch.Add<VPRC_IfElse>();
        reducedInputs.Label = "AdvancedGtaoReducedInputs";
        reducedInputs.ConditionEvaluator = CurrentGenerationUsesReducedGtaoInputs;

        var reduced = new ViewportRenderCommandContainer(this);
        reduced.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName, matchDestinationRenderArea: true)
            .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                .UseColorTexture(GTAORawTextureName)
                .SampleTexture(AoNormalTextureName)
                .SampleTexture(AoHalfLinearDepthTextureName));
        reduced.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionBlurFBOName, GTAOBlurIntermediateFBOName, matchDestinationRenderArea: true)
            .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                .SampleTexture(GTAORawTextureName)
                .SampleTexture(AoHalfDepthTextureName)
                .SampleTexture(AoNormalTextureName)
                .UseColorTexture(GTAOBlurIntermediateTextureName));
        reduced.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(GTAOBlurIntermediateFBOName, AmbientOcclusionBlurFBOName, matchDestinationRenderArea: true)
            .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                .SampleTexture(GTAOBlurIntermediateTextureName)
                .SampleTexture(AoHalfDepthTextureName)
                .SampleTexture(AoNormalTextureName)
                .UseColorTexture(GTAORawTextureName));
        reduced.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AoUpsampleQuadFBOName, AoIntensityFBOName, matchDestinationRenderArea: true)
            .SetRenderGraphResources(new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
                .SampleTexture(GTAORawTextureName)
                .SampleTexture(DepthViewTextureName)
                .UseColorTexture(AmbientOcclusionIntensityTextureName));

        reducedInputs.TrueCommands = reduced;
        reducedInputs.FalseCommands = CreateBlurredAmbientOcclusionCommandList(
            GTAORawTextureName, GTAOBlurIntermediateTextureName, GTAOBlurIntermediateFBOName,
            matchDestinationRenderArea: true);
        return branch;
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
        AddBlurredAmbientOcclusionCommands(branch, rawTexture, intermediateTexture, intermediateFbo, matchDestinationRenderArea);
        return branch;
    }

    private ViewportRenderCommandContainer CreateBlurredAmbientOcclusionCommandList(
        string rawTexture,
        string intermediateTexture,
        string intermediateFbo,
        bool matchDestinationRenderArea)
    {
        var commands = new ViewportRenderCommandContainer(this);
        AddBlurredAmbientOcclusionCommands(commands, rawTexture, intermediateTexture, intermediateFbo, matchDestinationRenderArea);
        return commands;
    }

    /// <summary>Generate into the raw texture, blur horizontally, then blur vertically into the intensity texture.</summary>
    private static void AddBlurredAmbientOcclusionCommands(
        ViewportRenderCommandContainer branch,
        string rawTexture,
        string intermediateTexture,
        string intermediateFbo,
        bool matchDestinationRenderArea)
    {
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
        bool reducedGtaoInputs = UsesReducedGtaoInputs(builder.Profile);
        RenderResourceSizePolicy reducedSize = RenderResourceSizePolicy.Internal(0.5f);

        // With reduced GTAO inputs, the AO normals exist only at half resolution.
        DeclareLatePostColor(builder, AoNormalTextureName, reducedGtaoInputs ? reducedSize : internalSize, layers,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f);
        DeclareLatePostColor(builder, AmbientOcclusionIntensityTextureName, internalSize, layers,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f);

        if (reducedGtaoInputs)
        {
            DeclareLatePostColor(builder, AoHalfDepthTextureName, reducedSize, layers,
                EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f);
            DeclareLatePostColor(builder, AoHalfLinearDepthTextureName, reducedSize, layers,
                EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f);
            builder.QuadMaterial(AoPrepareQuadFBOName).Lifetime(RenderResourceLifetime.Transient)
                .DependsOn(DepthViewTextureName).Factory(CreateAoPrepareQuadFbo).Add();
            builder.FrameBuffer(AoPrepareFBOName).Size(reducedSize).Lifetime(RenderResourceLifetime.Persistent)
                .Usage(RenderPipelineResourceUsage.ColorAttachment)
                .Color(0, AoNormalTextureName).Color(1, AoHalfDepthTextureName).Color(2, AoHalfLinearDepthTextureName)
                .Factory(CreateAoPrepareFbo).Add();
            builder.QuadMaterial(AoUpsampleQuadFBOName).Lifetime(RenderResourceLifetime.Transient)
                .DependsOn(GTAORawTextureName, DepthViewTextureName).Factory(CreateAoUpsampleQuadFbo).Add();
        }
        else
        {
            builder.QuadMaterial(AoNormalQuadFBOName).Lifetime(RenderResourceLifetime.Transient)
                .DependsOn(DepthViewTextureName).Factory(CreateAoNormalQuadFbo).Add();
            DeclareLatePostDestination(builder, AoNormalFBOName, AoNormalTextureName, internalSize,
                () => CreateAoColorFbo(AoNormalFBOName, AoNormalTextureName));
        }

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
            .Size(reducedGtaoInputs ? reducedSize : internalSize)
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

    private XRFrameBuffer CreateAoPrepareQuadFbo()
    {
        XRShader shader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, Stereo ? "GTAOPrepareHalfStereo.fs" : "GTAOPrepareHalf.fs"),
            EShaderType.Fragment);
        XRTexture depth = GetTexture<XRTexture>(DepthViewTextureName)
            ?? throw new InvalidOperationException("The GTAO prepare pass requires the depth view.");
        XRMaterial material = new([depth], shader)
        {
            RenderOptions = CreateAoQuadRenderParameters()
        };
        XRQuadFrameBuffer quad = new(material, true, false, Stereo) { Name = AoPrepareQuadFBOName };
        quad.SettingUniforms += SetAoNormalUniforms;
        return quad;
    }

    private XRFrameBuffer CreateAoPrepareFbo()
    {
        if (GetTexture<XRTexture>(AoNormalTextureName) is not IFrameBufferAttachement normal ||
            GetTexture<XRTexture>(AoHalfDepthTextureName) is not IFrameBufferAttachement depth ||
            GetTexture<XRTexture>(AoHalfLinearDepthTextureName) is not IFrameBufferAttachement linearDepth)
            throw new InvalidOperationException("The GTAO prepare targets must be FBO-attachable.");

        return new XRFrameBuffer(
            (normal, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depth, EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (linearDepth, EFrameBufferAttachment.ColorAttachment2, 0, -1))
        {
            Name = AoPrepareFBOName
        };
    }

    private XRFrameBuffer CreateAoUpsampleQuadFbo()
    {
        XRShader shader = XRShader.EngineShader(
            Path.Combine(SceneShaderPath, Stereo ? "GTAOUpsampleStereo.fs" : "GTAOUpsample.fs"),
            EShaderType.Fragment);
        XRTexture raw = GetTexture<XRTexture>(GTAORawTextureName)
            ?? throw new InvalidOperationException("The GTAO upsample pass requires the blurred half-resolution AO.");
        XRTexture depth = GetTexture<XRTexture>(DepthViewTextureName)
            ?? throw new InvalidOperationException("The GTAO upsample pass requires the depth view.");
        XRMaterial material = new([raw, depth], shader)
        {
            RenderOptions = CreateAoQuadRenderParameters()
        };
        XRQuadFrameBuffer quad = new(material, true, false, Stereo) { Name = AoUpsampleQuadFBOName };
        quad.SettingUniforms += SetAoNormalUniforms;
        return quad;
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
        if (UseTypedPostProcessPublishers)
            quad.FullScreenMesh.BindingPublishers.Add(new AoFinalizeBindingPublisher(this));
        else
            quad.SettingUniforms += SetAoFinalizeUniforms;
        return quad;
    }

    /// <summary>
    /// Publishes the AO finalize values through a typed publisher, so the backend
    /// keeps the captured binding snapshot while the render region, AO power and AO
    /// input texture stay the same. A uniform callback forces a new snapshot every
    /// frame. The shader samples <c>AmbientOcclusionTexture</c>, an engine AO name, so
    /// the publisher owns the AO requirement and binds the intensity texture itself.
    /// </summary>
    private sealed class AoFinalizeBindingPublisher(AdvancedRenderPipeline owner) :
        IRenderResourceBindingPublisher,
        IPersistentProgramBindingRequirementOwner
    {
        private readonly RenderBindingGenerationTracker<AoFinalizeBindingKey> _generation = new();

        public ERenderBindingFrequency Frequency => ERenderBindingFrequency.Pass;

        public ulong Generation
        {
            get
            {
                XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
                var region = instance?.RenderState.CurrentRenderRegion ?? default;
                return _generation.Advance(new AoFinalizeBindingKey(
                    instance is not null,
                    region.Width,
                    region.Height,
                    owner.ResolveAmbientOcclusionSettings()?.Power ?? 1.0f,
                    GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName),
                    instance?.ResourceGeneration ?? 0));
            }
        }

        public ulong ResourceGeneration => Generation;

        public EUniformRequirements OwnedPersistentArtifactRequirement => EUniformRequirements.AmbientOcclusion;

        public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => owner.SetAoFinalizeUniforms(materialProgram);

        public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            if (GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName) is { } intensity)
                materialProgram.Sampler(EngineShaderBindingNames.Samplers.AmbientOcclusionTexture, intensity, 0);
        }
    }

    /// <summary>False when the A/B switch selects the uniform callbacks for these passes.</summary>
    private static readonly bool UseTypedPostProcessPublishers =
        XREnvironment.GetValue(XREngineEnvironmentVariables.AdvancedTypedPostProcessPublishers)?.Trim() != "0";

    private readonly record struct AoFinalizeBindingKey(
        bool HasInstance,
        int Width,
        int Height,
        float Power,
        XRTexture? Intensity,
        int PipelineResourceGeneration);

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
