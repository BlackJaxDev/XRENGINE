using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline2
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

        //Create FBOs only after all their texture dependencies have been cached.

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

            // TransparentForward and OnTopForward are rendered AFTER the temporal
            // accumulation resolve (see below) so that sub-pixel jitter does not
            // shift alpha-test / blend boundaries, which causes smearing/ghosting
            // when TAA/TSR tries to blend jittered transparent edges with history.

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

        AppendFxaaTsrUpscaleChain(c);
        AppendExposureUpdate(c);
        AppendTemporalCommit(c);
        AppendFinalOutput(c, bypassVendorUpscale);

        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        c.Add<VPRC_RenderScreenSpaceUI>();
        return c;
    }

    /// <summary>Appends the voxel-cone-tracing voxelization dispatch (when compute is available).</summary>
    private void AppendVoxelConeTracingPass(ViewportRenderCommandContainer c, bool enableComputePasses)
    {
        if (enableComputePasses)
        {
            c.Add<VPRC_VoxelConeTracingPass>().SetOptions(VoxelConeTracingVolumeTextureName,
                [
                    (int)EDefaultRenderPass.OpaqueDeferred,
                    (int)EDefaultRenderPass.OpaqueForward,
                    (int)EDefaultRenderPass.MaskedForward
                ],
                GPURenderDispatch,
                true);
        }
            
        //Create FBOs only after all their texture dependencies have been cached.
    }

    /// <summary>Appends the ambient-occlusion mode switch (selecting SSAO, HBAO+, GTAO, etc.).</summary>
    private void AppendAmbientOcclusionSwitch(ViewportRenderCommandContainer c, bool enableComputePasses)
    {
        if (enableComputePasses)
        {
            // Render to the ambient occlusion FBO using a switch to select the active AO implementation.
            var aoSwitch = c.Add<VPRC_Switch>();
            aoSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
            aoSwitch.Cases = new()
            {
                [(int)AmbientOcclusionSettings.EType.ScreenSpace] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.HorizonBased] = CreateHBAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusPassCommands(),
                [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion] = CreateVXAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiViewCustom] = CreateMVAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype] = CreateMSVOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.SpatialHashExperimental] = CreateSpatialHashAOPassCommands(),
            };
            aoSwitch.DefaultCase = CreateAmbientOcclusionDisabledPassCommands();
        }
        else
        {
            var aoSwitch = c.Add<VPRC_Switch>();
            aoSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
            aoSwitch.Cases = new()
            {
                [(int)AmbientOcclusionSettings.EType.ScreenSpace] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.HorizonBased] = CreateHBAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusPassCommands(),
                [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion] = CreateVXAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiViewCustom] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.SpatialHashExperimental] = CreateSSAOPassCommands(),
            };
            aoSwitch.DefaultCase = CreateAmbientOcclusionDisabledPassCommands();
        }

    }

    /// <summary>Caches MSAA deferred FBOs, renders deferred GBuffer geometry, and resolves MSAA when active.</summary>
    private void AppendDeferredGBufferPass(ViewportRenderCommandContainer c)
    {
        // MSAA deferred GBuffer and Lighting FBOs must be cached before any command tries to bind them.
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

        // Deferred GBuffer geometry rendering.
        // When MSAA deferred is active, renders into the MSAA GBuffer FBO for per-sample surface data.
        // Otherwise renders into the standard AO FBO (non-MSAA).
        // Always clear color+depth so the GBuffer starts with known values.
        using (c.AddUsing<VPRC_BindFBOByName>(x =>
        {
            x.Write = true;
            x.ClearColor = true;
            x.ClearDepth = true;
            x.ClearStencil = true;
            x.DynamicName = () => RuntimeEnableMsaaDeferred ? MsaaGBufferFBOName : AmbientOcclusionFBOName;
        }))
        {
            c.Add<VPRC_StencilMask>().Set(~0u);
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DeferredDecals, GPURenderDispatch);
        }

        // When MSAA deferred is active, also render geometry into the non-MSAA AO FBO
        // so that the AO pass has correct GBuffer data (SSAO doesn't support MSAA textures).
        {
            var msaaGBufferBranch = c.Add<VPRC_IfElse>();
            msaaGBufferBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
            {
                var msaaGeomCmds = new ViewportRenderCommandContainer(this);
                // Resolve MSAA GBuffer â†’ non-MSAA GBuffer (AO FBO) for AO compatibility
                msaaGeomCmds.Add<VPRC_ResolveMsaaGBuffer>().SetOptions(
                    MsaaGBufferFBOName,
                    AmbientOcclusionFBOName,
                    colorAttachmentCount: 4,
                    resolveDepthStencil: true);
                msaaGBufferBranch.TrueCommands = msaaGeomCmds;
            }
        }

    }

    /// <summary>Appends the forward depth+normal pre-pass (shared or separate GBuffer targets).</summary>
    private void AppendForwardDepthPrePass(ViewportRenderCommandContainer c)
    {
        var prePassChoice = c.Add<VPRC_IfElse>();
        prePassChoice.ConditionEvaluator = () => Engine.EditorPreferences.Debug.ForwardDepthPrePassEnabled;
        {
            // When sharing GBuffer targets, skip the dedicated forward-only FBO
            // and render only into the merged GBuffer attachments.
            var shareChoice = new ViewportRenderCommandContainer(this);
            var shareIfElse = shareChoice.Add<VPRC_IfElse>();
            shareIfElse.ConditionEvaluator = () => Engine.EditorPreferences.Debug.ForwardPrePassSharesGBufferTargets;
            shareIfElse.TrueCommands = CreateForwardPrePassSharedCommands();
            shareIfElse.FalseCommands = CreateForwardPrePassSeparateCommands();
            prePassChoice.TrueCommands = shareChoice;
        }

    }

    /// <summary>Appends the AO blur/resolve switch (HBAO+, GTAO, or default).</summary>
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

    /// <summary>Caches LightCombine FBO, marks MSAA complex pixels, and renders the deferred lighting pass.</summary>
    private void AppendLightingPass(ViewportRenderCommandContainer c)
    {
        //LightCombine FBO
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            LightCombineFBOName,
            CreateLightCombineFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        // MSAA deferred: mark complex pixels in the MSAA depth-stencil before lighting
        {
            var msaaMarkBranch = c.Add<VPRC_IfElse>();
            msaaMarkBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
            {
                var markCmds = new ViewportRenderCommandContainer(this);
                // Clear color (zero for additive lighting) and stencil (fresh for marking),
                // but NOT depth â€” the MSAA depth-stencil is shared from the GBuffer pass.
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
        }

        // Render the GBuffer to the lighting FBO.
        // When MSAA deferred is active, light volumes render into the MSAA Lighting FBO
        // using two-pass (simple + complex with per-sample shading).
        // Otherwise, light volumes render into the standard LightCombine FBO.
        {
            var msaaLightingBranch = c.Add<VPRC_IfElse>();
            msaaLightingBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
            {
                // MSAA path: render lights into MSAA Lighting FBO, then resolve to DiffuseTexture
                var msaaLightCmds = new ViewportRenderCommandContainer(this);
                // Do NOT clear â€” color was zeroed and stencil was marked by the marking phase above;
                // depth is shared from the GBuffer and must be preserved for light volume testing.
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
                // Resolve MSAA lighting â†’ non-MSAA DiffuseTexture
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
                // Non-MSAA path: render lights directly into LightCombine FBO
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

    }

    /// <summary>Caches forward-pass FBOs, renders opaque/masked/GI/debug, and resolves MSAA.</summary>
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
            GetDesiredFBOSizeInternal);
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DepthPreloadFBOName,
            CreateDepthPreloadFBO,
            GetDesiredFBOSizeInternal);

        // MSAA deferred FBO caching is done earlier (before GBuffer geometry render).

        //ForwardPass FBO
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardPassFBOName,
            CreateForwardPassFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TransparentSceneCopyFBOName,
            CreateTransparentSceneCopyFBO,
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

        //Render forward pass - GBuffer results + forward lit meshes + debug data
        // FBO target and clear flags are resolved at render time so per-camera AA overrides work.
        // Color is always cleared: the LightCombine quad overwrites every pixel, but stale
        // HDRSceneTexture content from the previous frame can bleed through if the quad
        // doesn't cover fully (e.g., edge of viewport) or if the MSAA resolve blit fails.
        // Depth is only cleared for MSAA (renderbuffers start undefined); the non-MSAA path
        // preserves GBuffer depth so forward materials depth-test against deferred geometry.
        // Stencil is always cleared so post-process outline is driven only by current-frame writes.
        using (c.AddUsing<VPRC_BindFBOByName>(x =>
        {
            x.Write = true;
            x.ClearStencil = true;
            x.DynamicName = () => RuntimeEnableMsaa ? ForwardPassMsaaFBOName : ForwardPassFBOName;
            x.ClearColor = true;
            x.DynamicClearDepth = () => RuntimeEnableMsaa;
        }))
        {
            // Depth preload is only needed for MSAA.
            var msaaPreload = c.Add<VPRC_IfElse>();
            msaaPreload.ConditionEvaluator = () => RuntimeEnableMsaa;
            {
                var preloadCmds = new ViewportRenderCommandContainer(this);

                // When deferred MSAA is active, blit per-sample depth from the MSAA GBuffer
                // instead of the non-MSAA shader-based preload. This preserves per-sample
                // depth at silhouette edges so the skybox can render at actual sky samples
                // and forward meshes get correct per-sample depth testing.
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
                    // Forward-only MSAA: shader-based preload from non-MSAA depth
                    var shaderCmds = new ViewportRenderCommandContainer(this);
                    shaderCmds.Add<VPRC_RenderQuadToFBO>().SetTargets(DepthPreloadFBOName, ForwardPassMsaaFBOName);
                    deferredChoice.FalseCommands = shaderCmds;
                }

                msaaPreload.TrueCommands = preloadCmds;
            }

            //Render the deferred pass lighting result, no depth testing
            c.Add<VPRC_DepthTest>().Enable = false;

            // When deferred MSAA is active, use the per-sample LightCombine variant
            // so direct light is read from the MSAA lighting texture per-sample via
            // sampler2DMS + gl_SampleID. This avoids the dark silhouette edges that
            // occur when the premature resolve averages sky-samples (zero) with
            // geometry lighting before the skybox has a chance to fill them.
            var lightCompositeBranch = c.Add<VPRC_IfElse>();
            lightCompositeBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
            {
                var msaaCmds = new ViewportRenderCommandContainer(this);
                msaaCmds.Add<VPRC_SampleShading>().Enable = true;
                msaaCmds.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = MsaaLightCombineFBOName;
                msaaCmds.Add<VPRC_SampleShading>().Enable = false;
                lightCompositeBranch.TrueCommands = msaaCmds;
            }
            {
                var stdCmds = new ViewportRenderCommandContainer(this);
                stdCmds.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = LightCombineFBOName;
                lightCompositeBranch.FalseCommands = stdCmds;
            }

            //Backgrounds (skybox) should honor the depth buffer but avoid modifying it
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, GPURenderDispatch);

            //Enable depth testing and writing for forward passes
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

        // MSAA resolve blit: only execute when MSAA is active for the current camera.
        // Only color is resolved; OpenGL 4.6 Â§18.3.1 forbids blitting depth/stencil
        // from a multisampled read framebuffer to a single-sample draw framebuffer
        // (generates GL_INVALID_OPERATION and aborts the entire blit, including color).
        // The non-MSAA DepthStencilTexture retains the GBuffer depth, which is sufficient
        // for subsequent transparent passes and post-processing.
        {
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
    }

    /// <summary>Appends WB-OIT accumulation/resolve and exact transparency passes.</summary>
    private void AppendTransparencyPasses(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_RenderQuadToFBO>().SetTargets(ForwardPassFBOName, TransparentSceneCopyFBOName);
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

    /// <summary>Caches the velocity FBO, renders motion vectors, and restores default clears.</summary>
    private void AppendVelocityPass(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VelocityFBOName,
            CreateVelocityFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        // Ensure the velocity target is initialized to zero instead of inheriting whatever clear
        // color previous passes left on the renderer. Non-zero clears here imprint the scene's
        // clear color into the velocity buffer, which then looks like a color pass when previewed
        // and corrupts motion blur accumulation.
        //Debug.Out($"[Velocity] Preparing velocity pass. InternalSize={InternalWidth}x{InternalHeight} Stereo={Stereo} Msaa={EnableMsaa}");
        c.Add<VPRC_SetClears>().Set(ColorF4.Black, null, null);
        // Keep the existing depth buffer so skyboxes/UI behind geometry do not write into velocity.
        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(VelocityFBOName, true, true, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            // GPU path currently ignores override materials; force CPU so motion vectors render with the correct material.
            // Skip background/on-top passes so skyboxes/UI do not pollute the velocity buffer.
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
                    // TransparentForward is omitted: it renders after temporal
                    // accumulation to avoid TAA smearing artifacts.
                });
            c.Add<VPRC_DepthWrite>().Allow = true;
        }
        // Restore clears for subsequent passes to the pipeline defaults.
        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
    }

    /// <summary>Appends the bloom downsample/upsample pass.</summary>
    private void AppendBloomPass(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_BloomPass>().SetTargetFBONames(
            ForwardPassFBOName,
            BloomBlurTextureName,
            Stereo);

    }

    /// <summary>Appends conditional motion blur and depth-of-field sub-chains.</summary>
    private void AppendMotionBlurAndDoF(ViewportRenderCommandContainer c)
    {
        var motionBlurChoice = c.Add<VPRC_IfElse>();
        motionBlurChoice.ConditionEvaluator = ShouldUseMotionBlur;
        motionBlurChoice.TrueCommands = CreateMotionBlurPassCommands();

        var dofChoice = c.Add<VPRC_IfElse>();
        dofChoice.ConditionEvaluator = ShouldUseDepthOfField;
        dofChoice.TrueCommands = CreateDepthOfFieldPassCommands();
    }

    /// <summary>Appends temporal accumulation (TAA/TSR resolve) and pops the jitter offset.</summary>
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

        // Pop jitter so transparent / masked forward passes render with a
        // clean (unjittered) projection. This prevents sub-pixel jitter from
        // shifting alpha-test / blend boundaries that cause TAA smearing.
        c.Add<VPRC_TemporalAccumulationPass>().Phase =
            VPRC_TemporalAccumulationPass.EPhase.PopJitter;
    }

    /// <summary>Renders transparent and on-top forward passes after temporal resolve (unjittered).</summary>
    private void AppendPostTemporalForwardPasses(ViewportRenderCommandContainer c)
    {
        // Render transparent and on-top forward passes AFTER temporal resolve.
        // They composite on top of the resolved opaque image without temporal
        // accumulation, avoiding ghosting/smearing on transparent edges.
        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardPassFBOName, true, false, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GPURenderDispatch);
            c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GPURenderDispatch);
        }

    }

    /// <summary>Caches the post-process FBO (depends on BloomBlurTexture from the bloom pass).</summary>
    private void AppendPostProcessResourceCaching(ViewportRenderCommandContainer c)
    {
        //PostProcess FBO
        //This FBO is created here because it relies on BloomBlurTextureName, which is created in the BloomPass.
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            PostProcessFBOName,
            CreatePostProcessFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

    }

    /// <summary>Caches debug visualization FBOs (transform ID, transparency, overdraw, depth peeling).</summary>
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

    /// <summary>Caches post-AA and TSR support resources used by the default pipeline.</summary>
    private void AppendAntiAliasingResourceCaching(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            FxaaOutputTextureName,
            CreateFxaaOutputTexture,
            NeedsRecreateOutputTextureFullSize,
            ResizeTextureFullSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            PostProcessOutputFBOName,
            CreatePostProcessOutputFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateFboDueToOutputFormat);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            FxaaFBOName,
            CreateFxaaFBO,
            GetDesiredFBOSizeFull,
            NeedsRecreateFboDueToOutputFormat);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TsrHistoryColorTextureName,
            CreateTsrHistoryColorTexture,
            NeedsRecreateOutputTextureFullSize,
            ResizeTextureFullSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TsrHistoryColorFBOName,
            CreateTsrHistoryColorFBO,
            GetDesiredFBOSizeFull,
            NeedsRecreateFboDueToOutputFormat);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            TsrUpscaleFBOName,
            CreateTsrUpscaleFBO,
            GetDesiredFBOSizeFull,
            NeedsRecreateFboDueToOutputFormat);

        //c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
        //    UserInterfaceFBOName,
        //    CreateUserInterfaceFBO,
        //    GetDesiredFBOSizeInternal);
    }

    /// <summary>Appends the FXAA/SMAA/TSR post-AA chain.</summary>
    private void AppendFxaaTsrUpscaleChain(ViewportRenderCommandContainer c)
    {
        // Post-AA chain: FXAA and SMAA run against the post-process output, while TSR
        // resolves from internal resolution and writes a full-resolution result.
        {
            var upscaleChoice = c.Add<VPRC_IfElse>();
            upscaleChoice.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableSmaa || RuntimeNeedsTsrUpscale;
            {
                var upscaleCmds = new ViewportRenderCommandContainer(this);

                // First pass: PostProcess quad renders to PostProcessOutputTexture at internal resolution
                using (upscaleCmds.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
                {
                    upscaleCmds.Add<VPRC_RenderQuadToFBO>().SetTargets(PostProcessFBOName, PostProcessOutputFBOName);
                }

                // Second pass: apply the selected anti-aliasing path.
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

        // Auto exposure uses a GPU compute shader dispatch. Schedule it before the
    }

    /// <summary>Dispatches the auto-exposure compute shader before final output.</summary>
    private void AppendExposureUpdate(ViewportRenderCommandContainer c)
    {
        // force a LoadOp.Clear restart of) the swapchain render pass.
        // The HDR scene buffer is already fully rendered at this point, so reading
        // it here produces the same result as reading it after the output blit.
        string exposureSource = HDRSceneTextureName;
        c.Add<VPRC_ExposureUpdate>().SetOptions(exposureSource, true);
    }

    /// <summary>Commits temporal accumulation state (CPU-side bookkeeping).</summary>
    private void AppendTemporalCommit(ViewportRenderCommandContainer c)
    {
        // Temporal commit is CPU-side state bookkeeping only (no GPU ops).
        c.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Commit;
    }

    /// <summary>Binds the output FBO and presents the final composited image (debug viz / vendor upscale / AA).</summary>
    private void AppendFinalOutput(ViewportRenderCommandContainer c, bool bypassVendorUpscale)
    {
        // Final output to screen uses the full viewport region (with panel offset if applicable).
        // All subsequent commands target the swapchain, keeping them in one contiguous
        // Vulkan render pass so a LoadOp.Clear restart cannot wipe the composited scene.
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                //c.Add<VPRC_ClearByBoundFBO>();
                if (EnableTransformIdVisualization)
                {
                    // Debug visualization is produced by a quad shader; present it directly.
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
                        // Env var override takes absolute precedence (debug tooling).
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
                        // Dynamic AA/upscale selection: choose the correct source at render time.
                        // FXAA, SMAA, and TSR each publish a distinct post-AA output FBO; when none
                        // are active, the post-process output goes directly to screen.
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
                        upscaleOutputChoice.FalseCommands = CreateFinalBlitCommands(PostProcessFBOName, bypassVendorUpscale);
                    }
                }
            }
        }
    }


    /// <summary>
    /// Builds the final blit command container that presents the given source FBO to the output.
    /// Used by the FXAA/non-FXAA runtime switch so the output source is resolved per-camera.
    /// </summary>
    private ViewportRenderCommandContainer CreateFinalBlitCommands(string sourceFboName, bool bypassVendorUpscale)
    {
        var cmds = new ViewportRenderCommandContainer(this);
        if (bypassVendorUpscale)
        {
            cmds.Add<VPRC_RenderToWindow>().SourceFBOName = sourceFboName;
        }
        else
        {
            var vendorBlit = cmds.Add<VPRC_VendorUpscale>();
            vendorBlit.FrameBufferName = sourceFboName;
            vendorBlit.DepthTextureName = DepthViewTextureName;
            vendorBlit.MotionTextureName = VelocityTextureName;
        }
        return cmds;
    }

    private string TransparentResolveShaderName()
        => Stereo ? "TransparentResolveStereo.fs" : "TransparentResolve.fs";

    private string TransparentAccumulationDebugShaderName()
        => Stereo ? "TransparentAccumulationDebugStereo.fs" : "TransparentAccumulationDebug.fs";

    private string TransparentRevealageDebugShaderName()
        => Stereo ? "TransparentRevealageDebugStereo.fs" : "TransparentRevealageDebug.fs";

    private string TransparentOverdrawDebugShaderName()
        => Stereo ? "TransparentOverdrawDebugStereo.fs" : "TransparentOverdrawDebug.fs";

    private ViewportRenderCommandContainer CreateVendorUpscaleCommands(string sourceFboName)
    {
        var c = new ViewportRenderCommandContainer(this);
        var vendorBlit = c.Add<VPRC_VendorUpscale>();
        vendorBlit.FrameBufferName = sourceFboName;
        vendorBlit.DepthTextureName = DepthViewTextureName;
        vendorBlit.MotionTextureName = VelocityTextureName;
        return c;
    }


    private void CacheTextures(ViewportRenderCommandContainer c)
    {
        CacheGBufferTextures(c);
        CacheMsaaDeferredTextures(c);
        CacheLightingTextures(c);
        CacheTemporalTextures(c);
        CachePostProcessTextures(c);
        CacheTransparencyTextures(c);
        CacheGITextures(c);
    }

    /// <summary>Caches GBuffer textures: BRDF, depth/stencil, normals, albedo, RMSE, transform ID, and their views.</summary>
    private void CacheGBufferTextures(ViewportRenderCommandContainer c)
    {
        //BRDF, for PBR lighting
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            BRDFTextureName,
            CreateBRDFTexture,
            null,
            null);

        //Depth + Stencil GBuffer texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthStencilTextureName,
            CreateDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPrePassDepthStencilTextureName,
            CreateForwardPrePassDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPassMsaaDepthStencilTextureName,
            CreateForwardPassMsaaDepthStencilTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPassMsaaDepthViewTextureName,
            CreateForwardPassMsaaDepthViewTexture,
            t => NeedsRecreateTextureView(t, ForwardPassMsaaDepthStencilTextureName),
            ResizeTextureInternalSize);

        //Depth view texture
        //This is a view of the depth/stencil texture that only shows the depth values.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthViewTextureName,
            CreateDepthViewTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //Stencil view texture
        //This is a view of the depth/stencil texture that only shows the stencil values.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            StencilViewTextureName,
            CreateStencilViewTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //History depth + view textures
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryDepthStencilTextureName,
            CreateHistoryDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryDepthViewTextureName,
            CreateHistoryDepthViewTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //Albedo/Opacity GBuffer texture
        //RGB = Albedo, A = Opacity
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AlbedoOpacityTextureName,
            CreateAlbedoOpacityTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //Normal GBuffer texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            NormalTextureName,
            CreateNormalTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPrePassNormalTextureName,
            CreateForwardPrePassNormalTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //RMSI GBuffer texture
        //R = Roughness, G = Metallic, B = Specular, A = IOR
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            RMSETextureName,
            CreateRMSETexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        //Transform ID GBuffer texture
        //R32UI = per-draw/per-transform identifier
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TransformIdTextureName,
            CreateTransformIdTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        // TransformId visualization is rendered directly via a debug quad.
    }

    /// <summary>Caches MSAA deferred GBuffer textures (always cached so per-camera AA overrides work at runtime).</summary>
    private void CacheMsaaDeferredTextures(ViewportRenderCommandContainer c)
    {
        // MSAA deferred GBuffer textures (always cached so per-camera AA overrides work at runtime)
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaAlbedoOpacityTextureName,
            CreateMsaaAlbedoOpacityTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaNormalTextureName,
            CreateMsaaNormalTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaRMSETextureName,
            CreateMsaaRMSETexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaDepthStencilTextureName,
            CreateMsaaDepthStencilTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaDepthViewTextureName,
            CreateMsaaDepthViewTexture,
            t => NeedsRecreateTextureView(t, MsaaDepthStencilTextureName),
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaTransformIdTextureName,
            CreateMsaaTransformIdTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MsaaLightingTextureName,
            CreateMsaaLightingTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);

        //SSAO FBO texture, this is created later by the SSAO command
        //c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
        //    AmbientOcclusionIntensityTextureName,
        //    CreateSSAOTexture,
        //    NeedsRecreateTextureInternalSize,
        //    ResizeTextureInternalSize);
    }

    /// <summary>Caches the deferred lighting (diffuse) texture.</summary>
    private void CacheLightingTextures(ViewportRenderCommandContainer c)
    {
        //Lighting texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DiffuseTextureName,
            CreateLightingTexture,
            t =>
                NeedsRecreateTextureInternalSize(t) ||
                t is not IFrameBufferAttachement ||
                (Stereo ? t is not XRTexture2DArray : t is not XRTexture2D),
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VelocityTextureName,
            CreateVelocityTexture,
            t =>
                NeedsRecreateTextureInternalSize(t) ||
                t is not IFrameBufferAttachement ||
                (Stereo ? t is not XRTexture2DArray : t is not XRTexture2D),
            ResizeTextureInternalSize);
    }

    /// <summary>Caches temporal accumulation textures: history color, temporal input, exposure variance.</summary>
    private void CacheTemporalTextures(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryColorTextureName,
            CreateHistoryColorTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TemporalColorInputTextureName,
            CreateTemporalColorInputTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TemporalExposureVarianceTextureName,
            CreateTemporalExposureVarianceTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryExposureVarianceTextureName,
            CreateHistoryExposureVarianceTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);
    }

    /// <summary>Caches post-process output, FXAA, HDR scene, motion blur, DoF, and auto-exposure textures.</summary>
    private void CachePostProcessTextures(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            MotionBlurTextureName,
            CreateMotionBlurTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthOfFieldTextureName,
            CreateDepthOfFieldTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        // PostProcessOutput is the intermediate target used by the post-process quad before
        // any optional AA/upscale pass. It must exist regardless of the selected AA mode
        // because the matching FBO is created unconditionally later in the command chain.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            PostProcessOutputTextureName,
            CreatePostProcessOutputTexture,
            NeedsRecreateOutputTextureInternalSize,
            ResizeTextureInternalSize);

        if (EnableFxaa)
        {
            // FXAA output is full resolution (FXAA performs the upscale)
            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                FxaaOutputTextureName,
                CreateFxaaOutputTexture,
                NeedsRecreateOutputTextureFullSize,
                ResizeTextureFullSize);
        }

        //HDR Scene texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HDRSceneTextureName,
            CreateHDRSceneTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        // 1x1 exposure value texture (GPU auto exposure)
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AutoExposureTextureName,
            CreateAutoExposureTexture,
            null,
            null);
    }

    /// <summary>Caches transparency textures (scene copy, WB-OIT accumulation/revealage, exact transparency).</summary>
    private void CacheTransparencyTextures(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TransparentSceneCopyTextureName,
            CreateTransparentSceneCopyTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TransparentAccumTextureName,
            CreateTransparentAccumTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TransparentRevealageTextureName,
            CreateTransparentRevealageTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        CacheExactTransparencyTextures(c);
    }

    /// <summary>Caches GI textures: ReSTIR, light volumes, radiance cascades, surfel GI, and VCT volume.</summary>
    private void CacheGITextures(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            RestirGITextureName,
            CreateRestirGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            LightVolumeGITextureName,
            CreateLightVolumeGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            RadianceCascadeGITextureName,
            CreateRadianceCascadeGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            SurfelGITextureName,
            CreateSurfelGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VoxelConeTracingVolumeTextureName,
            CreateVoxelConeTracingVolumeTexture,
            NeedsRecreateVoxelVolumeTexture,
            ResizeVoxelVolumeTexture);

        //HDR Scene texture 2
        //c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
        //    HDRSceneTexture2Name,
        //    CreateHDRSceneTexture,
        //    NeedsRecreateTextureInternalSize,
        //    ResizeTextureInternalSize);

        //HUD texture
        //c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
        //    UserInterfaceTextureName,
        //    CreateHUDTexture,
        //    NeedsRecreateTextureFullSize,
        //    ResizeTextureFullSize);
    }


    private ViewportRenderCommandContainer CreateSSAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureSSAOPass(container.Add<VPRC_SSAOPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateAmbientOcclusionDisabledPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureAmbientOcclusionDisabledPass(container.Add<VPRC_AODisabledPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateAmbientOcclusionResolveCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionBlurFBOName, GBufferFBOName);
        return container;
    }

    private ViewportRenderCommandContainer CreateHBAOPlusResolveCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionBlurFBOName, HBAOPlusBlurIntermediateFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(HBAOPlusBlurIntermediateFBOName, GBufferFBOName);
        return container;
    }

    private ViewportRenderCommandContainer CreateHBAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureHBAOPass(container.Add<VPRC_AODisabledPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateHBAOPlusPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureHBAOPlusPass(container.Add<VPRC_HBAOPlusPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateGTAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureGTAOPass(container.Add<VPRC_GTAOPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateGTAOResolveCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionBlurFBOName, GTAOBlurIntermediateFBOName);
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(GTAOBlurIntermediateFBOName, GBufferFBOName);
        return container;
    }

    private ViewportRenderCommandContainer CreateVXAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureVXAOPass(container.Add<VPRC_AODisabledPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateMVAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureMVAOPass(container.Add<VPRC_MVAOPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateMSVOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureMSVOPass(container.Add<VPRC_MSVO>());
        return container;
    }

    private ViewportRenderCommandContainer CreateSpatialHashAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureSpatialHashAOPass(container.Add<VPRC_SpatialHashAOPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateMotionBlurPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this);

        container.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            MotionBlurCopyFBOName,
            CreateMotionBlurCopyFBO,
            GetDesiredFBOSizeInternal);

        container.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            MotionBlurFBOName,
            CreateMotionBlurFBO,
            GetDesiredFBOSizeInternal);

        container.Add<VPRC_BlitFrameBuffer>().SetOptions(
            ForwardPassFBOName,
            MotionBlurCopyFBOName,
            EReadBufferMode.ColorAttachment0,
            blitColor: true,
            blitDepth: false,
            blitStencil: false,
            linearFilter: false);

        // Render the motion blur result back into the forward pass FBO
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(MotionBlurFBOName, ForwardPassFBOName);

        return container;
    }

    /// <summary>
    /// Creates the forward pre-pass commands that render into both a dedicated
    /// forward-only FBO and into the shared GBuffer attachments (separate + merge).
    /// </summary>
    private ViewportRenderCommandContainer CreateForwardPrePassSeparateCommands()
    {
        var c = new ViewportRenderCommandContainer(this);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardDepthPrePassFBOName,
            CreateForwardDepthPrePassFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardDepthPrePassMergeFBOName,
            CreateForwardDepthPrePassMergeFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardDepthPrePassFBOName)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            c.Add<VPRC_ForwardDepthNormalPrePass>().SetOptions(
                [(int)EDefaultRenderPass.OpaqueForward, (int)EDefaultRenderPass.MaskedForward],
                GPURenderDispatch);
        }

        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardDepthPrePassMergeFBOName, true, false, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            c.Add<VPRC_ForwardDepthNormalPrePass>().SetOptions(
                [(int)EDefaultRenderPass.OpaqueForward, (int)EDefaultRenderPass.MaskedForward],
                GPURenderDispatch);
        }

        return c;
    }

    /// <summary>
    /// Creates the forward pre-pass commands that render directly into the GBuffer
    /// normal and depth attachments, skipping the dedicated forward-only FBO.
    /// </summary>
    private ViewportRenderCommandContainer CreateForwardPrePassSharedCommands()
    {
        var c = new ViewportRenderCommandContainer(this);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardDepthPrePassMergeFBOName,
            CreateForwardDepthPrePassMergeFBO,
            GetDesiredFBOSizeInternal)
            .UseLifetime(RenderResourceLifetime.Transient);

        using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(ForwardDepthPrePassMergeFBOName, true, false, false, false)))
        {
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            c.Add<VPRC_ForwardDepthNormalPrePass>().SetOptions(
                [(int)EDefaultRenderPass.OpaqueForward, (int)EDefaultRenderPass.MaskedForward],
                GPURenderDispatch);
        }

        return c;
    }

    private ViewportRenderCommandContainer CreateDepthOfFieldPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this);

        container.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DepthOfFieldCopyFBOName,
            CreateDepthOfFieldCopyFBO,
            GetDesiredFBOSizeInternal);

        container.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DepthOfFieldFBOName,
            CreateDepthOfFieldFBO,
            GetDesiredFBOSizeInternal);

        container.Add<VPRC_BlitFrameBuffer>().SetOptions(
            ForwardPassFBOName,
            DepthOfFieldCopyFBOName,
            EReadBufferMode.ColorAttachment0,
            blitColor: true,
            blitDepth: false,
            blitStencil: false,
            linearFilter: false);

        // Render the DoF result back into the forward pass FBO
        container.Add<VPRC_RenderQuadToFBO>().SetTargets(DepthOfFieldFBOName, ForwardPassFBOName);

        return container;
    }

    private void ConfigureSSAOPass(VPRC_SSAOPass pass)
    {
        pass.SetOptions(
            VPRC_SSAOPass.DefaultSamples,
            VPRC_SSAOPass.DefaultNoiseWidth,
            VPRC_SSAOPass.DefaultNoiseHeight,
            VPRC_SSAOPass.DefaultMinSampleDist,
            VPRC_SSAOPass.DefaultMaxSampleDist,
            Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName);

        pass.SetOutputNames(
            AmbientOcclusionNoiseTextureName,
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureMVAOPass(VPRC_MVAOPass pass)
    {
        pass.SetOptions(
            VPRC_MVAOPass.DefaultSamples,
            VPRC_MVAOPass.DefaultNoiseWidth,
            VPRC_MVAOPass.DefaultNoiseHeight,
            VPRC_MVAOPass.DefaultMinSampleDist,
            VPRC_MVAOPass.DefaultMaxSampleDist,
            Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName);

        pass.SetOutputNames(
            AmbientOcclusionNoiseTextureName,
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureMSVOPass(VPRC_MSVO pass)
    {
        pass.SetOptions(Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName,
            TransformIdTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureAmbientOcclusionDisabledPass(VPRC_AODisabledPass pass)
    {
        pass.SetOptions(Stereo);
        pass.SetStubInfo(null, null);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName,
            TransformIdTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureHBAOPass(VPRC_AODisabledPass pass)
    {
        ConfigureAmbientOcclusionDisabledPass(pass);
        pass.SetStubInfo(
            "HorizonBased",
            "HorizonBased AO is intentionally deferred in favor of HBAO+. Rendering neutral AO instead of implying that classic HBAO is implemented.");
    }

    private void ConfigureHBAOPlusPass(VPRC_HBAOPlusPass pass)
    {
        pass.SetOptions(Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName,
            TransformIdTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            HBAOPlusBlurIntermediateFBOName,
            GBufferFBOName,
            HBAOPlusRawTextureName,
            HBAOPlusBlurIntermediateTextureName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureGTAOPass(VPRC_GTAOPass pass)
    {
        pass.SetOptions(Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName,
            TransformIdTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GTAOBlurIntermediateFBOName,
            GBufferFBOName,
            GTAORawTextureName,
            GTAOBlurIntermediateTextureName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private void ConfigureVXAOPass(VPRC_AODisabledPass pass)
    {
        ConfigureAmbientOcclusionDisabledPass(pass);
        pass.SetStubInfo(
            "VoxelAmbientOcclusion",
            "VXAO is not implemented yet. This mode is reserved for a future voxelization plus cone-tracing path that will integrate with the existing voxel cone tracing infrastructure.");
    }

    private void ConfigureSpatialHashAOPass(VPRC_SpatialHashAOPass pass)
    {
        pass.SetOptions(
            VPRC_SpatialHashAOPass.DefaultSamples,
            VPRC_SpatialHashAOPass.DefaultNoiseWidth,
            VPRC_SpatialHashAOPass.DefaultNoiseHeight,
            VPRC_SpatialHashAOPass.DefaultMinSampleDist,
            VPRC_SpatialHashAOPass.DefaultMaxSampleDist,
            Stereo);

        pass.SetGBufferInputTextureNames(
            NormalTextureName,
            DepthViewTextureName,
            AlbedoOpacityTextureName,
            RMSETextureName,
            DepthStencilTextureName);

        pass.SetOutputNames(
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
    }

    private int EvaluateAmbientOcclusionMode()
    {
        var aoStage = State.SceneCamera?.GetPostProcessStageState<AmbientOcclusionSettings>();
        if (aoStage?.TryGetBacking(out AmbientOcclusionSettings? aoSettings) == true && aoSettings is not null)
        {
            if (!aoSettings.Enabled)
                return AmbientOcclusionDisabledMode;

            return MapAmbientOcclusionMode(aoSettings.Type);
        }

        return AmbientOcclusionDisabledMode;
    }

    private static int MapAmbientOcclusionMode(AmbientOcclusionSettings.EType type)
        => AmbientOcclusionSettings.NormalizeType(type) switch
        {
            AmbientOcclusionSettings.EType.ScreenSpace => (int)AmbientOcclusionSettings.EType.ScreenSpace,
            AmbientOcclusionSettings.EType.HorizonBased => (int)AmbientOcclusionSettings.EType.HorizonBased,
            AmbientOcclusionSettings.EType.HorizonBasedPlus => (int)AmbientOcclusionSettings.EType.HorizonBasedPlus,
            AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion => (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion,
            AmbientOcclusionSettings.EType.VoxelAmbientOcclusion => (int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion,
            AmbientOcclusionSettings.EType.MultiViewCustom => (int)AmbientOcclusionSettings.EType.MultiViewCustom,
            AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype => (int)AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype,
            AmbientOcclusionSettings.EType.SpatialHashExperimental => (int)AmbientOcclusionSettings.EType.SpatialHashExperimental,
            _ => (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion,
        };

    #endregion
}

