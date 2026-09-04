using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Components.Scene.Volumes;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private VPRC_BloomPass? _advancedBloomProvider;
    /// <summary>
    /// Executes authored forward-only work over the native opaque HDR/depth result.
    /// <see cref="HDRSceneTextureName"/> and the depth views must already alias the
    /// native visibility outputs; this path deliberately performs no opaque rerender.
    /// </summary>
    private void AppendAdvancedLatePassCommands(ViewportRenderCommandContainer commands)
    {
        var late = commands.Add<VPRC_IfElse>();
        late.Label = "AdvancedLateTransparencyAllowed";
        late.ConditionEvaluator = () => AllowsLateTransparency && HasAnyAdvancedLateConsumers();
        var lateCommands = new ViewportRenderCommandContainer(this);

        // The native stage owns HDRScene. Snapshot it only when a visible late
        // consumer needs to sample the opaque scene; never introduce a frame
        // readback to decide this.
        var snapshot = lateCommands.Add<VPRC_IfElse>();
        snapshot.Label = "AdvancedSceneSnapshotRequired";
        snapshot.ConditionEvaluator = RequiresAdvancedSceneSnapshot;
        var snapshotCommands = new ViewportRenderCommandContainer(this);
        snapshotCommands.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName)
            .SetRenderGraphResources(CreateAdvancedSceneCopyResources());
        snapshot.TrueCommands = snapshotCommands;

        AppendAdvancedWeightedTransparency(lateCommands);
        using (lateCommands.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(ForwardPassFBOName, write: true, clearColor: false, clearDepth: false, clearStencil: false)))
        {
            lateCommands.Add<VPRC_ColorMask>().Set(true, true, true, true);
            lateCommands.Add<VPRC_DepthTest>().Enable = true;
            lateCommands.Add<VPRC_DepthWrite>().Allow = false;
            lateCommands.Add<VPRC_RenderMeshesPass>()
                .SetOptions((int)EDefaultRenderPass.TransparentForward, EMeshSubmissionStrategy.CpuDirect);
            lateCommands.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
            lateCommands.Add<VPRC_RenderMeshesPass>()
                .SetOptions((int)EDefaultRenderPass.OnTopForward, EMeshSubmissionStrategy.CpuDirect);
            lateCommands.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
            lateCommands.Add<VPRC_DepthWrite>().Allow = true;
        }

        AppendExactTransparencyCommands(lateCommands);
        late.TrueCommands = lateCommands;
    }

    private static bool HasRenderPassCommands(int renderPass)
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.ActiveMeshRenderCommands.HasRenderingCommands(renderPass) == true;

    private bool HasAnyAdvancedLateConsumers()
        => HasRenderPassCommands((int)EDefaultRenderPass.WeightedBlendedOitForward)
        || HasRenderPassCommands((int)EDefaultRenderPass.TransparentForward)
        || HasRenderPassCommands((int)EDefaultRenderPass.OnTopForward)
        || HasAdvancedExactTransparencyConsumers();

    private bool RequiresAdvancedSceneSnapshot()
        => HasRenderPassCommands((int)EDefaultRenderPass.WeightedBlendedOitForward)
        || HasAdvancedExactTransparencyConsumers();

    private void AppendAdvancedWeightedTransparency(ViewportRenderCommandContainer commands)
    {
        var weighted = commands.Add<VPRC_IfElse>();
        weighted.Label = "AdvancedWeightedBlendedOitActive";
        weighted.ConditionEvaluator = ShouldRunAdvancedWeightedTransparency;
        var weightedCommands = new ViewportRenderCommandContainer(this);
        weightedCommands.Add<VPRC_ClearTextureByName>().SetOptions(TransparentAccumTextureName, ColorF4.Transparent);
        weightedCommands.Add<VPRC_ClearTextureByName>().SetOptions(TransparentRevealageTextureName, ColorF4.White);
        using (weightedCommands.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(TransparentAccumulationFBOName, true, false, false, false)))
        {
            weightedCommands.Add<VPRC_DepthTest>().Enable = true;
            weightedCommands.Add<VPRC_DepthWrite>().Allow = false;
            weightedCommands.Add<VPRC_RenderMeshesPass>()
                .SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, EMeshSubmissionStrategy.CpuDirect);
        }
        weightedCommands.Add<VPRC_RenderQuadToFBO>().SetOptions(TransparentResolveFBOName, renderToSourceFrameBuffer: true);
        weighted.TrueCommands = weightedCommands;
    }

    private bool ShouldRunAdvancedWeightedTransparency()
        => AllowsLateTransparency && EnableWeightedBlendedOitPasses
        && HasRenderPassCommands((int)EDefaultRenderPass.WeightedBlendedOitForward);

    /// <summary>
    /// Runs the existing compositing chain from the native HDR scene result and keeps
    /// temporal history ownership in the existing temporal command primitive.
    /// </summary>
    private void AppendAdvancedPostProcessCommands(ViewportRenderCommandContainer commands)
    {
        var post = commands.Add<VPRC_IfElse>();
        post.Label = "AdvancedPostProcessingAllowed";
        post.ConditionEvaluator = () => AllowsPostProcessing;
        var postCommands = new ViewportRenderCommandContainer(this);
        AppendAdvancedPostProcessCommandsCore(postCommands);
        post.TrueCommands = postCommands;
    }

    private void AppendAdvancedPostProcessCommandsCore(ViewportRenderCommandContainer commands)
    {
        AppendAdvancedTemporalAccumulation(commands);
        AppendAdvancedMotionBlurAndDepthOfField(commands);
        AppendAdvancedAtmosphereAndFog(commands);

        var bloom = commands.Add<VPRC_IfElse>();
        bloom.Label = "AdvancedBloomActive";
        bloom.ConditionEvaluator = () => AllowsBloomAndDepthOfField && ShouldUseBloom();
        var bloomCommands = new ViewportRenderCommandContainer(this);
        _advancedBloomProvider = bloomCommands.Add<VPRC_BloomPass>();
        _advancedBloomProvider.SetTargetFBONames(ForwardPassFBOName, BloomBlurTextureName, Stereo);
        bloom.TrueCommands = bloomCommands;

        commands.Add<VPRC_ExposureUpdate>().SetOptions(HDRSceneTextureName, true);
        using (commands.AddUsing<VPRC_PushViewportRenderArea>(x => x.UseInternalResolution = true))
        {
            commands.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(PostProcessFBOName, PostProcessOutputFBOName, matchDestinationRenderArea: true)
                .SetRenderGraphResources(CreateAdvancedPostProcessResources());
            commands.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(FinalPostProcessFBOName, FinalPostProcessOutputFBOName, matchDestinationRenderArea: true)
                .SetRenderGraphResources(CreateAdvancedFinalPostProcessResources());
        }

        AppendAdvancedPostAntiAliasing(commands);
        var temporalCommit = commands.Add<VPRC_IfElse>();
        temporalCommit.Label = "AdvancedTemporalCommitActive";
        temporalCommit.ConditionEvaluator = ShouldUseAdvancedTemporalAccumulationResources;
        var temporalCommitCommands = new ViewportRenderCommandContainer(this);
        temporalCommitCommands.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Commit;
        temporalCommit.TrueCommands = temporalCommitCommands;
    }

    // TSR consumes the temporal history inputs even though it does not use the
    // internal TAA resolve. Keep Begin/Accumulate/Commit in one lifecycle gate
    // so resize and camera-cut invalidation are observed before the TSR pass.
    private bool ShouldUseAdvancedTemporalAccumulationResources()
        => AllowsTemporalHistory &&
           (RuntimeNeedsTemporalAaVelocityBuffer || RuntimeNeedsTsrUpscale);
    private void AppendAdvancedTemporalAccumulation(ViewportRenderCommandContainer commands)
    {
        var temporal = commands.Add<VPRC_IfElse>();
        temporal.Label = "AdvancedTemporalAccumulationActive";
        temporal.ConditionEvaluator = ShouldUseAdvancedTemporalAccumulationResources;
        var temporalCommands = new ViewportRenderCommandContainer(this);
        var accumulate = temporalCommands.Add<VPRC_TemporalAccumulationPass>();
        accumulate.Phase = VPRC_TemporalAccumulationPass.EPhase.Accumulate;
        accumulate.ConfigureAccumulationTargets(
            ForwardPassFBOName,
            TemporalInputFBOName,
            TemporalAccumulationFBOName,
            HistoryCaptureFBOName,
            HistoryExposureFBOName);
        temporalCommands.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.PopJitter;
        temporal.TrueCommands = temporalCommands;
    }

    private void AppendAdvancedTemporalBegin(ViewportRenderCommandContainer commands)
    {
        var temporal = commands.Add<VPRC_IfElse>();
        temporal.Label = "AdvancedTemporalBeginActive";
        temporal.ConditionEvaluator = ShouldUseAdvancedTemporalAccumulationResources;
        var temporalCommands = new ViewportRenderCommandContainer(this);
        temporalCommands.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Begin;
        temporal.TrueCommands = temporalCommands;
    }

    private void AppendAdvancedPostAntiAliasing(ViewportRenderCommandContainer commands)
    {
        var aa = commands.Add<VPRC_IfElse>();
        aa.Label = "AdvancedPostAntiAliasingActive";
        aa.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableSmaa || RuntimeNeedsTsrUpscale;
        var aaCommands = new ViewportRenderCommandContainer(this);
        var tsr = aaCommands.Add<VPRC_IfElse>();
        tsr.ConditionEvaluator = () => RuntimeNeedsTsrUpscale;
        var tsrCommands = new ViewportRenderCommandContainer(this);
        tsrCommands.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(TsrUpscaleFBOName, TsrUpscaleFBOName, matchDestinationRenderArea: true)
            .SetRenderGraphResources(CreateAdvancedTsrResources());
        tsrCommands.Add<VPRC_BlitFrameBuffer>().SetOptions(TsrUpscaleFBOName, TsrHistoryColorFBOName,
            EReadBufferMode.ColorAttachment0, blitColor: true, blitDepth: false, blitStencil: false, linearFilter: false);
        tsr.TrueCommands = tsrCommands;
        var postAaCommands = new ViewportRenderCommandContainer(this);
        var fxaa = postAaCommands.Add<VPRC_IfElse>();
        fxaa.ConditionEvaluator = () => RuntimeEnableFxaa;
        var fxaaCommands = new ViewportRenderCommandContainer(this);
        var fxaaPass = fxaaCommands.Add<VPRC_FXAA>();
        fxaaPass.SourceFBOName = FinalPostProcessOutputFBOName;
        fxaaPass.DestinationFBOName = FxaaFBOName;
        fxaaPass.Stereo = Stereo;
        fxaa.TrueCommands = fxaaCommands;
        var smaaCommands = new ViewportRenderCommandContainer(this);
        var smaaPass = smaaCommands.Add<VPRC_SMAA>();
        smaaPass.SourceFBOName = FinalPostProcessOutputFBOName;
        smaaPass.OutputTextureName = SmaaOutputTextureName;
        smaaPass.OutputFBOName = SmaaFBOName;
        smaaPass.Stereo = Stereo;
        fxaa.FalseCommands = smaaCommands;
        tsr.FalseCommands = postAaCommands;
        aa.TrueCommands = aaCommands;
    }

    private void AppendAdvancedMotionBlurAndDepthOfField(ViewportRenderCommandContainer commands)
    {
        var motionBlur = commands.Add<VPRC_IfElse>();
        motionBlur.Label = "AdvancedMotionBlurActive";
        motionBlur.ConditionEvaluator = () => AllowsBloomAndDepthOfField && ShouldUseMotionBlur();
        motionBlur.TrueCommands = CreateAdvancedSceneFilterCommands(MotionBlurCopyFBOName, MotionBlurFBOName, CreateAdvancedMotionBlurResources());

        var depthOfField = commands.Add<VPRC_IfElse>();
        depthOfField.Label = "AdvancedDepthOfFieldActive";
        depthOfField.ConditionEvaluator = () => AllowsBloomAndDepthOfField && ShouldUseDepthOfField();
        depthOfField.TrueCommands = CreateAdvancedSceneFilterCommands(DepthOfFieldCopyFBOName, DepthOfFieldFBOName, CreateAdvancedDepthOfFieldResources());
    }

    private ViewportRenderCommandContainer CreateAdvancedSceneFilterCommands(string copyFboName, string filterFboName,
        VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor resources)
    {
        var commands = new ViewportRenderCommandContainer(this);
        commands.Add<VPRC_BlitFrameBuffer>().SetOptions(ForwardPassFBOName, copyFboName,
            EReadBufferMode.ColorAttachment0, blitColor: true, blitDepth: false, blitStencil: false, linearFilter: false);
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(filterFboName, ForwardPassFBOName).SetRenderGraphResources(resources);
        return commands;
    }

    private void AppendAdvancedAtmosphereAndFog(ViewportRenderCommandContainer commands)
    {
        AppendAdvancedNeutralCompositeInput(commands, AtmosphereUpscaleFBOName, "AdvancedAtmosphereNeutral");
        AppendAdvancedNeutralCompositeInput(commands, VolumetricFogUpscaleFBOName, "AdvancedVolumetricFogNeutral");

        var atmosphere = commands.Add<VPRC_IfElse>();
        atmosphere.Label = "AdvancedAtmosphereActive";
        atmosphere.ConditionEvaluator = ShouldRunAdvancedAtmosphericScattering;
        atmosphere.TrueCommands = CreateAdvancedAtmosphereCommands();
        var fog = commands.Add<VPRC_IfElse>();
        fog.Label = "AdvancedVolumetricFogActive";
        fog.ConditionEvaluator = ShouldRunAdvancedVolumetricFog;
        fog.TrueCommands = CreateAdvancedVolumetricFogCommands();
    }

    private bool ShouldRunAdvancedAtmosphericScattering()
    {
        if (!AllowsBloomAndDepthOfField || Stereo || UseOpenXrVulkanDesktopStartupSafePath)
            return false;
        var state = RuntimeEngine.Rendering.State.RenderingPipelineState?.SceneCamera?.GetActivePostProcessState();
        var settings = GetSettings<AtmosphericScatteringSettings>(state) ?? AtmosphericScatteringSettings.Default;
        return settings.Enabled && (settings.AerialPerspective || settings.DebugMode != AtmosphericScatteringSettings.EDebugMode.Off)
            && settings.MaxDistance > 0.0f && settings.SelectActiveAtmosphere(out var active) && active is { HasAerialPerspective: true };
    }

    private bool ShouldRunAdvancedVolumetricFog()
    {
        if (!AllowsBloomAndDepthOfField || Stereo || UseOpenXrVulkanDesktopStartupSafePath)
            return false;
        var state = RuntimeEngine.Rendering.State.RenderingPipelineState?.SceneCamera?.GetActivePostProcessState();
        var settings = GetSettings<VolumetricFogSettings>(state);
        var world = RuntimeEngine.Rendering.State.RenderingWorld;
        return settings is { Enabled: true } && settings.Intensity > 0.0f && settings.MaxDistance > 0.0f
            && world is not null && VolumetricFogVolumeComponent.Registry.HasActive(world);
    }

    private void AppendAdvancedNeutralCompositeInput(ViewportRenderCommandContainer commands, string fboName, string label)
    {
        var neutral = commands.Add<VPRC_IfElse>();
        neutral.Label = label;
        neutral.ConditionEvaluator = () => !Stereo;
        var clear = new ViewportRenderCommandContainer(this);
        clear.Add<VPRC_SetClears>().Set(ColorF4.Transparent, null, null);
        using (clear.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(fboName, true, true, false, false))) { }
        neutral.TrueCommands = clear;
    }

    private ViewportRenderCommandContainer CreateAdvancedAtmosphereCommands()
    {
        var commands = new ViewportRenderCommandContainer(this);
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(AtmosphereHalfDepthQuadFBOName, AtmosphereHalfDepthFBOName, matchDestinationRenderArea: true);
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(AtmosphereHalfScatterQuadFBOName, AtmosphereHalfScatterFBOName, matchDestinationRenderArea: true);
        commands.Add<VPRC_AtmosphereHistoryPass>().Phase = VPRC_AtmosphereHistoryPass.EPhase.Begin;
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(AtmosphereReprojectQuadFBOName, AtmosphereReprojectFBOName, matchDestinationRenderArea: true);
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(AtmosphereUpscaleQuadFBOName, AtmosphereUpscaleFBOName, matchDestinationRenderArea: true);
        commands.Add<VPRC_BlitFrameBuffer>().SetOptions(AtmosphereReprojectFBOName, AtmosphereHistoryFBOName, EReadBufferMode.ColorAttachment0, true, false, false, false);
        commands.Add<VPRC_AtmosphereHistoryPass>().Phase = VPRC_AtmosphereHistoryPass.EPhase.Commit;
        return commands;
    }

    private ViewportRenderCommandContainer CreateAdvancedVolumetricFogCommands()
    {
        var commands = new ViewportRenderCommandContainer(this);
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(VolumetricFogHalfDepthQuadFBOName, VolumetricFogHalfDepthFBOName, matchDestinationRenderArea: true);
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(VolumetricFogHalfScatterQuadFBOName, VolumetricFogHalfScatterFBOName, matchDestinationRenderArea: true);
        commands.Add<VPRC_VolumetricFogHistoryPass>().Phase = VPRC_VolumetricFogHistoryPass.EPhase.Begin;
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(VolumetricFogReprojectQuadFBOName, VolumetricFogReprojectFBOName, matchDestinationRenderArea: true);
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(VolumetricFogUpscaleQuadFBOName, VolumetricFogUpscaleFBOName, matchDestinationRenderArea: true);
        commands.Add<VPRC_BlitFrameBuffer>().SetOptions(VolumetricFogReprojectFBOName, VolumetricFogHistoryFBOName, EReadBufferMode.ColorAttachment0, true, false, false, false);
        commands.Add<VPRC_VolumetricFogHistoryPass>().Phase = VPRC_VolumetricFogHistoryPass.EPhase.Commit;
        return commands;
    }

    private void AppendAdvancedOutputCommands(ViewportRenderCommandContainer commands)
    {
        if (OffscreenProfile is not null)
            return;

        var output = commands.Add<VPRC_IfElse>();
        output.Label = "AdvancedFinalOutputSource";
        output.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableSmaa || RuntimeNeedsTsrUpscale;
        var antiAliasedOutput = new ViewportRenderCommandContainer(this);
        var tsr = antiAliasedOutput.Add<VPRC_IfElse>();
        tsr.ConditionEvaluator = () => RuntimeNeedsTsrUpscale;
        tsr.TrueCommands = CreateAdvancedPresentCommands(TsrUpscaleFBOName);
        var postAa = new ViewportRenderCommandContainer(this);
        var fxaa = postAa.Add<VPRC_IfElse>();
        fxaa.ConditionEvaluator = () => RuntimeEnableFxaa;
        fxaa.TrueCommands = CreateAdvancedPresentCommands(FxaaFBOName);
        fxaa.FalseCommands = CreateAdvancedPresentCommands(SmaaFBOName);
        tsr.FalseCommands = postAa;
        output.TrueCommands = antiAliasedOutput;
        output.FalseCommands = CreateAdvancedPresentCommands(FinalPostProcessOutputFBOName);
    }

    private ViewportRenderCommandContainer CreateAdvancedPresentCommands(string sourceFboName)
    {
        var commands = new ViewportRenderCommandContainer(this);
        commands.Add<VPRC_RenderToWindow>().SourceFBOName = sourceFboName;
        return commands;
    }

    private static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor CreateAdvancedSceneCopyResources()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor().SampleTexture(HDRSceneTextureName);

    private static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor CreateAdvancedPostProcessResources()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(HDRSceneTextureName)
            .SampleTexture(BloomBlurTextureName)
            .SampleTexture(DepthViewTextureName)
            .SampleTexture(StencilViewTextureName)
            .SampleTexture(AutoExposureTextureName);

    private static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor CreateAdvancedFinalPostProcessResources()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor().SampleTexture(PostProcessOutputTextureName);

    private static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor CreateAdvancedTsrResources()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor()
            .SampleTexture(FinalPostProcessOutputTextureName)
            .SampleTexture(VelocityTextureName)
            .SampleTexture(DepthViewTextureName)
            .SampleTexture(HistoryDepthViewTextureName)
            .SampleTexture(TsrHistoryColorTextureName)
            .SampleTexture(StencilViewTextureName);

    private static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor CreateAdvancedMotionBlurResources()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor().SampleTexture(MotionBlurTextureName).SampleTexture(VelocityTextureName).SampleTexture(DepthViewTextureName);

    private static VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor CreateAdvancedDepthOfFieldResources()
        => new VPRC_RenderQuadToFBO.RenderGraphResourceDescriptor().SampleTexture(DepthOfFieldTextureName).SampleTexture(DepthViewTextureName);
}
