using System;
using System.IO;
using System.Numerics;
using XREngine.Components.Scene.Volumes;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    // \u2500\u2500 AO provider references \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\n    // Captured during Configure*Pass calls; used by DeclareAmbientOcclusionFBOs factories.\n    private IDeclaredAoResourceProvider? _disabledAoProvider;\n    private IDeclaredAoResourceProvider? _ssaoAoProvider;\n    private IDeclaredAoResourceProvider? _mvaoAoProvider;\n    private IDeclaredAoResourceProvider? _msvoAoProvider;\n    private IDeclaredAoResourceProvider? _hbaoPlusAoProvider;\n    private IDeclaredAoResourceProvider? _gtaoAoProvider;\n    private IDeclaredAoResourceProvider? _spatialHashAoProvider;
    private IDeclaredAoResourceProvider? _disabledAoProvider;
    private IDeclaredAoResourceProvider? _ssaoAoProvider;
    private IDeclaredAoResourceProvider? _mvaoAoProvider;
    private IDeclaredAoResourceProvider? _msvoAoProvider;
    private IDeclaredAoResourceProvider? _hbaoPlusAoProvider;
    private IDeclaredAoResourceProvider? _gtaoAoProvider;
    private IDeclaredAoResourceProvider? _spatialHashAoProvider;
    private VPRC_BloomPass? _bloomProvider;

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer c = new(this);
        var ifElse = c.Add<VPRC_IfElse>();
        ifElse.ConditionEvaluator = ShouldUseViewportTargetCommands;
        ifElse.TrueCommands = CreateViewportTargetCommands();
        ifElse.FalseCommands = CreateFBOTargetCommands();
        return c;
    }

    private static bool ShouldUseViewportTargetCommands()
    {
        XRViewport? viewport = State.WindowViewport;
        if (viewport is null)
            return false;

        if (RuntimeEngine.Rendering.State.RenderingTargetOutputFBO is null)
            return true;

        if (viewport.UseDirectFboTargetCommandsWhenRenderingToFbo)
            return false;

        return RuntimeEngine.Rendering.State.IsStereoPass
            || RuntimeEngine.Rendering.State.IsSceneCapturePass
            || RuntimeEngine.Rendering.State.IsLightProbePass;
    }

    public ViewportRenderCommandContainer CreateFBOTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);
        bool enableComputePasses = EnableComputeDependentPasses;

        c.Add<VPRC_ColorMask>().Set(true, true, true, true);
        c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
        c.Add<VPRC_DepthWrite>().Allow = true;
        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.PreRender, branch =>
            branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false));

        using (c.AddUsing<VPRC_PushOutputFBORenderArea>())
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                c.Add<VPRC_StencilMask>().Set(~0u);
                c.Add<VPRC_ClearByBoundFBO>();
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.Background, branch =>
                    branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, MeshSubmissionStrategy));
                c.Add<VPRC_DepthWrite>().Allow = true;
                RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.OpaqueDeferred, branch =>
                    branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, MeshSubmissionStrategy));
                if (enableComputePasses)
                {
                    RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.ComputeLighting, branch =>
                        branch.Add<VPRC_ForwardPlusLightCullingPass>());
                }
                RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.OpaqueForward, branch =>
                    branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, MeshSubmissionStrategy));
                RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.Masked, branch =>
                    branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, MeshSubmissionStrategy));
                RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.Transparent, branch =>
                {
                    branch.Add<VPRC_DepthWrite>().Allow = false;
                    branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, MeshSubmissionStrategy);
                    branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PerPixelLinkedListForward, MeshSubmissionStrategy);
                    branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DepthPeelingForward, MeshSubmissionStrategy);
                    branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, MeshSubmissionStrategy);
                });
                RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.OnTop, branch =>
                {
                    branch.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                    branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, MeshSubmissionStrategy);
                    branch.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                });
                RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.DebugOverlays, branch =>
                {
                    branch.Add<VPRC_RenderMeshletDebugDisplay>();
                    branch.Add<VPRC_BuildAccelerationStructure>();
                    branch.Add<VPRC_RenderDebugGpuBvh>();
                    branch.Add<VPRC_RenderDebugShapes>();
                    if (enableComputePasses)
                        branch.Add<VPRC_ForwardPlusDebugOverlay>();
                });
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_ColorMask>().Set(true, true, true, true);
            }
        }

        RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.PostRender, branch =>
            branch.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false));
        RenderCaptureCommandPolicy.AddConditional(c, this, ERenderCapturePass.ScreenSpaceUi, branch =>
            branch.Add<VPRC_RenderScreenSpaceUI>());
        return c;
    }

    private ViewportRenderCommandContainer CreateViewportTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);
        bool enableComputePasses = EnableComputeDependentPasses;
        bool bypassVendorUpscale = RenderDiagnosticsFlags.BypassVendorUpscale;

        AppendTemporalBegin(c);

        AppendVoxelConeTracingPass(c, enableComputePasses);

        c.Add<VPRC_ColorMask>().Set(true, true, true, true);
        c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
        c.Add<VPRC_DepthWrite>().Allow = true;
        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        var sceneWorkload = c.Add<VPRC_IfElse>();
        sceneWorkload.Label = "FullSceneMeshWorkload";
        sceneWorkload.ConditionEvaluator = ShouldRunFullScenePipeline;

        var fullSceneCommands = new ViewportRenderCommandContainer(this);
        using (fullSceneCommands.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            AppendAmbientOcclusionSwitch(fullSceneCommands, enableComputePasses);
            AppendDeferredGBufferPass(fullSceneCommands);
            AppendForwardDepthPrePass(fullSceneCommands);
            fullSceneCommands.Add<VPRC_DepthTest>().Enable = false;
            AppendAmbientOcclusionResolve(fullSceneCommands);
            AppendForwardDepthPrePassGBufferRestore(fullSceneCommands);
            AppendLightingPass(fullSceneCommands);
            AppendForwardPass(fullSceneCommands, enableComputePasses);
            AppendTransparencyPasses(fullSceneCommands);

            fullSceneCommands.Add<VPRC_DepthTest>().Enable = false;
            AppendVelocityPassSwitch(fullSceneCommands);
            fullSceneCommands.Add<VPRC_DepthTest>().Enable = false;
            AppendBloomPass(fullSceneCommands);
            AppendMotionBlurAndDoF(fullSceneCommands);
            AppendTemporalAccumulation(fullSceneCommands);
            // Build the GPU BVH so debug overlays (and any zero-readback consumers)
            // have an up-to-date acceleration structure published into pipeline
            // variables before the on-top forward passes run.
            fullSceneCommands.Add<VPRC_BuildAccelerationStructure>();
            AppendPostTemporalForwardPasses(fullSceneCommands);
            // Fog must composite after the late forward batches; its passes upload
            // the temporal pass' stored current projection so depth reconstruction
            // still matches the jittered depth buffer after PopJitter.
            if (!Stereo)
            {
                AppendPostProcessCompositeInputDefaults(fullSceneCommands);
                AppendAtmosphericScattering(fullSceneCommands);
                AppendVolumetricFog(fullSceneCommands);
            }
            fullSceneCommands.Add<VPRC_DepthTest>().Enable = false;
            AppendFullOverdrawCountingPass(fullSceneCommands);
        }
        sceneWorkload.TrueCommands = fullSceneCommands;
        sceneWorkload.FalseCommands = CreateCallbackOnlySceneCommands();

        AppendExposureUpdate(c);
        AppendLateDebugOverlay(c);
        AppendFinalPostProcess(c);
        AppendFxaaTsrUpscaleChain(c);
        AppendTemporalCommit(c);
        AppendFinalOutput(c, bypassVendorUpscale);

        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        c.Add<VPRC_RenderScreenSpaceUI>();
        return c;
    }

    private ViewportRenderCommandContainer CreateCallbackOnlySceneCommands()
    {
        ViewportRenderCommandContainer c = new(this);
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
            using (c.AddUsing<VPRC_BindFBOByName>(x =>
                x.SetOptions(ForwardPassFBOName, write: true, clearColor: true, clearDepth: true, clearStencil: true)))
            {
                c.Add<VPRC_ColorMask>().Set(true, true, true, true);
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = true;
                // This path is selected only when no mesh commands exist. CPU-direct execution
                // preserves callback side effects without entering empty indirect dispatches.
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, false);
            }

            c.Add<VPRC_DepthTest>().Enable = false;
            AppendTemporalAccumulation(c);
            c.Add<VPRC_BuildAccelerationStructure>();

            using (c.AddUsing<VPRC_BindFBOByName>(x =>
                x.SetOptions(ForwardPassFBOName, write: true, clearColor: false, clearDepth: false, clearStencil: false)))
            {
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, false);
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                c.Add<VPRC_DepthWrite>().Allow = true;
            }

            c.Add<VPRC_DepthTest>().Enable = false;
        }

        return c;
    }

    private static bool HasRenderPassCommands(int renderPass)
        => CurrentRenderingPipeline?.ActiveMeshRenderCommands.HasRenderingCommands(renderPass) == true;

    private static bool HasRenderPassMeshCommands(int renderPass)
        => CurrentRenderingPipeline?.ActiveMeshRenderCommands.HasRenderingMeshCommands(renderPass) == true;

    private bool ShouldRunForwardDepthPrePass()
        => !UseOpenXrVulkanDesktopStartupSafePath
        && ForwardDepthPrePassEnabled
        && (HasRenderPassMeshCommands((int)EDefaultRenderPass.OpaqueForward)
            || HasRenderPassMeshCommands((int)EDefaultRenderPass.MaskedForward));

    private bool ShouldRunTransparencyPasses()
        => ShouldRunWeightedBlendedOitPasses()
        || ShouldRunExactTransparencyPasses();

    private static bool ShouldUseTemporalAccumulationResources()
        => RuntimeNeedsTemporalAaResources;

    private static bool ShouldUseFullSizePostProcessCompositeInputDefaults()
        => !UseOpenXrVulkanDesktopStartupSafePath;

    private bool ShouldRunWeightedBlendedOitPasses()
        => EnableWeightedBlendedOitPasses
        && (HasRenderPassCommands((int)EDefaultRenderPass.WeightedBlendedOitForward)
            || EnableTransparencyAccumulationVisualization
            || EnableTransparencyRevealageVisualization
            || EnableTransparencyOverdrawVisualization);

    private bool ShouldRunExactTransparencyPasses()
        => ExactTransparencyEnabled
        && (HasRenderPassCommands((int)EDefaultRenderPass.PerPixelLinkedListForward)
            || HasRenderPassCommands((int)EDefaultRenderPass.DepthPeelingForward)
            || EnableDepthPeelingLayerVisualization);

    private static bool ShouldRunAtmosphericScattering()
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
            return false;

        var state = RenderingPipelineState?.SceneCamera?.GetActivePostProcessState();
        var settings = GetSettings<AtmosphericScatteringSettings>(state) ?? AtmosphericScatteringSettings.Default;
        bool wantsAerialPerspective = settings.AerialPerspective ||
            settings.DebugMode != AtmosphericScatteringSettings.EDebugMode.Off;

        return settings.Enabled
            && wantsAerialPerspective
            && settings.MaxDistance > 0.0f
            && settings.SelectActiveAtmosphere(out var active)
            && active is { HasAerialPerspective: true };
    }

    private static bool ShouldRunVolumetricFog()
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
            return false;

        var state = RenderingPipelineState?.SceneCamera?.GetActivePostProcessState();
        var settings = GetSettings<VolumetricFogSettings>(state);
        var world = RuntimeEngine.Rendering.State.RenderingWorld;

        return settings is { Enabled: true }
            && settings.Intensity > 0.0f
            && settings.MaxDistance > 0.0f
            && world is not null
            && VolumetricFogVolumeComponent.Registry.HasActive(world);
    }

    private void AppendVoxelConeTracingPass(ViewportRenderCommandContainer c, bool enableComputePasses)
    {
        if (enableComputePasses)
        {
            c.Add<VPRC_VoxelConeTracingPass>().SetOptions(
                VoxelConeTracingVolumeTextureName,
                [
                    (int)EDefaultRenderPass.OpaqueDeferred,
                    (int)EDefaultRenderPass.OpaqueForward,
                    (int)EDefaultRenderPass.MaskedForward
                ],
                GPURenderDispatch,
                true);
        }
    }

    private void AppendAmbientOcclusionSwitch(ViewportRenderCommandContainer c, bool enableComputePasses)
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
        {
            ConfigureAmbientOcclusionDisabledPass(c.Add<VPRC_AODisabledPass>());
            return;
        }

        var aoSwitch = c.Add<VPRC_Switch>();
        aoSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
        aoSwitch.Cases = enableComputePasses
            ? new()
            {
                [(int)AmbientOcclusionSettings.EType.ScreenSpace] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusPassCommands(),
                [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion] = CreateVXAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion] = CreateMVAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance] = CreateMSVOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion] = CreateSpatialHashAOPassCommands(),
            }
            : new()
            {
                [(int)AmbientOcclusionSettings.EType.ScreenSpace] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusPassCommands(),
                [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion] = CreateVXAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion] = CreateSSAOPassCommands(),
            };
        aoSwitch.DefaultCase = CreateAmbientOcclusionDisabledPassCommands();
    }

    private void AppendDeferredGBufferPass(ViewportRenderCommandContainer c)
    {
        using (c.AddUsing<VPRC_BindFBOByName>(x =>
        {
            x.FrameBufferName = DeferredGBufferFBOName;
            x.Write = true;
            x.ClearColor = true;
            x.ClearDepth = true;
            x.ClearStencil = true;
            x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : DeferredGBufferFBOName;
        }))
        {
            c.Add<VPRC_StencilMask>().Set(~0u);
            c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, MeshSubmissionStrategy);
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DeferredDecals, MeshSubmissionStrategy);
        }

        if (!UseOpenXrVulkanDesktopStartupSafePath)
        {
            var msaaGBufferBranch = c.Add<VPRC_IfElse>();
            msaaGBufferBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
            {
                var msaaGeomCmds = new ViewportRenderCommandContainer(this);
                msaaGeomCmds.Add<VPRC_ResolveMsaaGBuffer>().SetOptions(
                    MsaaGBufferFBOName,
                    DeferredGBufferFBOName,
                    colorAttachmentCount: 4,
                    resolveDepthStencil: true);
                msaaGBufferBranch.TrueCommands = msaaGeomCmds;
            }
        }

        AppendDiagnosticTextureCapture(c, "01_AlbedoOpacity", AlbedoOpacityTextureName);
        AppendDiagnosticTextureCapture(c, "02_Normal", NormalTextureName);
        AppendDiagnosticTextureCapture(c, "03_RMSE", RMSETextureName);
    }

    private void AppendForwardDepthPrePass(ViewportRenderCommandContainer c)
    {
        var prePassChoice = c.Add<VPRC_IfElse>();
        prePassChoice.Label = "ForwardDepthPrePassActive";
        prePassChoice.ConditionEvaluator = ShouldRunForwardDepthPrePass;
        {
            var shareChoice = new ViewportRenderCommandContainer(this);
            shareChoice.Add<VPRC_BlitFrameBuffer>().SetOptions(
                ForwardDepthPrePassMergeFBOName,
                DeferredGBufferPreForwardCopyFBOName,
                EReadBufferMode.ColorAttachment0,
                blitColor: true,
                blitDepth: true,
                blitStencil: false,
                linearFilter: false);

            var shareIfElse = shareChoice.Add<VPRC_IfElse>();
            shareIfElse.ConditionEvaluator = () => ForwardPrePassSharesGBufferTargets;
            shareIfElse.TrueCommands = CreateForwardPrePassSharedCommands();
            shareIfElse.FalseCommands = CreateForwardPrePassSeparateCommands();
            shareChoice.Add<VPRC_BlitFrameBuffer>().SetOptions(
                ForwardDepthPrePassMergeFBOName,
                ForwardContactPrePassCopyFBOName,
                EReadBufferMode.ColorAttachment0,
                blitColor: true,
                blitDepth: true,
                blitStencil: false,
                linearFilter: false);
            prePassChoice.TrueCommands = shareChoice;
        }
    }

    private void AppendForwardDepthPrePassGBufferRestore(ViewportRenderCommandContainer c)
    {
        var restoreChoice = c.Add<VPRC_IfElse>();
        restoreChoice.Label = "ForwardDepthPrePassRestoreActive";
        restoreChoice.ConditionEvaluator = ShouldRunForwardDepthPrePass;
        {
            var restoreCommands = new ViewportRenderCommandContainer(this);
            restoreCommands.Add<VPRC_BlitFrameBuffer>().SetOptions(
                DeferredGBufferPreForwardCopyFBOName,
                ForwardDepthPrePassMergeFBOName,
                EReadBufferMode.ColorAttachment0,
                blitColor: true,
                blitDepth: true,
                blitStencil: false,
                linearFilter: false);
            restoreChoice.TrueCommands = restoreCommands;
        }
    }

    private void AppendAmbientOcclusionResolve(ViewportRenderCommandContainer c)
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName)
                .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantDisabled)
                .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionGenerate(
                    AmbientOcclusionIntensityTextureName,
                    disabled: true));
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(AmbientOcclusionBlurFBOName, GBufferFBOName)
                .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantDisabled)
                .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionFinal(
                    AmbientOcclusionIntensityTextureName,
                    DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantDisabled,
                    disabled: true));
            AppendDiagnosticTextureCapture(c, "04_AmbientOcclusion", AmbientOcclusionIntensityTextureName);
            return;
        }

        var aoResolveSwitch = c.Add<VPRC_Switch>();
        aoResolveSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
        aoResolveSwitch.Cases = new()
        {
            [AmbientOcclusionDisabledMode] = CreateAmbientOcclusionDisabledResolveCommands(),
            [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusResolveCommands(),
            [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOResolveCommands(),
            [(int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion] = CreateSpatialHashAOResolveCommands(),
        };
        aoResolveSwitch.DefaultCase = CreateAmbientOcclusionResolveCommands();

        AppendDiagnosticTextureCapture(c, "04_AmbientOcclusion", AmbientOcclusionIntensityTextureName);
    }

    private void AppendLightingPass(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_SyncLightProbeResources>();

        c.Add<VPRC_SetClears>().Set(ColorF4.Black, null, null);

        if (!UseOpenXrVulkanDesktopStartupSafePath)
        {
            var msaaMarkBranch = c.Add<VPRC_IfElse>();
            msaaMarkBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
            {
                var markCmds = new ViewportRenderCommandContainer(this);
                using (markCmds.AddUsing<VPRC_BindFBOByName>(x =>
                    x.SetOptions(MsaaLightingFBOName, write: true, clearColor: true, clearDepth: false, clearStencil: true)))
                {
                    markCmds.Add<VPRC_StencilMask>().Set(~0u);
                    markCmds.Add<VPRC_MarkComplexMsaaPixels>().SetOptions(
                        MsaaNormalTextureName,
                        MsaaDepthViewTextureName);
                }
                msaaMarkBranch.TrueCommands = markCmds;
            }

            var msaaLightingBranch = c.Add<VPRC_IfElse>();
            msaaLightingBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
            {
                var msaaLightCmds = new ViewportRenderCommandContainer(this);
                using (msaaLightCmds.AddUsing<VPRC_BindFBOByName>(x =>
                    x.SetOptions(MsaaLightingFBOName, write: true, clearColor: false, clearDepth: false, clearStencil: false)))
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbePositionBufferName;
                    x.BindingLocation = DeferredLightProbePositionBufferBinding;
                }))
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeTetraBufferName;
                    x.BindingLocation = DeferredLightProbeTetraBufferBinding;
                }))
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeParamBufferName;
                    x.BindingLocation = DeferredLightProbeParamBufferBinding;
                }))
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeGridCellBufferName;
                    x.BindingLocation = DeferredLightProbeGridCellBufferBinding;
                }))
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeGridIndexBufferName;
                    x.BindingLocation = DeferredLightProbeGridIndexBufferBinding;
                }))
                using (msaaLightCmds.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyLightCombineProgramBindings))
                {
                    msaaLightCmds.Add<VPRC_StencilMask>().Set(~0u);
                    var msaaLightPass = msaaLightCmds.Add<VPRC_LightCombinePass>();
                    msaaLightPass.SetOptions(
                        AlbedoOpacityTextureName,
                        NormalTextureName,
                        RMSETextureName,
                        DepthViewTextureName);
                    msaaLightPass.MsaaDeferred = true;
                }

                msaaLightCmds.Add<VPRC_BlitFrameBuffer>().SetOptions(
                    MsaaLightingFBOName,
                    LightingAccumFBOName,
                    EReadBufferMode.ColorAttachment0,
                    blitColor: true,
                    blitDepth: false,
                    blitStencil: false,
                    linearFilter: false);
                msaaLightingBranch.TrueCommands = msaaLightCmds;
            }
            {
                var stdLightCmds = new ViewportRenderCommandContainer(this);
                AppendStandardLightingCommands(stdLightCmds);
                msaaLightingBranch.FalseCommands = stdLightCmds;
            }
        }
        else
        {
            AppendStandardLightingCommands(c);
        }

        AppendDiagnosticTextureCapture(c, "05_LightingAccum", LightingAccumTextureName);
        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, null, null);
    }

    private void AppendStandardLightingCommands(ViewportRenderCommandContainer c)
    {
        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(LightingAccumFBOName, clearDepth: false, clearStencil: false)))
        using (c.AddUsing<VPRC_BindBuffer>(x =>
        {
            x.BufferName = LightProbePositionBufferName;
            x.BindingLocation = DeferredLightProbePositionBufferBinding;
        }))
        using (c.AddUsing<VPRC_BindBuffer>(x =>
        {
            x.BufferName = LightProbeTetraBufferName;
            x.BindingLocation = DeferredLightProbeTetraBufferBinding;
        }))
        using (c.AddUsing<VPRC_BindBuffer>(x =>
        {
            x.BufferName = LightProbeParamBufferName;
            x.BindingLocation = DeferredLightProbeParamBufferBinding;
        }))
        using (c.AddUsing<VPRC_BindBuffer>(x =>
        {
            x.BufferName = LightProbeGridCellBufferName;
            x.BindingLocation = DeferredLightProbeGridCellBufferBinding;
        }))
        using (c.AddUsing<VPRC_BindBuffer>(x =>
        {
            x.BufferName = LightProbeGridIndexBufferName;
            x.BindingLocation = DeferredLightProbeGridIndexBufferBinding;
        }))
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyLightCombineProgramBindings))
        {
            c.Add<VPRC_StencilMask>().Set(~0u);
            c.Add<VPRC_LightCombinePass>().SetOptions(
                AlbedoOpacityTextureName,
                NormalTextureName,
                RMSETextureName,
                DepthViewTextureName);
        }
    }

    private void AppendForwardPass(ViewportRenderCommandContainer c, bool enableComputePasses)
    {
        bool includeMsaaCommandGraph = !UseOpenXrVulkanDesktopStartupSafePath;

        using (c.AddUsing<VPRC_BindFBOByName>(x =>
        {
            x.FrameBufferName = ForwardPassFBOName;
            x.Write = true;
            x.DynamicName = () => RuntimeEnableMsaaTargets ? ForwardPassMsaaFBOName : ForwardPassFBOName;
            x.ClearColor = true;
            x.ClearDepth = false;
            x.ClearStencil = false;
            x.DynamicClearDepth = () => RuntimeEnableMsaaTargets;
        }))
        {
            if (includeMsaaCommandGraph)
            {
                var msaaPreload = c.Add<VPRC_IfElse>();
                msaaPreload.ConditionEvaluator = () => RuntimeEnableMsaaTargets;
                {
                    var preloadCmds = new ViewportRenderCommandContainer(this);
                    var deferredChoice = preloadCmds.Add<VPRC_IfElse>();
                    deferredChoice.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
                    {
                        var blitCmds = new ViewportRenderCommandContainer(this);
                        blitCmds.Add<VPRC_BlitFrameBuffer>().SetOptions(
                            MsaaGBufferFBOName,
                            ForwardPassMsaaFBOName,
                            EReadBufferMode.ColorAttachment0,
                            blitColor: false,
                            blitDepth: true,
                            blitStencil: false,
                            linearFilter: false);
                        deferredChoice.TrueCommands = blitCmds;
                    }
                    {
                        var shaderCmds = new ViewportRenderCommandContainer(this);
                        shaderCmds.Add<VPRC_RenderQuadToFBO>().SetTargets(DepthPreloadFBOName, ForwardPassMsaaFBOName);
                        deferredChoice.FalseCommands = shaderCmds;
                    }

                    msaaPreload.TrueCommands = preloadCmds;
                }
            }

            c.Add<VPRC_DepthTest>().Enable = false;
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbePositionBufferName;
                x.BindingLocation = DeferredLightProbePositionBufferBinding;
            }))
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbeTetraBufferName;
                x.BindingLocation = DeferredLightProbeTetraBufferBinding;
            }))
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbeParamBufferName;
                x.BindingLocation = DeferredLightProbeParamBufferBinding;
            }))
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbeGridCellBufferName;
                x.BindingLocation = DeferredLightProbeGridCellBufferBinding;
            }))
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbeGridIndexBufferName;
                x.BindingLocation = DeferredLightProbeGridIndexBufferBinding;
            }))
            using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyLightCombineProgramBindings))
                c.Add<VPRC_RenderQuadToFBO>()
                    .SetTargets(LightCombineFBOName, ForwardPassFBOName)
                    .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.DeferredLightCombine());
            AppendDiagnosticTextureCapture(c, "05b_LightCombine", DiffuseTextureName);

            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, MeshSubmissionStrategy);

            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            if (enableComputePasses)
                c.Add<VPRC_ForwardPlusLightCullingPass>();
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, MeshSubmissionStrategy);
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, MeshSubmissionStrategy);

            if (enableComputePasses)
            {
                c.Add<VPRC_ReSTIRPass>();
                c.Add<VPRC_LightVolumesPass>();
                c.Add<VPRC_RadianceCascadesPass>();
                c.Add<VPRC_SurfelGIPass>();
            }

            if (enableComputePasses)
                c.Add<VPRC_ForwardPlusDebugOverlay>();

        }

        if (includeMsaaCommandGraph)
        {
            var msaaResolve = c.Add<VPRC_IfElse>();
            msaaResolve.ConditionEvaluator = () => RuntimeEnableMsaaTargets;
            {
                var resolveCmds = new ViewportRenderCommandContainer(this);
                resolveCmds.Add<VPRC_ResolveMsaaGBuffer>().SetOptions(
                    ForwardPassMsaaFBOName,
                    ForwardPassFBOName,
                    colorAttachmentCount: 1,
                    resolveDepthStencil: true,
                    depthViewTextureName: ForwardPassMsaaDepthViewTextureName);
                msaaResolve.TrueCommands = resolveCmds;
            }
        }

        AppendDiagnosticTextureCapture(c, "06_ForwardPass", HDRSceneTextureName);
    }

    private void AppendTransparencyPasses(ViewportRenderCommandContainer c)
    {
        var transparencyChoice = c.Add<VPRC_IfElse>();
        transparencyChoice.Label = "TransparencyActive";
        transparencyChoice.ConditionEvaluator = ShouldRunTransparencyPasses;
        {
            var transparencyCommands = new ViewportRenderCommandContainer(this);
            transparencyCommands.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
            transparencyCommands.Add<VPRC_RenderQuadToFBO>().SetOptions(DeferredTransparencyBlurFBOName, renderToSourceFrameBuffer: true);
            transparencyCommands.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
            transparencyCommands.Add<VPRC_ClearTextureByName>().SetOptions(TransparentAccumTextureName, ColorF4.Transparent);
            transparencyCommands.Add<VPRC_ClearTextureByName>().SetOptions(TransparentRevealageTextureName, ColorF4.White);
            using (transparencyCommands.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(TransparentAccumulationFBOName, true, false, false, false)))
            {
                transparencyCommands.Add<VPRC_DepthTest>().Enable = true;
                transparencyCommands.Add<VPRC_DepthWrite>().Allow = false;
                transparencyCommands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, MeshSubmissionStrategy);
            }
            transparencyCommands.Add<VPRC_RenderQuadToFBO>().SetOptions(TransparentResolveFBOName, renderToSourceFrameBuffer: true);

            AppendExactTransparencyCommands(transparencyCommands);
            transparencyChoice.TrueCommands = transparencyCommands;
        }
    }

    private void AppendVelocityPassSwitch(ViewportRenderCommandContainer c)
    {
        var velocityChoice = c.Add<VPRC_IfElse>();
        velocityChoice.Label = "Velocity Buffer";
        velocityChoice.ConditionEvaluator = ShouldGenerateVelocityBufferForWorkload;

        ViewportRenderCommandContainer velocityCommands = new(this);
        AppendVelocityPass(velocityCommands);
        velocityChoice.TrueCommands = velocityCommands;
    }

    private void AppendVelocityPass(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_SetClears>().Set(ColorF4.Black, null, null);
        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(VelocityFBOName, true, true, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMotionVectorsPass>().SetOptions(false,
                new[]
                {
                    (int)EDefaultRenderPass.OpaqueDeferred,
                    (int)EDefaultRenderPass.DeferredDecals,
                    (int)EDefaultRenderPass.OpaqueForward,
                    (int)EDefaultRenderPass.MaskedForward,
                    (int)EDefaultRenderPass.WeightedBlendedOitForward,
                    (int)EDefaultRenderPass.PerPixelLinkedListForward,
                    (int)EDefaultRenderPass.DepthPeelingForward,
                });
            c.Add<VPRC_DepthWrite>().Allow = true;
        }
        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        AppendDiagnosticTextureCapture(c, "07_Velocity", VelocityTextureName);
        AppendDiagnosticFboCapture(c, "07b_VelocityFBO", VelocityFBOName);
    }

    private void AppendBloomPass(ViewportRenderCommandContainer c)
    {
        bool safePath = UseOpenXrVulkanDesktopStartupSafePath;
        if (RenderDiagnosticsFlags.DiagPostProcess)
        {
            Debug.Rendering(
                "[BloomDiag] AppendBloomPass bake: safePath={0} pipeline=0x{1:X8} stereo={2} vp='{3}' external={4}",
                safePath,
                GetHashCode(),
                Stereo,
                RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport?.Index.ToString() ?? "<null>",
                RuntimeEngine.Rendering.State.RenderingPipelineState?.WindowViewport?.RendersToExternalSwapchainTarget.ToString() ?? "<null>");
        }
        if (safePath)
            return;

        var bloomChoice = c.Add<VPRC_IfElse>();
        bloomChoice.ConditionEvaluator = ShouldUseBloom;
        {
            var bloomCommands = new ViewportRenderCommandContainer(this);
            VPRC_BloomPass bloomPass = bloomCommands.Add<VPRC_BloomPass>();
            bloomPass.SetTargetFBONames(
                ForwardPassFBOName,
                BloomBlurTextureName,
                Stereo);
            _bloomProvider = bloomPass;
            bloomChoice.TrueCommands = bloomCommands;
        }

        AppendDiagnosticTextureCapture(c, "08_BloomMip0", BloomBlurTextureName, mipLevel: 0);
        AppendDiagnosticTextureCapture(c, "09_BloomMip1", BloomBlurTextureName, mipLevel: 1);
        AppendDiagnosticTextureCapture(c, "09b_BloomMip2", BloomBlurTextureName, mipLevel: 2);
        AppendDiagnosticTextureCapture(c, "09c_BloomMip3", BloomBlurTextureName, mipLevel: 3);
        AppendDiagnosticTextureCapture(c, "10_BloomMip4", BloomBlurTextureName, mipLevel: 4);
    }

    private void AppendMotionBlurAndDoF(ViewportRenderCommandContainer c)
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
            return;

        var motionBlurChoice = c.Add<VPRC_IfElse>();
        motionBlurChoice.ConditionEvaluator = ShouldUseMotionBlur;
        motionBlurChoice.TrueCommands = CreateMotionBlurPassCommands();

        var dofChoice = c.Add<VPRC_IfElse>();
        dofChoice.ConditionEvaluator = ShouldUseDepthOfField;
        dofChoice.TrueCommands = CreateDepthOfFieldPassCommands();
    }

    private void AppendTemporalBegin(ViewportRenderCommandContainer c)
    {
        var temporalBegin = c.Add<VPRC_IfElse>();
        temporalBegin.Label = "TemporalBeginActive";
        temporalBegin.ConditionEvaluator = ShouldUseTemporalAccumulationResources;
        var temporalBeginCommands = new ViewportRenderCommandContainer(this);
        temporalBeginCommands.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Begin;
        temporalBegin.TrueCommands = temporalBeginCommands;
    }

    private void AppendTemporalAccumulation(ViewportRenderCommandContainer c)
    {
        var temporalChoice = c.Add<VPRC_IfElse>();
        temporalChoice.Label = "TemporalAccumulationActive";
        temporalChoice.ConditionEvaluator = ShouldUseTemporalAccumulationResources;

        var temporalCommands = new ViewportRenderCommandContainer(this);
        var temporalAccumulate = temporalCommands.Add<VPRC_TemporalAccumulationPass>();
        temporalAccumulate.Phase = VPRC_TemporalAccumulationPass.EPhase.Accumulate;
        temporalAccumulate.ConfigureAccumulationTargets(
            ForwardPassFBOName,
            TemporalInputFBOName,
            TemporalAccumulationFBOName,
            HistoryCaptureFBOName,
            HistoryExposureFBOName);

        temporalCommands.Add<VPRC_TemporalAccumulationPass>().Phase =
            VPRC_TemporalAccumulationPass.EPhase.PopJitter;

        AppendDiagnosticTextureCapture(temporalCommands, "11_TemporalColorInput", TemporalColorInputTextureName);
        AppendDiagnosticTextureCapture(temporalCommands, "11b_CurrentDepth", DepthViewTextureName);
        AppendDiagnosticTextureCapture(temporalCommands, "11c_HistoryDepth", HistoryDepthViewTextureName);
        temporalChoice.TrueCommands = temporalCommands;
    }

    private void AppendPostTemporalForwardPasses(ViewportRenderCommandContainer c)
    {
        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardPassFBOName, true, false, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, MeshSubmissionStrategy);
            c.Add<VPRC_RenderMeshletDebugDisplay>();
            c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, MeshSubmissionStrategy);
            c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
            c.Add<VPRC_DepthWrite>().Allow = true;
            c.Add<VPRC_ColorMask>().Set(true, true, true, true);
        }
    }

    private void AppendPostProcessCompositeInputDefaults(ViewportRenderCommandContainer c)
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
            return;

        var defaults = c.Add<VPRC_IfElse>();
        defaults.Label = "PostProcessCompositeInputDefaultsActive";
        defaults.ConditionEvaluator = ShouldUseFullSizePostProcessCompositeInputDefaults;
        var commands = new ViewportRenderCommandContainer(this);

        commands.Add<VPRC_SetClears>().Set(ColorF4.Transparent, null, null);
        using (commands.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(AtmosphereUpscaleFBOName, write: true, clearColor: true, clearDepth: false, clearStencil: false)))
        {
        }

        using (commands.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(VolumetricFogUpscaleFBOName, write: true, clearColor: true, clearDepth: false, clearStencil: false)))
        {
        }

        defaults.TrueCommands = commands;
    }

    private void AppendFullOverdrawCountingPass(ViewportRenderCommandContainer c)
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
            return;

        var fullOverdraw = c.Add<VPRC_IfElse>();
        fullOverdraw.ConditionEvaluator = () => EnableFullOverdrawVisualization;
        {
            var countCommands = new ViewportRenderCommandContainer(this);
            countCommands.Add<VPRC_SetClears>().Set(ColorF4.Black, null, null);
            using (countCommands.AddUsing<VPRC_BindFBOByName>(x =>
                x.SetOptions(FullOverdrawCountFBOName, write: true, clearColor: true, clearDepth: false, clearStencil: false)))
            {
                countCommands.Add<VPRC_ColorMask>().Set(true, true, true, true);
                countCommands.Add<VPRC_DepthTest>().Enable = false;
                countCommands.Add<VPRC_DepthWrite>().Allow = false;
                countCommands.Add<VPRC_RenderFullOverdrawPass>();
            }

            countCommands.Add<VPRC_DepthWrite>().Allow = true;
            countCommands.Add<VPRC_DepthTest>().Enable = false;
            countCommands.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
            fullOverdraw.TrueCommands = countCommands;
        }
    }

    private void AppendFxaaTsrUpscaleChain(ViewportRenderCommandContainer c)
    {
        var upscaleChoice = c.Add<VPRC_IfElse>();
        upscaleChoice.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableDeclaredSmaa || RuntimeNeedsTsrUpscale;
        {
            var upscaleCmds = new ViewportRenderCommandContainer(this);

            var tsrOrPostAa = upscaleCmds.Add<VPRC_IfElse>();
            tsrOrPostAa.ConditionEvaluator = () => RuntimeNeedsTsrUpscale;
            {
                var tsrUpscale = new ViewportRenderCommandContainer(this);
                AppendDiagnosticTextureCapture(tsrUpscale, "13b_PreTsrHistoryColor", TsrHistoryColorTextureName);
                if (Stereo && IsPhase524bValidationEnabled())
                {
                    using (tsrUpscale.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = TsrMonoReferenceFBO_SettingUniforms))
                        tsrUpscale.Add<VPRC_RenderQuadToFBO>()
                            .SetTargets(TsrMonoReferenceLeftFBOName, TsrMonoReferenceLeftFBOName, matchDestinationRenderArea: true)
                            .SetIsolatedMonoReference()
                            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.TsrUpscale());
                    using (tsrUpscale.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = TsrMonoReferenceFBO_SettingUniforms))
                        tsrUpscale.Add<VPRC_RenderQuadToFBO>()
                            .SetTargets(TsrMonoReferenceRightFBOName, TsrMonoReferenceRightFBOName, matchDestinationRenderArea: true)
                            .SetIsolatedMonoReference()
                            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.TsrUpscale());
                    AppendDiagnosticTextureCapture(tsrUpscale, "13c_MonoTsrReference", TsrMonoReferenceTextureName);
                }
                using (tsrUpscale.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = TsrUpscaleFBO_SettingUniforms))
                    tsrUpscale.Add<VPRC_RenderQuadToFBO>()
                        .SetTargets(TsrUpscaleFBOName, TsrUpscaleFBOName, matchDestinationRenderArea: true)
                        .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.TsrUpscale());
                tsrUpscale.Add<VPRC_BlitFrameBuffer>().SetOptions(
                    TsrUpscaleFBOName,
                    TsrHistoryColorFBOName,
                    EReadBufferMode.ColorAttachment0,
                    blitColor: true,
                    blitDepth: false,
                    blitStencil: false,
                    linearFilter: false);
                var markTsrHistory = tsrUpscale.Add<VPRC_TemporalAccumulationPass>();
                markTsrHistory.Phase = VPRC_TemporalAccumulationPass.EPhase.MarkTsrHistoryColor;
                markTsrHistory.ConfigureTsrHistoryTargets(TsrUpscaleFBOName, TsrHistoryColorFBOName);
                AppendDiagnosticTextureCapture(tsrUpscale, "14_TsrOutput", TsrOutputTextureName);
                AppendDiagnosticTextureCapture(tsrUpscale, "14b_TsrHistoryColor", TsrHistoryColorTextureName);
                AppendDiagnosticDesktopFinalCapture(tsrUpscale, "15_FinalOutput", TsrOutputTextureName);
                tsrOrPostAa.TrueCommands = tsrUpscale;
            }
            {
                var fxaaOrSmaa = new ViewportRenderCommandContainer(this);
                var postAaChoice = fxaaOrSmaa.Add<VPRC_IfElse>();
                postAaChoice.ConditionEvaluator = () => RuntimeEnableFxaa;
                {
                    var fxaaUpscale = new ViewportRenderCommandContainer(this);
                    var fxaa = fxaaUpscale.Add<VPRC_FXAA>();
                    fxaa.SourceFBOName = FinalPostProcessOutputFBOName;
                    fxaa.DestinationFBOName = FxaaFBOName;
                    fxaa.Stereo = Stereo;
                    postAaChoice.TrueCommands = fxaaUpscale;
                }
                {
                    var smaaUpscale = new ViewportRenderCommandContainer(this);
                    var smaa = smaaUpscale.Add<VPRC_SMAA>();
                    smaa.SourceFBOName = FinalPostProcessOutputFBOName;
                    smaa.OutputTextureName = SmaaOutputTextureName;
                    smaa.OutputFBOName = SmaaFBOName;
                    smaa.Stereo = Stereo;
                    postAaChoice.FalseCommands = smaaUpscale;
                }
                tsrOrPostAa.FalseCommands = fxaaOrSmaa;
            }

            upscaleChoice.TrueCommands = upscaleCmds;
        }
    }

    private void AppendExposureUpdate(ViewportRenderCommandContainer c)
    {
        string exposureSource = HDRSceneTextureName;
        c.Add<VPRC_ExposureUpdate>().SetOptions(exposureSource, true);

        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        using (c.AddUsing<VPRC_PushProgramBindings>(t => t.ApplyUniforms = PostProcessFBO_SettingUniforms))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(PostProcessFBOName, PostProcessOutputFBOName, matchDestinationRenderArea: true)
                .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.PostProcess());
        }
    }

    private void AppendLateDebugOverlay(ViewportRenderCommandContainer c)
    {
        const string lateDebugPassName = "LateDebugOverlay";
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(
                PostProcessOutputFBOName,
                write: true,
                clearColor: false,
                clearDepth: false,
                clearStencil: false)))
            {
                c.Add<VPRC_ColorMask>().Set(true, true, true, true);
                c.Add<VPRC_DepthTest>().Enable = false;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderDebugGpuBvh>().RenderGraphPassName = lateDebugPassName;
                c.Add<VPRC_RenderDebugShapes>().RenderGraphPassName = lateDebugPassName;
                c.Add<VPRC_RenderDebugPhysics>().RenderGraphPassName = lateDebugPassName;
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                c.Add<VPRC_DepthWrite>().Allow = true;
            }
        }

        AppendDiagnosticTextureCapture(c, "12_PostProcessOutput", PostProcessOutputTextureName);
    }

    private void AppendFinalPostProcess(ViewportRenderCommandContainer c)
    {
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = FinalPostProcessFBO_SettingUniforms))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(FinalPostProcessFBOName, FinalPostProcessOutputFBOName, matchDestinationRenderArea: true)
                .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.FinalPostProcess());
        }

        AppendDiagnosticTextureCapture(c, "13_FinalPostProcessOutput", FinalPostProcessOutputTextureName);
    }

    /// <summary>
    /// Caches and runs the four-stage atmospheric aerial-perspective chain:
    ///   1. Half-resolution depth downsample (<c>AtmosphereHalfDepth</c>).
    ///   2. Half-resolution aerial-perspective raymarch (<c>AtmosphereHalfScatter</c>).
    ///   3. Half-resolution temporal reprojection (<c>AtmosphereHalfTemporal</c>).
    ///   4. Full-resolution bilateral upscale (<c>AtmosphereColor</c>).
    /// The chain is gated off when the camera has no active aerial-perspective atmosphere.
    /// </summary>
    private void AppendAtmosphericScattering(ViewportRenderCommandContainer c)
    {
        var atmosphereChoice = c.Add<VPRC_IfElse>();
        atmosphereChoice.Label = "AtmosphereActive";
        atmosphereChoice.ConditionEvaluator = ShouldRunAtmosphericScattering;
        atmosphereChoice.TrueCommands = CreateAtmosphericScatteringCommands();
    }

    private ViewportRenderCommandContainer CreateAtmosphericScatteringCommands()
    {
        var c = new ViewportRenderCommandContainer(this);

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AtmosphereHalfDepthQuadFBOName, AtmosphereHalfDepthFBOName, matchDestinationRenderArea: true);

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AtmosphereHalfScatterQuadFBOName, AtmosphereHalfScatterFBOName, matchDestinationRenderArea: true);

        c.Add<VPRC_AtmosphereHistoryPass>().Phase = VPRC_AtmosphereHistoryPass.EPhase.Begin;
        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AtmosphereReprojectQuadFBOName, AtmosphereReprojectFBOName, matchDestinationRenderArea: true);

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AtmosphereUpscaleQuadFBOName, AtmosphereUpscaleFBOName, matchDestinationRenderArea: true);

        c.Add<VPRC_BlitFrameBuffer>().SetOptions(
            AtmosphereReprojectFBOName,
            AtmosphereHistoryFBOName,
            EReadBufferMode.ColorAttachment0,
            blitColor: true,
            blitDepth: false,
            blitStencil: false,
            linearFilter: false);

        c.Add<VPRC_AtmosphereHistoryPass>().Phase = VPRC_AtmosphereHistoryPass.EPhase.Commit;
        return c;
    }

    /// <summary>
    /// Caches and runs the four-stage volumetric fog chain:
    ///   1. Half-resolution depth downsample (<c>VolumetricFogHalfDepth</c>).
    ///   2. Half-resolution scatter raymarch (<c>VolumetricFogHalfScatter</c>).
    ///   3. Half-resolution temporal reprojection (<c>VolumetricFogHalfTemporal</c>).
    ///   4. Full-resolution bilateral upscale (<c>VolumetricFogColor</c>).
    /// The chain is gated off when there are no active fog volumes. Each pass uses
    /// <see cref="VPRC_RenderQuadToFBO.MatchDestinationRenderArea"/> so the
    /// viewport follows the destination FBO's size automatically.
    /// </summary>
    private void AppendVolumetricFog(ViewportRenderCommandContainer c)
    {
        var fogChoice = c.Add<VPRC_IfElse>();
        fogChoice.Label = "VolumetricFogActive";
        fogChoice.ConditionEvaluator = ShouldRunVolumetricFog;
        fogChoice.TrueCommands = CreateVolumetricFogCommands();
    }

    private ViewportRenderCommandContainer CreateVolumetricFogCommands()
    {
        var c = new ViewportRenderCommandContainer(this);

        // Stage 1: half-resolution depth downsample.

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(VolumetricFogHalfDepthQuadFBOName, VolumetricFogHalfDepthFBOName, matchDestinationRenderArea: true);

        // Stage 2: half-resolution scatter raymarch.

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(VolumetricFogHalfScatterQuadFBOName, VolumetricFogHalfScatterFBOName, matchDestinationRenderArea: true);

        // Stage 3: half-resolution temporal reprojection.

        c.Add<VPRC_VolumetricFogHistoryPass>().Phase = VPRC_VolumetricFogHistoryPass.EPhase.Begin;
        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(VolumetricFogReprojectQuadFBOName, VolumetricFogReprojectFBOName, matchDestinationRenderArea: true);

        // Stage 4: full-resolution bilateral upscale.

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(VolumetricFogUpscaleQuadFBOName, VolumetricFogUpscaleFBOName, matchDestinationRenderArea: true);

        c.Add<VPRC_BlitFrameBuffer>().SetOptions(
            VolumetricFogReprojectFBOName,
            VolumetricFogHistoryFBOName,
            EReadBufferMode.ColorAttachment0,
            blitColor: true,
            blitDepth: false,
            blitStencil: false,
            linearFilter: false);

        c.Add<VPRC_VolumetricFogHistoryPass>().Phase = VPRC_VolumetricFogHistoryPass.EPhase.Commit;
        return c;
    }

    private void AppendTemporalCommit(ViewportRenderCommandContainer c)
    {
        var temporalCommit = c.Add<VPRC_IfElse>();
        temporalCommit.Label = "TemporalCommitActive";
        temporalCommit.ConditionEvaluator = ShouldUseTemporalAccumulationResources;
        var commands = new ViewportRenderCommandContainer(this);
        commands.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Commit;
        temporalCommit.TrueCommands = commands;
    }

    private void AppendFinalOutput(ViewportRenderCommandContainer c, bool bypassVendorUpscale)
    {
        var outputChoice = c.Add<VPRC_IfElse>();
        outputChoice.ConditionEvaluator = IsOffscreenSceneCaptureOutput;
        outputChoice.TrueCommands = CreateOffscreenCaptureFinalOutputCommands();
        outputChoice.FalseCommands = CreateViewportFinalOutputCommands(bypassVendorUpscale);
    }

    private static bool IsOffscreenSceneCaptureOutput()
        => State.OutputFBO is not null &&
           (RuntimeEngine.Rendering.State.IsSceneCapturePass || RuntimeEngine.Rendering.State.IsLightProbePass);

    private ViewportRenderCommandContainer CreateOffscreenCaptureFinalOutputCommands()
    {
        ViewportRenderCommandContainer c = new(this);
        using (c.AddUsing<VPRC_PushOutputFBORenderArea>())
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                c.Add<VPRC_ColorMask>().Set(true, true, true, true);
                c.Add<VPRC_DepthTest>().Enable = false;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderQuadToFBO>()
                    .SetOptions(FinalPostProcessFBOName)
                    .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.FinalPostProcessToOutputTarget());
                AppendDebugOverlay(c);
            }
        }
        return c;
    }

    private ViewportRenderCommandContainer CreateViewportFinalOutputCommands(bool bypassVendorUpscale)
    {
        ViewportRenderCommandContainer c = new(this);
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                c.Add<VPRC_ColorMask>().Set(true, true, true, true);
                c.Add<VPRC_DepthTest>().Enable = false;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                AppendFullOverdrawOrStandardFinalOutput(c, bypassVendorUpscale);
            }
        }
        return c;
    }

    private void AppendFullOverdrawOrStandardFinalOutput(ViewportRenderCommandContainer c, bool bypassVendorUpscale)
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
        {
            AppendViewportFinalOutputSourceCommands(c, bypassVendorUpscale);
            return;
        }

        var fullOverdraw = c.Add<VPRC_IfElse>();
        fullOverdraw.ConditionEvaluator = () => EnableFullOverdrawVisualization;
        fullOverdraw.TrueCommands = CreateFullOverdrawFinalOutputCommands();
        fullOverdraw.FalseCommands = CreateLegacyDebugOrStandardFinalOutputCommands(bypassVendorUpscale);
    }

    private ViewportRenderCommandContainer CreateFullOverdrawFinalOutputCommands()
    {
        var commands = new ViewportRenderCommandContainer(this);
        commands.Add<VPRC_RenderQuadToFBO>().SetTargets(FullOverdrawDebugFBOName, null);
        return commands;
    }

    private ViewportRenderCommandContainer CreateLegacyDebugOrStandardFinalOutputCommands(bool bypassVendorUpscale)
    {
        var commands = new ViewportRenderCommandContainer(this);
        if (EnableTransformIdVisualization)
        {
            commands.Add<VPRC_RenderQuadToFBO>().SetTargets(TransformIdDebugQuadFBOName, null);
        }
        else if (ActiveTransparencyDebugFboName is not null)
        {
            commands.Add<VPRC_RenderQuadToFBO>().SetTargets(ActiveTransparencyDebugFboName, null);
        }
        else
        {
            AppendViewportFinalOutputSourceCommands(commands, bypassVendorUpscale);
        }

        return commands;
    }

    private void AppendDebugOverlay(ViewportRenderCommandContainer c, bool visible = true)
    {
        c.Add<VPRC_ColorMask>().Set(visible, visible, visible, visible);
        c.Add<VPRC_RenderDebugShapes>();
        c.Add<VPRC_RenderDebugPhysics>();
        if (!visible)
            c.Add<VPRC_ColorMask>().Set(true, true, true, true);
    }

    private void AppendViewportFinalOutputSourceCommands(ViewportRenderCommandContainer c, bool bypassVendorUpscale)
    {
        string? overrideSource = ResolveOutputSourceFboOverride();
        if (overrideSource is not null)
        {
            var overrideChoice = c.Add<VPRC_IfElse>();
            overrideChoice.ConditionEvaluator = () => IsValidFinalOutputSourceFboOverride(overrideSource);
            overrideChoice.TrueCommands = CreateOutputSourceOverrideCommands(overrideSource, bypassVendorUpscale);
            overrideChoice.FalseCommands = CreateStandardViewportFinalOutputCommands(bypassVendorUpscale);
            return;
        }

        AppendStandardViewportFinalOutputCommands(c, bypassVendorUpscale);
    }

    private ViewportRenderCommandContainer CreateStandardViewportFinalOutputCommands(bool bypassVendorUpscale)
    {
        var commands = new ViewportRenderCommandContainer(this);
        AppendStandardViewportFinalOutputCommands(commands, bypassVendorUpscale);
        return commands;
    }

    private void AppendStandardViewportFinalOutputCommands(ViewportRenderCommandContainer c, bool bypassVendorUpscale)
    {
        var upscaleOutputChoice = c.Add<VPRC_IfElse>();
        upscaleOutputChoice.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableDeclaredSmaa || RuntimeNeedsTsrUpscale;
        {
            var upscaleOutput = new ViewportRenderCommandContainer(this);
            var tsrOrPostAaFinal = upscaleOutput.Add<VPRC_IfElse>();
            tsrOrPostAaFinal.ConditionEvaluator = () => RuntimeNeedsTsrUpscale;
            tsrOrPostAaFinal.TrueCommands = CreateFinalBlitCommands(TsrUpscaleFBOName, bypassVendorUpscale);
            {
                var postAaOutput = new ViewportRenderCommandContainer(this);
                var fxaaOrSmaaFinal = postAaOutput.Add<VPRC_IfElse>();
                fxaaOrSmaaFinal.ConditionEvaluator = () => RuntimeEnableFxaa;
                fxaaOrSmaaFinal.TrueCommands = CreateFinalBlitCommands(FxaaFBOName, bypassVendorUpscale);
                fxaaOrSmaaFinal.FalseCommands = CreateFinalBlitCommands(SmaaFBOName, bypassVendorUpscale);
                tsrOrPostAaFinal.FalseCommands = postAaOutput;
            }
            upscaleOutputChoice.TrueCommands = upscaleOutput;
        }
        upscaleOutputChoice.FalseCommands = CreateFinalBlitCommands(ResolveStandardFinalOutputFboName(), bypassVendorUpscale);
    }

    private static string ResolveStandardFinalOutputFboName()
        => FinalPostProcessOutputFBOName;

    private ViewportRenderCommandContainer CreateOutputSourceOverrideCommands(string sourceFboName, bool bypassVendorUpscale)
        => CreateFinalBlitCommands(sourceFboName, bypassVendorUpscale);

    private static string? ResolveOutputSourceFboOverride()
        => RenderDiagnosticsFlags.OutputSourceFboOverride;

    private void AppendDiagnosticTextureCapture(
        ViewportRenderCommandContainer c,
        string label,
        string textureName,
        int mipLevel = 0)
    {
        if (!Stereo || !ShouldCaptureDefaultPipelineFbos())
            return;

        int layerCount = DefaultPipelineDiagnosticCapture.ResolveLayerCount(stereo: true);
        for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
        {
            var capture = c.Add<VPRC_CaptureFrame>();
            capture.SourceTextureName = textureName;
            capture.SourceMipLevel = mipLevel;
            capture.SourceLayerIndex = layerIndex;
            capture.MaxCaptures = 1;
            capture.SkipFramesBeforeCapture = ResolveDefaultPipelineCaptureSkipFrames();
            capture.OutputFilePath = DefaultPipelineDiagnosticCapture.ResolveOutputPath("DefaultPipelineSps", label, layerIndex);
            capture.FlipVertically = false;
            capture.RequireStableImportedTextureStreaming = IsPhase524bValidationEnabled();
            capture.RequiredPhase524bBoundaryMotionIndex = IsPhase524bValidationEnabled() ? 0 : -1;
            ConfigurePhase524bTemporalScenarioCapture(capture, label, layerIndex, layerCount);
        }
    }

    private void AppendDiagnosticFboCapture(
        ViewportRenderCommandContainer c,
        string label,
        string fboName)
    {
        if (!Stereo || !ShouldCaptureDefaultPipelineFbos())
            return;

        int layerCount = DefaultPipelineDiagnosticCapture.ResolveLayerCount(stereo: true);
        for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
        {
            var capture = c.Add<VPRC_CaptureFrame>();
            capture.SourceFBOName = fboName;
            capture.SourceLayerIndex = layerIndex;
            capture.MaxCaptures = 1;
            capture.SkipFramesBeforeCapture = ResolveDefaultPipelineCaptureSkipFrames();
            capture.OutputFilePath = DefaultPipelineDiagnosticCapture.ResolveOutputPath("DefaultPipelineSps", label, layerIndex);
            capture.FlipVertically = false;
            capture.RequireStableImportedTextureStreaming = IsPhase524bValidationEnabled();
            capture.RequiredPhase524bBoundaryMotionIndex = IsPhase524bValidationEnabled() ? 0 : -1;
        }
    }

    private void AppendDiagnosticDesktopFinalCapture(
        ViewportRenderCommandContainer c,
        string label,
        string textureName)
    {
        if (Stereo || !ShouldCaptureDefaultPipelineFbos())
            return;

        var capture = c.Add<VPRC_CaptureFrame>();
        capture.SourceTextureName = textureName;
        capture.SourceLayerIndex = 0;
        capture.MaxCaptures = 1;
        capture.SkipFramesBeforeCapture = ResolveDefaultPipelineCaptureSkipFrames();
        capture.OutputFilePath = DefaultPipelineDiagnosticCapture.ResolveOutputPath(
            "DefaultPipelineDesktop",
            label,
            layerIndex: 0);
        capture.FlipVertically = false;
        capture.RequireStableImportedTextureStreaming = IsPhase524bValidationEnabled();
        capture.RequiredPhase524bBoundaryMotionIndex = IsPhase524bValidationEnabled() ? 0 : -1;

        const int motionCaptureCount = 3;
        const int motionCaptureIntervalFrames = 3;
        for (int motionIndex = 1; motionIndex < motionCaptureCount; motionIndex++)
        {
            var motionCapture = c.Add<VPRC_CaptureFrame>();
            motionCapture.SourceTextureName = textureName;
            motionCapture.SourceLayerIndex = 0;
            motionCapture.MaxCaptures = 1;
            motionCapture.SkipFramesBeforeCapture = ResolveDefaultPipelineCaptureSkipFrames() +
                (motionIndex * motionCaptureIntervalFrames);
            motionCapture.OutputFilePath = DefaultPipelineDiagnosticCapture.ResolveOutputPath(
                "DefaultPipelineDesktop",
                $"{label}_motion{motionIndex}",
                layerIndex: 0);
            motionCapture.FlipVertically = false;
            motionCapture.RequireStableImportedTextureStreaming = IsPhase524bValidationEnabled();
            motionCapture.RequiredPhase524bBoundaryMotionIndex = IsPhase524bValidationEnabled()
                ? motionIndex
                : -1;
        }
    }

    private static int ResolveDefaultPipelineCaptureSkipFrames()
    {
        string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CaptureDefaultPipelineSkipFrames);
        return int.TryParse(raw, out int skipFrames) ? Math.Max(0, skipFrames) : 120;
    }

    private static void ConfigurePhase524bTemporalScenarioCapture(
        VPRC_CaptureFrame capture,
        string label,
        int layerIndex,
        int layerCount)
    {
        if (!IsPhase524bTemporalScenarioStage(label) || !IsPhase524bValidationEnabled())
            return;

        capture.CapturePhase524bTemporalScenarios = true;
        capture.TemporalScenarioPipelineName = "DefaultPipelineSps";
        capture.TemporalScenarioStage = label;
        capture.CompletesPhase524bTemporalScenarioFrame =
            label == "14_TsrOutput" && layerIndex == layerCount - 1;
    }

    private static bool IsPhase524bTemporalScenarioStage(string label)
        => label is "07_Velocity" or "09_BloomMip1" or "13c_MonoTsrReference" or "14_TsrOutput";

    private static bool IsPhase524bValidationEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPhase524bValidation);
        return string.Equals(raw, "1", StringComparison.Ordinal) ||
            string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldCaptureDefaultPipelineFbos()
    {
        string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CaptureDefaultPipelineFbo);
        return !string.IsNullOrWhiteSpace(raw) &&
            !string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidFinalOutputSourceFboOverride(string sourceFboName)
    {
        try
        {
            if (TryGetFBO(sourceFboName, out XRFrameBuffer? fbo) && fbo is not null)
                return true;

            Debug.RenderingWarningEvery(
                $"DefaultRenderPipeline.InvalidOutputSourceFbo.{sourceFboName}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] XRE_OUTPUT_SOURCE_FBO='{0}' does not resolve to a known FBO. Falling back to standard final output.",
                sourceFboName);
            return false;
        }
        catch (Exception ex)
        {
            Debug.RenderingWarningEvery(
                $"DefaultRenderPipeline.InvalidOutputSourceFbo.Exception.{sourceFboName}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] XRE_OUTPUT_SOURCE_FBO='{0}' could not be validated before final blit: {1}. Falling back to standard final output.",
                sourceFboName,
                ex.Message);
            return false;
        }
    }
}
