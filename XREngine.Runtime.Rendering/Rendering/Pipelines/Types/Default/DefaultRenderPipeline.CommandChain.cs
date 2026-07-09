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
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (c.AddUsing<VPRC_PushOutputFBORenderArea>())
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                c.Add<VPRC_StencilMask>().Set(~0u);
                c.Add<VPRC_ClearByBoundFBO>();
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, MeshSubmissionStrategy);
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, MeshSubmissionStrategy);
                if (enableComputePasses)
                    c.Add<VPRC_ForwardPlusLightCullingPass>();
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, MeshSubmissionStrategy);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, MeshSubmissionStrategy);
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, MeshSubmissionStrategy);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PerPixelLinkedListForward, MeshSubmissionStrategy);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DepthPeelingForward, MeshSubmissionStrategy);
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, MeshSubmissionStrategy);
                c.Add<VPRC_RenderMeshletDebugDisplay>();
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_BuildAccelerationStructure>();
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, MeshSubmissionStrategy);
                // GPU BVH wireframe overlay; no-op unless toggled via the
                // GpuBvhDebugSettings post-process stage on the active camera.
                c.Add<VPRC_RenderDebugGpuBvh>();
                c.Add<VPRC_RenderDebugShapes>();
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_ColorMask>().Set(true, true, true, true);
                if (enableComputePasses)
                    c.Add<VPRC_ForwardPlusDebugOverlay>();
            }
        }

        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        c.Add<VPRC_RenderScreenSpaceUI>();
        return c;
    }

    private ViewportRenderCommandContainer CreateViewportTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);
        bool enableComputePasses = EnableComputeDependentPasses;
        bool bypassVendorUpscale = RenderDiagnosticsFlags.BypassVendorUpscale;

        AppendTemporalBegin(c);

        CacheTextures(c);
        AppendVoxelConeTracingPass(c, enableComputePasses);

        c.Add<VPRC_ColorMask>().Set(true, true, true, true);
        c.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
        c.Add<VPRC_DepthWrite>().Allow = true;
        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            AppendAmbientOcclusionSwitch(c, enableComputePasses);
            AppendDeferredGBufferPass(c);
            AppendForwardDepthPrePass(c);
            c.Add<VPRC_DepthTest>().Enable = false;
            AppendAmbientOcclusionResolve(c);
            AppendForwardDepthPrePassGBufferRestore(c);
            AppendLightingPass(c);
            AppendForwardPass(c, enableComputePasses);
            AppendTransparencyPasses(c);

            c.Add<VPRC_DepthTest>().Enable = false;
            AppendVelocityPassSwitch(c);
            c.Add<VPRC_DepthTest>().Enable = false;
            AppendBloomPass(c);
            AppendMotionBlurAndDoF(c);
            AppendTemporalAccumulation(c);
            // Build the GPU BVH so debug overlays (and any zero-readback consumers)
            // have an up-to-date acceleration structure published into pipeline
            // variables before the on-top forward passes run.
            c.Add<VPRC_BuildAccelerationStructure>();
            AppendPostTemporalForwardPasses(c);
            // Fog must composite after the late forward batches; its passes upload
            // the temporal pass' stored current projection so depth reconstruction
            // still matches the jittered depth buffer after PopJitter.
            if (!Stereo)
            {
                AppendPostProcessCompositeInputDefaults(c);
                AppendAtmosphericScattering(c);
                AppendVolumetricFog(c);
            }
            c.Add<VPRC_DepthTest>().Enable = false;
            AppendPostProcessResourceCaching(c);
            AppendDebugVisualizationCaching(c);
            AppendFullOverdrawCountingPass(c);
            AppendAntiAliasingResourceCaching(c);
        }

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

    private static bool HasRenderPassCommands(int renderPass)
        => CurrentRenderingPipeline?.ActiveMeshRenderCommands.HasRenderingCommands(renderPass) == true;

    private bool ShouldRunForwardDepthPrePass()
        => !UseOpenXrVulkanDesktopStartupSafePath
        && ForwardDepthPrePassEnabled
        && (HasRenderPassCommands((int)EDefaultRenderPass.OpaqueForward)
            || HasRenderPassCommands((int)EDefaultRenderPass.MaskedForward));

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
            shareChoice.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                ForwardDepthPrePassMergeFBOName,
                CreateForwardDepthPrePassMergeFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);
            shareChoice.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                DeferredGBufferPreForwardCopyFBOName,
                CreateDeferredGBufferPreForwardCopyFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);
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
            shareChoice.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                ForwardContactPrePassCopyFBOName,
                CreateForwardContactPrePassCopyFBO,
                GetDesiredFBOSizeForwardDepthNormalPrePass)
                .UseLifetime(RenderResourceLifetime.Transient);
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
            restoreCommands.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                ForwardDepthPrePassMergeFBOName,
                CreateForwardDepthPrePassMergeFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);
            restoreCommands.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                DeferredGBufferPreForwardCopyFBOName,
                CreateDeferredGBufferPreForwardCopyFBO,
                GetDesiredFBOSizeInternal)
                .UseLifetime(RenderResourceLifetime.Transient);
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
        if (includeMsaaCommandGraph)
        {
            AddConditionalFboCache(c, "ForwardPassMsaaFBO", () => RuntimeEnableMsaaTargets,
                ForwardPassMsaaFBOName,
                CreateForwardPassMsaaFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreateMsaaFbo);
            AddConditionalFboCache(c, "MsaaLightCombineFBO", () => RuntimeEnableMsaaDeferred,
                MsaaLightCombineFBOName,
                CreateMsaaLightCombineFBO,
                GetDesiredFBOSizeInternal,
                NeedsRecreateMsaaLightCombineFbo);
            AddConditionalFboCache(c, "DepthPreloadFBO", () => RuntimeEnableMsaaTargets && !RuntimeEnableMsaaDeferred,
                DepthPreloadFBOName,
                CreateDepthPreloadFBO,
                GetDesiredFBOSizeInternal);
        }

        AddConditionalTransientFboCache(c, "TransparencySceneCopyFBO", () => EnableTransparencySceneCopyResources, SceneCopyFBOName, CreateSceneCopyFBO);

        AddConditionalTransientFboCache(c, "RestirCompositeFBO", () => enableComputePasses && UsesRestirGI, RestirCompositeFBOName, CreateRestirCompositeFBO);
        AddConditionalTransientFboCache(c, "LightVolumeCompositeFBO", () => enableComputePasses && UsesLightVolumes, LightVolumeCompositeFBOName, CreateLightVolumeCompositeFBO);
        AddConditionalTransientFboCache(c, "RadianceCascadeCompositeFBO", () => enableComputePasses && UsesRadianceCascades, RadianceCascadeCompositeFBOName, CreateRadianceCascadeCompositeFBO);
        AddConditionalTransientFboCache(c, "SurfelGICompositeFBO", () => enableComputePasses && UsesSurfelGI, SurfelGICompositeFBOName, CreateSurfelGICompositeFBO);

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

    private void AddConditionalFboCache(
        ViewportRenderCommandContainer c,
        string label,
        Func<bool> condition,
        string name,
        Func<XRFrameBuffer> factory,
        Func<(uint x, uint y)>? sizeVerifier,
        Func<XRFrameBuffer, bool>? needsRecreate = null)
    {
        var conditional = c.Add<VPRC_IfElse>();
        conditional.Label = label;
        conditional.ConditionEvaluator = condition;

        var commands = new ViewportRenderCommandContainer(this);
        commands.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            name,
            factory,
            sizeVerifier,
            needsRecreate);
        conditional.TrueCommands = commands;
    }

    private void AddConditionalTransientFboCache(
        ViewportRenderCommandContainer c,
        string label,
        Func<bool> condition,
        string name,
        Func<XRFrameBuffer> factory)
    {
        var conditional = c.Add<VPRC_IfElse>();
        conditional.Label = label;
        conditional.ConditionEvaluator = condition;

        var commands = new ViewportRenderCommandContainer(this);
        commands.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            name,
            factory,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);
        conditional.TrueCommands = commands;
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
        velocityChoice.ConditionEvaluator = ShouldGenerateVelocityBuffer;

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
            bloomCommands.Add<VPRC_BloomPass>().SetTargetFBONames(
                ForwardPassFBOName,
                BloomBlurTextureName,
                Stereo);
            bloomChoice.TrueCommands = bloomCommands;
        }

        AppendDiagnosticTextureCapture(c, "08_BloomMip0", BloomBlurTextureName, mipLevel: 0);
        AppendDiagnosticTextureCapture(c, "09_BloomMip1", BloomBlurTextureName, mipLevel: 1);
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

    private void AppendPostProcessResourceCaching(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            PostProcessFBOName,
            CreatePostProcessFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreatePostProcessFbo)
            .UseLifetime(RenderResourceLifetime.Transient);
    }

    private void AppendPostProcessCompositeInputDefaults(ViewportRenderCommandContainer c)
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
            return;

        var defaults = c.Add<VPRC_IfElse>();
        defaults.Label = "PostProcessCompositeInputDefaultsActive";
        defaults.ConditionEvaluator = ShouldUseFullSizePostProcessCompositeInputDefaults;
        var commands = new ViewportRenderCommandContainer(this);

        commands.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AtmosphereColorTextureName,
            CreateAtmosphereColorTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        commands.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VolumetricFogColorTextureName,
            CreateVolumetricFogColorTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        commands.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereUpscaleFBOName,
            CreateAtmosphereUpscaleFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateAtmosphereUpscaleFbo);

        commands.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogUpscaleFBOName,
            CreateVolumetricFogUpscaleFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateVolumetricFogUpscaleFbo);

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

    private void AppendDebugVisualizationCaching(ViewportRenderCommandContainer c)
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
            return;

        if (EnableTransformIdVisualization)
        {
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TransformIdDebugQuadFBOName,
                CreateTransformIdDebugQuadFBO,
                GetDesiredFBOSizeInternal);
        }

        if (EnableTransparencyAccumulationVisualization)
        {
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TransparentAccumulationDebugFBOName,
                CreateTransparentAccumulationDebugFBO,
                GetDesiredFBOSizeInternal);
        }

        if (EnableTransparencyRevealageVisualization)
        {
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TransparentRevealageDebugFBOName,
                CreateTransparentRevealageDebugFBO,
                GetDesiredFBOSizeInternal);
        }

        if (EnableTransparencyOverdrawVisualization)
        {
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                TransparentOverdrawDebugFBOName,
                CreateTransparentOverdrawDebugFBO,
                GetDesiredFBOSizeInternal);
        }

        if (EnableDepthPeelingLayerVisualization)
        {
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                DepthPeelingDebugFBOName,
                CreateDepthPeelingDebugFBO,
                GetDesiredFBOSizeInternal);
        }

        var fullOverdrawCache = c.Add<VPRC_IfElse>();
        fullOverdrawCache.ConditionEvaluator = () => EnableFullOverdrawVisualization;
        {
            var cacheCommands = new ViewportRenderCommandContainer(this);
            cacheCommands.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                FullOverdrawCountFBOName,
                CreateFullOverdrawCountFBO,
                GetDesiredFBOSizeInternal);
            cacheCommands.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                FullOverdrawDebugFBOName,
                CreateFullOverdrawDebugFBO,
                GetDesiredFBOSizeInternal);
            fullOverdrawCache.TrueCommands = cacheCommands;
        }
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

    private void AppendAntiAliasingResourceCaching(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            FinalPostProcessFBOName,
            CreateFinalPostProcessFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateFinalPostProcessFbo)
            .UseLifetime(RenderResourceLifetime.Transient);
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
                AppendDiagnosticTextureCapture(tsrUpscale, "14b_TsrHistoryColor", TsrHistoryColorTextureName);
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
                    postAaChoice.TrueCommands = fxaaUpscale;
                }
                {
                    var smaaUpscale = new ViewportRenderCommandContainer(this);
                    var smaa = smaaUpscale.Add<VPRC_SMAA>();
                    smaa.SourceFBOName = FinalPostProcessOutputFBOName;
                    smaa.OutputTextureName = SmaaOutputTextureName;
                    smaa.OutputFBOName = SmaaFBOName;
                    postAaChoice.FalseCommands = smaaUpscale;
                }
                tsrOrPostAa.FalseCommands = fxaaOrSmaa;
            }

            upscaleChoice.TrueCommands = upscaleCmds;
        }

        AppendDiagnosticTextureCapture(c, "14_TsrOutput", TsrOutputTextureName);
    }

    private void AppendExposureUpdate(ViewportRenderCommandContainer c)
    {
        string exposureSource = HDRSceneTextureName;
        c.Add<VPRC_ExposureUpdate>().SetOptions(exposureSource, true);

        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        using (c.AddUsing<VPRC_PushProgramBindings>(t => t.ApplyUniforms = PostProcessFBO_SettingUniforms))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(PostProcessFBOName, PostProcessOutputFBOName)
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
                .SetTargets(FinalPostProcessFBOName, FinalPostProcessOutputFBOName)
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

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AtmosphereHalfDepthTextureName,
            CreateAtmosphereHalfDepthTexture,
            NeedsRecreateTextureHalfInternalSize,
            ResizeTextureHalfInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereHalfDepthQuadFBOName,
            CreateAtmosphereHalfDepthQuadFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateAtmosphereHalfDepthQuadFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereHalfDepthFBOName,
            CreateAtmosphereHalfDepthFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateAtmosphereHalfDepthFbo);

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AtmosphereHalfDepthQuadFBOName, AtmosphereHalfDepthFBOName, matchDestinationRenderArea: true);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AtmosphereHalfScatterTextureName,
            CreateAtmosphereHalfScatterTexture,
            NeedsRecreateTextureHalfInternalSize,
            ResizeTextureHalfInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereHalfScatterQuadFBOName,
            CreateAtmosphereHalfScatterQuadFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateAtmosphereHalfScatterQuadFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereHalfScatterFBOName,
            CreateAtmosphereHalfScatterFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateAtmosphereHalfScatterFbo);

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AtmosphereHalfScatterQuadFBOName, AtmosphereHalfScatterFBOName, matchDestinationRenderArea: true);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AtmosphereHalfTemporalTextureName,
            CreateAtmosphereHalfTemporalTexture,
            NeedsRecreateTextureHalfInternalSize,
            ResizeTextureHalfInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AtmosphereHalfHistoryTextureName,
            CreateAtmosphereHalfHistoryTexture,
            NeedsRecreateTextureHalfInternalSize,
            ResizeTextureHalfInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereReprojectQuadFBOName,
            CreateAtmosphereReprojectQuadFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateAtmosphereReprojectQuadFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereReprojectFBOName,
            CreateAtmosphereReprojectFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateAtmosphereReprojectFbo);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereHistoryFBOName,
            CreateAtmosphereHistoryFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateAtmosphereHistoryFbo);

        c.Add<VPRC_AtmosphereHistoryPass>().Phase = VPRC_AtmosphereHistoryPass.EPhase.Begin;
        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AtmosphereReprojectQuadFBOName, AtmosphereReprojectFBOName, matchDestinationRenderArea: true);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AtmosphereColorTextureName,
            CreateAtmosphereColorTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereUpscaleQuadFBOName,
            CreateAtmosphereUpscaleQuadFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateAtmosphereUpscaleQuadFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereUpscaleFBOName,
            CreateAtmosphereUpscaleFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateAtmosphereUpscaleFbo);

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
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VolumetricFogHalfDepthTextureName,
            CreateVolumetricFogHalfDepthTexture,
            NeedsRecreateTextureHalfInternalSize,
            ResizeTextureHalfInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogHalfDepthQuadFBOName,
            CreateVolumetricFogHalfDepthQuadFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateVolumetricFogHalfDepthQuadFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogHalfDepthFBOName,
            CreateVolumetricFogHalfDepthFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateVolumetricFogHalfDepthFbo);

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(VolumetricFogHalfDepthQuadFBOName, VolumetricFogHalfDepthFBOName, matchDestinationRenderArea: true);

        // Stage 2: half-resolution scatter raymarch.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VolumetricFogHalfScatterTextureName,
            CreateVolumetricFogHalfScatterTexture,
            NeedsRecreateTextureHalfInternalSize,
            ResizeTextureHalfInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogHalfScatterQuadFBOName,
            CreateVolumetricFogHalfScatterQuadFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateVolumetricFogHalfScatterQuadFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogHalfScatterFBOName,
            CreateVolumetricFogHalfScatterFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateVolumetricFogHalfScatterFbo);

        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(VolumetricFogHalfScatterQuadFBOName, VolumetricFogHalfScatterFBOName, matchDestinationRenderArea: true);

        // Stage 3: half-resolution temporal reprojection.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VolumetricFogHalfTemporalTextureName,
            CreateVolumetricFogHalfTemporalTexture,
            NeedsRecreateTextureHalfInternalSize,
            ResizeTextureHalfInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VolumetricFogHalfHistoryTextureName,
            CreateVolumetricFogHalfHistoryTexture,
            NeedsRecreateTextureHalfInternalSize,
            ResizeTextureHalfInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogReprojectQuadFBOName,
            CreateVolumetricFogReprojectQuadFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateVolumetricFogReprojectQuadFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogReprojectFBOName,
            CreateVolumetricFogReprojectFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateVolumetricFogReprojectFbo);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogHistoryFBOName,
            CreateVolumetricFogHistoryFBO,
            GetDesiredFBOSizeHalfInternal,
            NeedsRecreateVolumetricFogHistoryFbo);

        c.Add<VPRC_VolumetricFogHistoryPass>().Phase = VPRC_VolumetricFogHistoryPass.EPhase.Begin;
        c.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(VolumetricFogReprojectQuadFBOName, VolumetricFogReprojectFBOName, matchDestinationRenderArea: true);

        // Stage 4: full-resolution bilateral upscale.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VolumetricFogColorTextureName,
            CreateVolumetricFogColorTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogUpscaleQuadFBOName,
            CreateVolumetricFogUpscaleQuadFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateVolumetricFogUpscaleQuadFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogUpscaleFBOName,
            CreateVolumetricFogUpscaleFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateVolumetricFogUpscaleFbo);

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

    private static void AppendDiagnosticTextureCapture(
        ViewportRenderCommandContainer c,
        string label,
        string textureName,
        int mipLevel = 0)
    {
        if (!ShouldCaptureDefaultPipelineFbos())
            return;

        var capture = c.Add<VPRC_CaptureFrame>();
        capture.SourceTextureName = textureName;
        capture.SourceMipLevel = mipLevel;
        capture.MaxCaptures = 1;
        capture.SkipFramesBeforeCapture = ResolveDefaultPipelineCaptureSkipFrames();
        capture.OutputFilePath = Path.Combine("Build", "Diagnostics", "FrameCaptures", $"DefaultPipeline_{label}.png");
        capture.FlipVertically = false;
    }

    private static void AppendDiagnosticFboCapture(
        ViewportRenderCommandContainer c,
        string label,
        string fboName)
    {
        if (!ShouldCaptureDefaultPipelineFbos())
            return;

        var capture = c.Add<VPRC_CaptureFrame>();
        capture.SourceFBOName = fboName;
        capture.MaxCaptures = 1;
        capture.SkipFramesBeforeCapture = ResolveDefaultPipelineCaptureSkipFrames();
        capture.OutputFilePath = Path.Combine("Build", "Diagnostics", "FrameCaptures", $"DefaultPipeline_{label}.png");
        capture.FlipVertically = false;
    }

    private static int ResolveDefaultPipelineCaptureSkipFrames()
    {
        string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CaptureDefaultPipelineSkipFrames);
        return int.TryParse(raw, out int skipFrames) ? Math.Max(0, skipFrames) : 120;
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
