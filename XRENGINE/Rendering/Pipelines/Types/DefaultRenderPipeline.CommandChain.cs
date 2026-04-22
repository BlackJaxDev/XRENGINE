using System;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    #region Command Chain Generation

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer c = new(this);
        var ifElse = c.Add<VPRC_IfElse>();
        ifElse.ConditionEvaluator = () => State.WindowViewport is not null;
        ifElse.TrueCommands = CreateViewportTargetCommands();
        ifElse.FalseCommands = CreateFBOTargetCommands();
        return c;
    }

    public ViewportRenderCommandContainer CreateFBOTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);
        bool enableComputePasses = EnableComputeDependentPasses;

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
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, GPURenderDispatch);
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, GPURenderDispatch);
                if (enableComputePasses)
                    c.Add<VPRC_ForwardPlusLightCullingPass>();
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, GPURenderDispatch);
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PerPixelLinkedListForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DepthPeelingForward, GPURenderDispatch);
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GPURenderDispatch);
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GPURenderDispatch);
            }
        }

        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, GPURenderDispatch);
        c.Add<VPRC_RenderScreenSpaceUI>();
        return c;
    }

    private ViewportRenderCommandContainer CreateViewportTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);
        bool enableComputePasses = EnableComputeDependentPasses;
        bool bypassVendorUpscale = string.Equals(
            Environment.GetEnvironmentVariable("XRE_BYPASS_VENDOR_UPSCALE"),
            "1",
            StringComparison.Ordinal);

        c.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Begin;

        CacheTextures(c);
        AppendVoxelConeTracingPass(c, enableComputePasses);

        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            AppendAmbientOcclusionSwitch(c, enableComputePasses);
            AppendDeferredGBufferPass(c);
            AppendForwardDepthPrePass(c);
            c.Add<VPRC_DepthTest>().Enable = false;
            AppendAmbientOcclusionResolve(c);
            AppendLightingPass(c);
            AppendForwardPass(c, enableComputePasses);
            AppendTransparencyPasses(c);

            c.Add<VPRC_DepthTest>().Enable = false;
            AppendVelocityPass(c);
            c.Add<VPRC_DepthTest>().Enable = false;
            AppendBloomPass(c);
            AppendMotionBlurAndDoF(c);
            AppendTemporalAccumulation(c);
            AppendPostTemporalForwardPasses(c);
            c.Add<VPRC_DepthTest>().Enable = false;
            AppendPostProcessResourceCaching(c);
            AppendDebugVisualizationCaching(c);
            AppendAntiAliasingResourceCaching(c);
        }

        // Volumetric fog scatter runs once at internal resolution, writing an
        // RGBA16F texture that the post-process composite samples. Skipped in
        // stereo (no stereo scatter variant yet).
        if (!Stereo)
            AppendVolumetricFog(c);

        AppendExposureUpdate(c);
        AppendFxaaTsrUpscaleChain(c);
        AppendTemporalCommit(c);
        AppendFinalOutput(c, bypassVendorUpscale);

        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        c.Add<VPRC_RenderScreenSpaceUI>();
        return c;
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
                [(int)AmbientOcclusionSettings.EType.SpatialHashExperimental] = CreateSpatialHashAOPassCommands(),
            }
            : new()
            {
                [(int)AmbientOcclusionSettings.EType.ScreenSpace] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusPassCommands(),
                [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion] = CreateVXAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.SpatialHashExperimental] = CreateSSAOPassCommands(),
            };
        aoSwitch.DefaultCase = CreateAmbientOcclusionDisabledPassCommands();
    }

    private void AppendDeferredGBufferPass(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DeferredGBufferFBOName,
            CreateDeferredGBufferFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateDeferredGBufferFbo);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            MsaaGBufferFBOName,
            CreateMsaaGBufferFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateMsaaFbo);
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            MsaaLightingFBOName,
            CreateMsaaLightingFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateMsaaFbo);

        using (c.AddUsing<VPRC_BindFBOByName>(x =>
        {
            x.Write = true;
            x.ClearColor = true;
            x.ClearDepth = true;
            x.ClearStencil = true;
            x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : DeferredGBufferFBOName;
        }))
        {
            c.Add<VPRC_StencilMask>().Set(~0u);
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, GPURenderDispatch);
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DeferredDecals, GPURenderDispatch);
        }

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

    private void AppendForwardDepthPrePass(ViewportRenderCommandContainer c)
    {
        var prePassChoice = c.Add<VPRC_IfElse>();
        prePassChoice.ConditionEvaluator = () => Engine.EditorPreferences.Debug.ForwardDepthPrePassEnabled;
        {
            var shareChoice = new ViewportRenderCommandContainer(this);
            var shareIfElse = shareChoice.Add<VPRC_IfElse>();
            shareIfElse.ConditionEvaluator = () => Engine.EditorPreferences.Debug.ForwardPrePassSharesGBufferTargets;
            shareIfElse.TrueCommands = CreateForwardPrePassSharedCommands();
            shareIfElse.FalseCommands = CreateForwardPrePassSeparateCommands();
            prePassChoice.TrueCommands = shareChoice;
        }
    }

    private void AppendAmbientOcclusionResolve(ViewportRenderCommandContainer c)
    {
        var aoResolveSwitch = c.Add<VPRC_Switch>();
        aoResolveSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
        aoResolveSwitch.Cases = new()
        {
            [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusResolveCommands(),
            [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOResolveCommands(),
        };
        aoResolveSwitch.DefaultCase = CreateAmbientOcclusionResolveCommands();
    }

    private void AppendLightingPass(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_SyncLightProbeResources>();

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            LightCombineFBOName,
            CreateLightCombineFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateLightCombineFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

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
                LightCombineFBOName,
                EReadBufferMode.ColorAttachment0,
                blitColor: true,
                blitDepth: false,
                blitStencil: false,
                linearFilter: false);
            msaaLightingBranch.TrueCommands = msaaLightCmds;
        }
        {
            var stdLightCmds = new ViewportRenderCommandContainer(this);
            using (stdLightCmds.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(LightCombineFBOName)))
            {
                stdLightCmds.Add<VPRC_StencilMask>().Set(~0u);
                stdLightCmds.Add<VPRC_LightCombinePass>().SetOptions(
                    AlbedoOpacityTextureName,
                    NormalTextureName,
                    RMSETextureName,
                    DepthViewTextureName);
            }
            msaaLightingBranch.FalseCommands = stdLightCmds;
        }
    }

    private void AppendForwardPass(ViewportRenderCommandContainer c, bool enableComputePasses)
    {
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardPassMsaaFBOName,
            CreateForwardPassMsaaFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateMsaaFbo);
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            MsaaLightCombineFBOName,
            CreateMsaaLightCombineFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateMsaaLightCombineFbo);
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DepthPreloadFBOName,
            CreateDepthPreloadFBO,
            GetDesiredFBOSizeInternal);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardPassFBOName,
            CreateForwardPassFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateForwardPassFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            SceneCopyFBOName,
            CreateSceneCopyFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TransparentSceneCopyFBOName,
            CreateTransparentSceneCopyFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DeferredTransparencyBlurFBOName,
            CreateDeferredTransparencyBlurFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TransparentAccumulationFBOName,
            CreateTransparentAccumulationFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TransparentResolveFBOName,
            CreateTransparentResolveFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            RestirCompositeFBOName,
            CreateRestirCompositeFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            LightVolumeCompositeFBOName,
            CreateLightVolumeCompositeFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            RadianceCascadeCompositeFBOName,
            CreateRadianceCascadeCompositeFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            SurfelGICompositeFBOName,
            CreateSurfelGICompositeFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            HistoryCaptureFBOName,
            CreateHistoryCaptureFBO,
            GetDesiredFBOSizeInternal);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TemporalInputFBOName,
            CreateTemporalInputFBO,
            GetDesiredFBOSizeInternal);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TemporalAccumulationFBOName,
            CreateTemporalAccumulationFBO,
            GetDesiredFBOSizeInternal);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            HistoryExposureFBOName,
            CreateHistoryExposureFBO,
            GetDesiredFBOSizeInternal);

        using (c.AddUsing<VPRC_BindFBOByName>(x =>
        {
            x.Write = true;
            x.ClearStencil = true;
            x.DynamicName = () => RuntimeEnableMsaa ? ForwardPassMsaaFBOName : ForwardPassFBOName;
            x.ClearColor = true;
            x.DynamicClearDepth = () => RuntimeEnableMsaa;
        }))
        {
            var msaaPreload = c.Add<VPRC_IfElse>();
            msaaPreload.ConditionEvaluator = () => RuntimeEnableMsaa;
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

            c.Add<VPRC_DepthTest>().Enable = false;
            c.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = LightCombineFBOName;

            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, GPURenderDispatch);

            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            if (enableComputePasses)
                c.Add<VPRC_ForwardPlusLightCullingPass>();
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GPURenderDispatch);
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, GPURenderDispatch);

            if (enableComputePasses)
            {
                c.Add<VPRC_ReSTIRPass>();
                c.Add<VPRC_LightVolumesPass>();
                c.Add<VPRC_RadianceCascadesPass>();
                c.Add<VPRC_SurfelGIPass>();
            }

            c.Add<VPRC_RenderDebugShapes>();
            c.Add<VPRC_RenderDebugPhysics>();
        }

        var msaaResolve = c.Add<VPRC_IfElse>();
        msaaResolve.ConditionEvaluator = () => RuntimeEnableMsaa;
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

    private void AppendTransparencyPasses(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
        c.Add<VPRC_RenderQuadFBO>().FrameBufferName = DeferredTransparencyBlurFBOName;
        c.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
        c.Add<VPRC_ClearTextureByName>().SetOptions(TransparentAccumTextureName, ColorF4.Transparent);
        c.Add<VPRC_ClearTextureByName>().SetOptions(TransparentRevealageTextureName, ColorF4.White);
        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(TransparentAccumulationFBOName, true, false, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, GPURenderDispatch);
        }
        c.Add<VPRC_RenderQuadFBO>().FrameBufferName = TransparentResolveFBOName;

        AppendExactTransparencyCommands(c);
    }

    private void AppendVelocityPass(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VelocityFBOName,
            CreateVelocityFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

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
    }

    private void AppendBloomPass(ViewportRenderCommandContainer c)
    {
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
    }

    private void AppendMotionBlurAndDoF(ViewportRenderCommandContainer c)
    {
        var motionBlurChoice = c.Add<VPRC_IfElse>();
        motionBlurChoice.ConditionEvaluator = ShouldUseMotionBlur;
        motionBlurChoice.TrueCommands = CreateMotionBlurPassCommands();

        var dofChoice = c.Add<VPRC_IfElse>();
        dofChoice.ConditionEvaluator = ShouldUseDepthOfField;
        dofChoice.TrueCommands = CreateDepthOfFieldPassCommands();
    }

    private void AppendTemporalAccumulation(ViewportRenderCommandContainer c)
    {
        var temporalAccumulate = c.Add<VPRC_TemporalAccumulationPass>();
        temporalAccumulate.Phase = VPRC_TemporalAccumulationPass.EPhase.Accumulate;
        temporalAccumulate.ConfigureAccumulationTargets(
            ForwardPassFBOName,
            TemporalInputFBOName,
            TemporalAccumulationFBOName,
            HistoryCaptureFBOName,
            HistoryExposureFBOName);

        c.Add<VPRC_TemporalAccumulationPass>().Phase =
            VPRC_TemporalAccumulationPass.EPhase.PopJitter;
    }

    private void AppendPostTemporalForwardPasses(ViewportRenderCommandContainer c)
    {
        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardPassFBOName, true, false, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GPURenderDispatch);
            c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GPURenderDispatch);
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

    private void AppendDebugVisualizationCaching(ViewportRenderCommandContainer c)
    {
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
    }

    private void AppendAntiAliasingResourceCaching(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            FxaaOutputTextureName,
            CreateFxaaOutputTexture,
            NeedsRecreatePostProcessTextureFullSize,
            ResizeTextureFullSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            PostProcessOutputFBOName,
            CreatePostProcessOutputFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreatePostProcessOutputFbo);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            FxaaFBOName,
            CreateFxaaFBO,
            GetDesiredFBOSizeFull,
            NeedsRecreateFxaaFbo);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TsrHistoryColorTextureName,
            CreateTsrHistoryColorTexture,
            NeedsRecreatePostProcessTextureFullSize,
            ResizeTextureFullSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TsrHistoryColorFBOName,
            CreateTsrHistoryColorFBO,
            GetDesiredFBOSizeFull,
            NeedsRecreateTsrHistoryColorFbo);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TsrUpscaleFBOName,
            CreateTsrUpscaleFBO,
            GetDesiredFBOSizeFull,
            NeedsRecreateTsrUpscaleFbo);
    }

    private void AppendFxaaTsrUpscaleChain(ViewportRenderCommandContainer c)
    {
        var upscaleChoice = c.Add<VPRC_IfElse>();
        upscaleChoice.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableSmaa || RuntimeNeedsTsrUpscale;
        {
            var upscaleCmds = new ViewportRenderCommandContainer(this);

            using (upscaleCmds.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
            {
                var tsrOrPostAa = upscaleCmds.Add<VPRC_IfElse>();
                tsrOrPostAa.ConditionEvaluator = () => RuntimeNeedsTsrUpscale;
                {
                    var tsrUpscale = new ViewportRenderCommandContainer(this);
                    tsrUpscale.Add<VPRC_RenderQuadToFBO>().SetTargets(TsrUpscaleFBOName, TsrUpscaleFBOName);
                    tsrUpscale.Add<VPRC_BlitFrameBuffer>().SetOptions(
                        TsrUpscaleFBOName,
                        TsrHistoryColorFBOName,
                        EReadBufferMode.ColorAttachment0,
                        blitColor: true,
                        blitDepth: false,
                        blitStencil: false,
                        linearFilter: false);
                    tsrOrPostAa.TrueCommands = tsrUpscale;
                }
                {
                    var fxaaOrSmaa = new ViewportRenderCommandContainer(this);
                    var postAaChoice = fxaaOrSmaa.Add<VPRC_IfElse>();
                    postAaChoice.ConditionEvaluator = () => RuntimeEnableFxaa;
                    {
                        var fxaaUpscale = new ViewportRenderCommandContainer(this);
                        fxaaUpscale.Add<VPRC_RenderQuadToFBO>().SetTargets(FxaaFBOName, FxaaFBOName);
                        postAaChoice.TrueCommands = fxaaUpscale;
                    }
                    {
                        var smaaUpscale = new ViewportRenderCommandContainer(this);
                        var smaa = smaaUpscale.Add<VPRC_SMAA>();
                        smaa.SourceTextureName = PostProcessOutputTextureName;
                        smaa.OutputTextureName = SmaaOutputTextureName;
                        smaa.OutputFBOName = SmaaFBOName;
                        postAaChoice.FalseCommands = smaaUpscale;
                    }
                    tsrOrPostAa.FalseCommands = fxaaOrSmaa;
                }
            }

            upscaleChoice.TrueCommands = upscaleCmds;
        }
    }

    private void AppendExposureUpdate(ViewportRenderCommandContainer c)
    {
        string exposureSource = HDRSceneTextureName;
        c.Add<VPRC_ExposureUpdate>().SetOptions(exposureSource, true);

        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            c.Add<VPRC_RenderQuadToFBO>().SetTargets(PostProcessFBOName, PostProcessOutputFBOName);
        }
    }

    /// <summary>
    /// Caches and runs the three-stage volumetric fog chain:
    ///   1. Half-resolution depth downsample (<c>VolumetricFogHalfDepth</c>).
    ///   2. Half-resolution scatter raymarch (<c>VolumetricFogHalfScatter</c>).
    ///   3. Full-resolution bilateral upscale (<c>VolumetricFogColor</c>).
    /// The scatter shader early-outs to (0,0,0,1) when no volumes are present
    /// or the effect is disabled, so the post-process composite degrades to a
    /// no-op without an external gate. Each pass uses
    /// <see cref="VPRC_RenderQuadToFBO.MatchDestinationRenderArea"/> so the
    /// viewport follows the destination FBO's size automatically.
    /// </summary>
    private void AppendVolumetricFog(ViewportRenderCommandContainer c)
    {
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

        // Stage 3: full-resolution bilateral upscale.
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
    }

    private void AppendTemporalCommit(ViewportRenderCommandContainer c)
        => c.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Commit;

    private void AppendFinalOutput(ViewportRenderCommandContainer c, bool bypassVendorUpscale)
    {
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                if (EnableTransformIdVisualization)
                {
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(TransformIdDebugQuadFBOName, null);
                }
                else if (ActiveTransparencyDebugFboName is not null)
                {
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(ActiveTransparencyDebugFboName, null);
                }
                else
                {
                    string? overrideSource = Environment.GetEnvironmentVariable("XRE_OUTPUT_SOURCE_FBO");
                    if (!string.IsNullOrWhiteSpace(overrideSource))
                    {
                        if (bypassVendorUpscale)
                        {
                            c.Add<VPRC_RenderQuadToFBO>().SetTargets(overrideSource, null);
                        }
                        else
                        {
                            var vendorBlit = c.Add<VPRC_VendorUpscale>();
                            vendorBlit.FrameBufferName = overrideSource;
                            vendorBlit.DepthTextureName = DepthViewTextureName;
                            vendorBlit.MotionTextureName = VelocityTextureName;
                        }
                    }
                    else
                    {
                        var upscaleOutputChoice = c.Add<VPRC_IfElse>();
                        upscaleOutputChoice.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableSmaa || RuntimeNeedsTsrUpscale;
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
                        upscaleOutputChoice.FalseCommands = CreateFinalBlitCommands(PostProcessOutputFBOName, bypassVendorUpscale);
                    }
                }
            }
        }
    }

    #endregion
}