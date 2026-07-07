using System;
using System.IO;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline2
{
    #region Command Chain Generation

    private const string DebugVizTransformIdVariableName = "DebugViz_TransformId";
    private const string DebugVizTransparencyAccumulationVariableName = "DebugViz_TransparencyAccumulation";
    private const string DebugVizTransparencyRevealageVariableName = "DebugViz_TransparencyRevealage";
    private const string DebugVizTransparencyOverdrawVariableName = "DebugViz_TransparencyOverdraw";
    private const string DebugVizFullOverdrawVariableName = "DebugViz_FullOverdraw";
    private const string DebugVizPpllFragmentsVariableName = "DebugViz_PpllFragments";
    private const string DebugVizDepthPeelingLayerVariableName = "DebugViz_DepthPeelingLayer";

    private static void BeginGpuScope(ViewportRenderCommandContainer c, string label)
    {
        c.Add<VPRC_Annotation>().Label = label;
        c.Add<VPRC_GPUTimerBegin>().Label = label;
    }

    private static void EndGpuScope(ViewportRenderCommandContainer c, string label)
        => c.Add<VPRC_GPUTimerEnd>().Label = label;

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
        => State.WindowViewport is not null
        && (RuntimeEngine.Rendering.State.RenderingTargetOutputFBO is null
            || RuntimeEngine.Rendering.State.IsStereoPass);
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

        c.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Begin;
        AppendDebugVisualizationVariables(c);

        BeginGpuScope(c, "Texture Caching");
        CacheTextures(c);
        EndGpuScope(c, "Texture Caching");
        AppendVoxelConeTracingPass(c, enableComputePasses);

        //Create FBOs only after all their texture dependencies have been cached.

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

            // TransparentForward and OnTopForward are rendered AFTER the temporal
            // accumulation resolve (see below) so that sub-pixel jitter does not
            // shift alpha-test / blend boundaries, which causes smearing/ghosting
            // when TAA/TSR tries to blend jittered transparent edges with history.

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
            c.Add<VPRC_DepthTest>().Enable = false;
            if (!Stereo)
            {
                AppendPostProcessCompositeInputDefaults(c);
                AppendAtmosphericScattering(c);
                AppendVolumetricFog(c);
            }
            AppendPostProcessResourceCaching(c);
            AppendDebugVisualizationCaching(c);
            AppendFullOverdrawCountingPass(c);
            AppendAntiAliasingResourceCaching(c);
        }

        // Compute exposure BEFORE the post-process quad so the 1x1 exposure
        // texture and _gpuAutoExposureReadyThisFrame flag are current when
        // PostProcess.fs reads them.
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

    private void AppendDebugVisualizationVariables(ViewportRenderCommandContainer c)
    {
        AddDebugVisualizationVariable(c, DebugVizTransformIdVariableName, () => EnableTransformIdVisualization);
        AddDebugVisualizationVariable(c, DebugVizTransparencyAccumulationVariableName, () => EnableTransparencyAccumulationVisualization);
        AddDebugVisualizationVariable(c, DebugVizTransparencyRevealageVariableName, () => EnableTransparencyRevealageVisualization);
        AddDebugVisualizationVariable(c, DebugVizTransparencyOverdrawVariableName, () => EnableTransparencyOverdrawVisualization);
        AddDebugVisualizationVariable(c, DebugVizFullOverdrawVariableName, () => EnableFullOverdrawVisualization);
        AddDebugVisualizationVariable(c, DebugVizPpllFragmentsVariableName, () => EnablePerPixelLinkedListVisualization);
        AddDebugVisualizationVariable(c, DebugVizDepthPeelingLayerVariableName, () => EnableDepthPeelingLayerVisualization);
    }

    private static void AddDebugVisualizationVariable(ViewportRenderCommandContainer c, string variableName, Func<bool> evaluator)
    {
        var command = c.Add<VPRC_SetVariable>();
        command.VariableName = variableName;
        command.ValueEvaluator = () => evaluator();
    }

    private static bool DebugVariableIsTrue(string variableName)
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Variables.TryGet(variableName, out bool enabled) == true && enabled;

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
        BeginGpuScope(c, "AO Compute");
        if (enableComputePasses)
        {
            // Render to the ambient occlusion FBO using a switch to select the active AO implementation.
            var aoSwitch = c.Add<VPRC_Switch>();
            aoSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
            aoSwitch.Cases = new()
            {
                [(int)AmbientOcclusionSettings.EType.ScreenSpace] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusPassCommands(),
                [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion] = CreateVXAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion] = CreateMVAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance] = CreateMSVOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion] = CreateSpatialHashAOPassCommands(),
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
                [(int)AmbientOcclusionSettings.EType.HorizonBasedPlus] = CreateHBAOPlusPassCommands(),
                [(int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion] = CreateGTAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion] = CreateVXAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance] = CreateSSAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion] = CreateSSAOPassCommands(),
            };
            aoSwitch.DefaultCase = CreateAmbientOcclusionDisabledPassCommands();
        }

        EndGpuScope(c, "AO Compute");
    }

    /// <summary>Caches MSAA deferred FBOs, renders deferred GBuffer geometry, and resolves MSAA when active.</summary>
    private void AppendDeferredGBufferPass(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Deferred GBuffer");
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            DeferredGBufferFBOName,
            CreateDeferredGBufferFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateDeferredGBufferFbo);

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
        // Otherwise renders into the dedicated non-MSAA GBuffer FBO.
        // Always clear color+depth so the GBuffer starts with known values.
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
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, MeshSubmissionStrategy);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DeferredDecals, MeshSubmissionStrategy);
        }
        EndGpuScope(c, "Deferred GBuffer");

        // When MSAA deferred is active, also resolve geometry into the non-MSAA GBuffer FBO
        // so that the AO pass has correct GBuffer data (SSAO doesn't support MSAA textures).
        {
            var msaaGBufferBranch = c.Add<VPRC_IfElse>();
            msaaGBufferBranch.ConditionEvaluator = () => RuntimeEnableMsaaDeferred;
            {
                var msaaGeomCmds = new ViewportRenderCommandContainer(this);
                BeginGpuScope(msaaGeomCmds, "MSAA GBuffer Resolve");
                // Resolve MSAA GBuffer â†’ non-MSAA GBuffer for AO compatibility.
                msaaGeomCmds.Add<VPRC_ResolveMsaaGBuffer>().SetOptions(
                    MsaaGBufferFBOName,
                    DeferredGBufferFBOName,
                    colorAttachmentCount: 4,
                    resolveDepthStencil: true);
                EndGpuScope(msaaGeomCmds, "MSAA GBuffer Resolve");
                msaaGBufferBranch.TrueCommands = msaaGeomCmds;
            }
        }

    }

    /// <summary>Appends the forward depth+normal pre-pass (shared or separate GBuffer targets).</summary>
    private void AppendForwardDepthPrePass(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Forward Pre-Pass");
        var prePassChoice = c.Add<VPRC_IfElse>();
        prePassChoice.ConditionEvaluator = () => !UseOpenXrVulkanDesktopStartupSafePath && ForwardDepthPrePassEnabled;
        {
            // When sharing GBuffer targets, skip the dedicated forward-only FBO
            // and render only into the merged GBuffer attachments.
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

        EndGpuScope(c, "Forward Pre-Pass");
    }

    private void AppendForwardDepthPrePassGBufferRestore(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Forward Pre-Pass GBuffer Restore");
        var restoreChoice = c.Add<VPRC_IfElse>();
        restoreChoice.ConditionEvaluator = () => !UseOpenXrVulkanDesktopStartupSafePath && ForwardDepthPrePassEnabled;
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
        EndGpuScope(c, "Forward Pre-Pass GBuffer Restore");
    }

    /// <summary>Appends the AO blur/resolve switch (HBAO+, GTAO, or default).</summary>
    private void AppendAmbientOcclusionResolve(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "AO Resolve");
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
            EndGpuScope(c, "AO Resolve");
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
        EndGpuScope(c, "AO Resolve");
    }

    /// <summary>Caches LightCombine FBO, marks MSAA complex pixels, and renders the deferred lighting pass.</summary>
    private void AppendLightingPass(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Lighting");
        c.Add<VPRC_SyncLightProbeResources>();

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            LightingAccumFBOName,
            CreateLightingAccumFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateLightingAccumFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        //LightCombine FBO
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            LightCombineFBOName,
            CreateLightCombineFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateLightCombineFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_SetClears>().Set(ColorF4.Black, null, null);

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

        // Render the GBuffer lighting into the accumulation FBO consumed by LightCombine.
        // When MSAA deferred is active, light volumes render into the MSAA Lighting FBO
        // using two-pass (simple + complex with per-sample shading).
        // Otherwise, light volumes render into the standard accumulation FBO.
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
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbePositionBufferName;
                    x.BindingLocation = LightProbePositionBufferBinding;
                }))
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeTetraBufferName;
                    x.BindingLocation = LightProbeTetraBufferBinding;
                }))
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeParamBufferName;
                    x.BindingLocation = LightProbeParamBufferBinding;
                }))
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeGridCellBufferName;
                    x.BindingLocation = LightProbeGridCellBufferBinding;
                }))
                using (msaaLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeGridIndexBufferName;
                    x.BindingLocation = LightProbeGridIndexBufferBinding;
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
                // Resolve MSAA lighting â†’ non-MSAA DiffuseTexture
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
                // Non-MSAA path: render lights into the accumulation FBO consumed by LightCombine.
                var stdLightCmds = new ViewportRenderCommandContainer(this);
                using (stdLightCmds.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(LightingAccumFBOName, clearDepth: false, clearStencil: false)))
                using (stdLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbePositionBufferName;
                    x.BindingLocation = LightProbePositionBufferBinding;
                }))
                using (stdLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeTetraBufferName;
                    x.BindingLocation = LightProbeTetraBufferBinding;
                }))
                using (stdLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeParamBufferName;
                    x.BindingLocation = LightProbeParamBufferBinding;
                }))
                using (stdLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeGridCellBufferName;
                    x.BindingLocation = LightProbeGridCellBufferBinding;
                }))
                using (stdLightCmds.AddUsing<VPRC_BindBuffer>(x =>
                {
                    x.BufferName = LightProbeGridIndexBufferName;
                    x.BindingLocation = LightProbeGridIndexBufferBinding;
                }))
                using (stdLightCmds.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyLightCombineProgramBindings))
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

        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, null, null);
        EndGpuScope(c, "Lighting");
    }

    /// <summary>Caches forward-pass FBOs, renders opaque/masked/GI/debug, and resolves MSAA.</summary>
    private void AppendForwardPass(ViewportRenderCommandContainer c, bool enableComputePasses)
    {
        AddConditionalFboCache(c, "ForwardPassMsaaFBO", () => RuntimeEnableMsaa,
            ForwardPassMsaaFBOName,
            CreateForwardPassMsaaFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateMsaaFbo);
        AddConditionalFboCache(c, "MsaaLightCombineFBO", () => RuntimeEnableMsaaDeferred,
            MsaaLightCombineFBOName,
            CreateMsaaLightCombineFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateMsaaLightCombineFbo);
        AddConditionalFboCache(c, "DepthPreloadFBO", () => RuntimeEnableMsaa && !RuntimeEnableMsaaDeferred,
            DepthPreloadFBOName,
            CreateDepthPreloadFBO,
            GetDesiredFBOSizeInternal);

        // MSAA deferred FBO caching is done earlier (before GBuffer geometry render).

        //ForwardPass FBO
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            ForwardPassFBOName,
            CreateForwardPassFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateForwardPassFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        AddConditionalTransientFboCache(c, "TransparencySceneCopyFBO", () => EnableTransparencySceneCopyResources, SceneCopyFBOName, CreateSceneCopyFBO);
        AddConditionalTransientFboCache(c, "TransparencySceneCopyTextureFBO", () => EnableTransparencySceneCopyResources, TransparentSceneCopyFBOName, CreateTransparentSceneCopyFBO);

        AddConditionalTransientFboCache(c, "WeightedBlendedOitBlurFBO", () => EnableWeightedBlendedOitPasses, DeferredTransparencyBlurFBOName, CreateDeferredTransparencyBlurFBO);
        AddConditionalTransientFboCache(c, "WeightedBlendedOitAccumulationFBO", () => EnableWeightedBlendedOitPasses, TransparentAccumulationFBOName, CreateTransparentAccumulationFBO);
        AddConditionalTransientFboCache(c, "WeightedBlendedOitResolveFBO", () => EnableWeightedBlendedOitPasses, TransparentResolveFBOName, CreateTransparentResolveFBO);

        AddConditionalTransientFboCache(c, "RestirCompositeFBO", () => enableComputePasses && UsesRestirGI, RestirCompositeFBOName, CreateRestirCompositeFBO);
        AddConditionalTransientFboCache(c, "LightVolumeCompositeFBO", () => enableComputePasses && UsesLightVolumes, LightVolumeCompositeFBOName, CreateLightVolumeCompositeFBO);
        AddConditionalTransientFboCache(c, "RadianceCascadeCompositeFBO", () => enableComputePasses && UsesRadianceCascades, RadianceCascadeCompositeFBOName, CreateRadianceCascadeCompositeFBO);
        AddConditionalTransientFboCache(c, "SurfelGICompositeFBO", () => enableComputePasses && UsesSurfelGI, SurfelGICompositeFBOName, CreateSurfelGICompositeFBO);

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
        BeginGpuScope(c, "Forward Render");
        using (c.AddUsing<VPRC_BindFBOByName>(x =>
        {
            x.FrameBufferName = ForwardPassFBOName;
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

            // Present the resolved deferred light buffer for both standard and MSAA modes.
            // The fullscreen per-sample combine path can blank deferred content on some
            // GL drivers, while this resolved path preserves visibility and still lets the
            // skybox refill uncovered MSAA samples afterward.
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbePositionBufferName;
                x.BindingLocation = LightProbePositionBufferBinding;
            }))
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbeTetraBufferName;
                x.BindingLocation = LightProbeTetraBufferBinding;
            }))
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbeParamBufferName;
                x.BindingLocation = LightProbeParamBufferBinding;
            }))
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbeGridCellBufferName;
                x.BindingLocation = LightProbeGridCellBufferBinding;
            }))
            using (c.AddUsing<VPRC_BindBuffer>(x =>
            {
                x.BufferName = LightProbeGridIndexBufferName;
                x.BindingLocation = LightProbeGridIndexBufferBinding;
            }))
            using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyLightCombineProgramBindings))
                c.Add<VPRC_RenderQuadToFBO>()
                    .SetTargets(LightCombineFBOName, ForwardPassFBOName)
                    .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.DeferredLightCombine());
            AppendDiagnosticTextureCapture(c, "05b_LightCombine", DiffuseTextureName);

            //Backgrounds (skybox) should honor the depth buffer but avoid modifying it
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = false;
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, MeshSubmissionStrategy);

            //Enable depth testing and writing for forward passes
            c.Add<VPRC_DepthTest>().Enable = true;
            c.Add<VPRC_DepthWrite>().Allow = true;
            if (enableComputePasses)
                c.Add<VPRC_ForwardPlusLightCullingPass>();
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, MeshSubmissionStrategy);
            c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.MaskedForward, MeshSubmissionStrategy);

            if (enableComputePasses)
            {
                BeginGpuScope(c, "GI Composite");
                using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyRestirCompositeProgramBindings))
                    c.Add<VPRC_ReSTIRPass>();
                using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyLightVolumeCompositeProgramBindings))
                    c.Add<VPRC_LightVolumesPass>();
                using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyRadianceCascadeCompositeProgramBindings))
                    c.Add<VPRC_RadianceCascadesPass>();
                using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplySurfelGICompositeProgramBindings))
                    c.Add<VPRC_SurfelGIPass>();
                EndGpuScope(c, "GI Composite");
            }

            if (enableComputePasses)
                c.Add<VPRC_ForwardPlusDebugOverlay>();

        }
        EndGpuScope(c, "Forward Render");

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

    /// <summary>Appends WB-OIT accumulation/resolve and exact transparency passes.</summary>
    private void AppendTransparencyPasses(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Transparency");

        var weightedOit = c.Add<VPRC_IfElse>();
        weightedOit.Label = "Weighted Blended OIT";
        weightedOit.ConditionEvaluator = () => EnableWeightedBlendedOitPasses;
        {
            ViewportRenderCommandContainer weightedCommands = new(this);
            weightedCommands.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
            weightedCommands.Add<VPRC_RenderQuadToFBO>().SetOptions(DeferredTransparencyBlurFBOName, renderToSourceFrameBuffer: true);
            weightedCommands.Add<VPRC_RenderQuadToFBO>().SetTargets(SceneCopyFBOName, TransparentSceneCopyFBOName);
            weightedCommands.Add<VPRC_ClearTextureByName>().SetOptions(TransparentAccumTextureName, ColorF4.Transparent);
            weightedCommands.Add<VPRC_ClearTextureByName>().SetOptions(TransparentRevealageTextureName, ColorF4.White);
            using (weightedCommands.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(TransparentAccumulationFBOName, true, false, false, false)))
            {
                weightedCommands.Add<VPRC_DepthTest>().Enable = true;
                weightedCommands.Add<VPRC_DepthWrite>().Allow = false;
                weightedCommands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.WeightedBlendedOitForward, MeshSubmissionStrategy);
            }
            using (weightedCommands.AddUsing<VPRC_BindTexture>(x =>
            {
                x.TextureName = TransparentSceneCopyTextureName;
                x.TextureUnit = 0;
            }))
            using (weightedCommands.AddUsing<VPRC_BindTexture>(x =>
            {
                x.TextureName = TransparentAccumTextureName;
                x.TextureUnit = 1;
            }))
            using (weightedCommands.AddUsing<VPRC_BindTexture>(x =>
            {
                x.TextureName = TransparentRevealageTextureName;
                x.TextureUnit = 2;
            }))
            using (weightedCommands.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyTransparentResolveProgramBindings))
            {
                weightedCommands.Add<VPRC_RenderQuadToFBO>().SetOptions(TransparentResolveFBOName, renderToSourceFrameBuffer: true);
            }

            weightedOit.TrueCommands = weightedCommands;
        }

        AppendExactTransparencyCommands(c);
        EndGpuScope(c, "Transparency");
    }

    /// <summary>Renders velocity only when enabled features consume the buffer.</summary>
    private void AppendVelocityPassSwitch(ViewportRenderCommandContainer c)
    {
        var velocityChoice = c.Add<VPRC_IfElse>();
        velocityChoice.Label = "Velocity Buffer";
        velocityChoice.ConditionEvaluator = ShouldGenerateVelocityBuffer;

        ViewportRenderCommandContainer velocityCommands = new(this);
        AppendVelocityPass(velocityCommands);
        velocityChoice.TrueCommands = velocityCommands;
    }

    /// <summary>Caches the velocity FBO, renders motion vectors, and restores default clears.</summary>
    private void AppendVelocityPass(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Velocity");
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
            // OnTopForward gizmos are intentionally excluded: they use custom depth-trick vertex shaders
            // (z forced to near plane) that the engine-generated VS used by the velocity pass does not
            // honor, so velocity would only be written where the gizmo is unoccluded, while motion blur
            // and TAA reprojection would still rely on those values for on-top pixels and produce smears
            // / ghosting. TAA sharpness for gizmos is handled in the TAA/TSR shader via the gizmo stencil bit.
            using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyMotionVectorsProgramBindings))
            {
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
            }
            c.Add<VPRC_DepthWrite>().Allow = true;
        }
        // Restore clears for subsequent passes to the pipeline defaults.
        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        AppendDiagnosticTextureCapture(c, "07_Velocity", VelocityTextureName);
        AppendDiagnosticFboCapture(c, "07b_VelocityFBO", VelocityFBOName);
        EndGpuScope(c, "Velocity");
    }

    /// <summary>Appends the bloom downsample/upsample pass.</summary>
    private void AppendBloomPass(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Bloom");
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

        EndGpuScope(c, "Bloom");
    }

    /// <summary>Appends conditional motion blur and depth-of-field sub-chains.</summary>
    private void AppendMotionBlurAndDoF(ViewportRenderCommandContainer c)
    {
        if (UseOpenXrVulkanDesktopStartupSafePath)
            return;

        BeginGpuScope(c, "Motion Blur / DoF");
        var motionBlurChoice = c.Add<VPRC_IfElse>();
        motionBlurChoice.ConditionEvaluator = ShouldUseMotionBlur;
        motionBlurChoice.TrueCommands = CreateMotionBlurPassCommands();

        var dofChoice = c.Add<VPRC_IfElse>();
        dofChoice.ConditionEvaluator = ShouldUseDepthOfField;
        dofChoice.TrueCommands = CreateDepthOfFieldPassCommands();
        EndGpuScope(c, "Motion Blur / DoF");
    }

    /// <summary>Appends temporal accumulation (TAA/TSR resolve) and pops the jitter offset.</summary>
    private void AppendTemporalAccumulation(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Temporal Accumulation");
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyTemporalAccumulationProgramBindings))
        {
            var temporalAccumulate = c.Add<VPRC_TemporalAccumulationPass>();
            temporalAccumulate.Phase = VPRC_TemporalAccumulationPass.EPhase.Accumulate;
            temporalAccumulate.ConfigureAccumulationTargets(
                ForwardPassFBOName,
                TemporalInputFBOName,
                TemporalAccumulationFBOName,
                HistoryCaptureFBOName,
                HistoryExposureFBOName);
        }

        // Pop jitter so transparent / masked forward passes render with a
        // clean (unjittered) projection. This prevents sub-pixel jitter from
        // shifting alpha-test / blend boundaries that cause TAA smearing.
        c.Add<VPRC_TemporalAccumulationPass>().Phase =
            VPRC_TemporalAccumulationPass.EPhase.PopJitter;
        EndGpuScope(c, "Temporal Accumulation");
    }

    /// <summary>Renders transparent and on-top forward passes after temporal resolve (unjittered).</summary>
    private void AppendPostTemporalForwardPasses(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Post-Temporal Forward");
        // Render transparent and on-top forward passes AFTER temporal resolve.
        // They composite on top of the resolved opaque image without temporal
        // accumulation, avoiding ghosting/smearing on transparent edges.
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

        EndGpuScope(c, "Post-Temporal Forward");
    }

    /// <summary>Caches the post-process FBO (depends on BloomBlurTexture from the bloom pass).</summary>
    private void AppendPostProcessResourceCaching(ViewportRenderCommandContainer c)
    {
        //PostProcess FBO
        //This FBO is created here because it relies on BloomBlurTextureName, which is created in the BloomPass.
        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            PostProcessFBOName,
            CreatePostProcessFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreatePostProcessFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

    }

    private void AppendPostProcessCompositeInputDefaults(ViewportRenderCommandContainer c)
    {
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            AtmosphereColorTextureName,
            CreateAtmosphereColorTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VolumetricFogColorTextureName,
            CreateVolumetricFogColorTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            AtmosphereUpscaleFBOName,
            CreateAtmosphereUpscaleFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateAtmosphereUpscaleFbo);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            VolumetricFogUpscaleFBOName,
            CreateVolumetricFogUpscaleFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateVolumetricFogUpscaleFbo);

        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, null, null);
        using (c.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(AtmosphereUpscaleFBOName, write: true, clearColor: true, clearDepth: false, clearStencil: false)))
        {
        }

        using (c.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(VolumetricFogUpscaleFBOName, write: true, clearColor: true, clearDepth: false, clearStencil: false)))
        {
        }
    }

    /// <summary>Caches debug visualization FBOs (transform ID, transparency, overdraw, depth peeling).</summary>
    private void AppendDebugVisualizationCaching(ViewportRenderCommandContainer c)
    {
        AppendConditionalDebugFboCache(
            c,
            DebugVizTransformIdVariableName,
            TransformIdDebugQuadFBOName,
            CreateTransformIdDebugQuadFBO);

        AppendConditionalDebugFboCache(
            c,
            DebugVizTransparencyAccumulationVariableName,
            TransparentAccumulationDebugFBOName,
            CreateTransparentAccumulationDebugFBO);

        AppendConditionalDebugFboCache(
            c,
            DebugVizTransparencyRevealageVariableName,
            TransparentRevealageDebugFBOName,
            CreateTransparentRevealageDebugFBO);

        AppendConditionalDebugFboCache(
            c,
            DebugVizTransparencyOverdrawVariableName,
            TransparentOverdrawDebugFBOName,
            CreateTransparentOverdrawDebugFBO);

        AppendConditionalDebugFboCache(
            c,
            DebugVizFullOverdrawVariableName,
            FullOverdrawCountFBOName,
            CreateFullOverdrawCountFBO);

        AppendConditionalDebugFboCache(
            c,
            DebugVizFullOverdrawVariableName,
            FullOverdrawDebugFBOName,
            CreateFullOverdrawDebugFBO);
    }

    private void AppendConditionalDebugFboCache(
        ViewportRenderCommandContainer c,
        string variableName,
        string fboName,
        Func<XRFrameBuffer> factory)
    {
        var conditional = c.Add<VPRC_ConditionalRender>();
        conditional.VariableName = variableName;
        conditional.Body = new ViewportRenderCommandContainer(this);
        conditional.Body.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            fboName,
            factory,
            GetDesiredFBOSizeInternal);
    }

    private void AppendFullOverdrawCountingPass(ViewportRenderCommandContainer c)
    {
        var fullOverdraw = c.Add<VPRC_IfElse>();
        fullOverdraw.ConditionEvaluator = () => DebugVariableIsTrue(DebugVizFullOverdrawVariableName);
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

    /// <summary>Caches post-AA and TSR support resources used by the default pipeline.</summary>
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

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            FinalPostProcessOutputTextureName,
            CreateFinalPostProcessOutputTexture,
            NeedsRecreatePostProcessTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            FinalPostProcessFBOName,
            CreateFinalPostProcessFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateFinalPostProcessFbo)
            .UseLifetime(RenderResourceLifetime.Transient);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            FinalPostProcessOutputFBOName,
            CreateFinalPostProcessOutputFBO,
            GetDesiredFBOSizeInternal,
            NeedsRecreateFinalPostProcessOutputFbo);

        c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            FxaaFBOName,
            CreateFxaaFBO,
            GetDesiredFBOSizeFull,
            NeedsRecreateFxaaFbo);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            TsrOutputTextureName,
            CreateTsrOutputTexture,
            NeedsRecreatePostProcessTextureFullSize,
            ResizeTextureFullSize);

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

        //c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
        //    UserInterfaceFBOName,
        //    CreateUserInterfaceFBO,
        //    GetDesiredFBOSizeInternal);
    }

    /// <summary>Appends the FXAA/SMAA/TSR post-AA chain.</summary>
    private void AppendFxaaTsrUpscaleChain(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "AA Upscale");
        // Post-AA chain: FXAA and SMAA run against the post-process output, while TSR
        // resolves from internal resolution and writes a full-resolution result.
        {
            var upscaleChoice = c.Add<VPRC_IfElse>();
            upscaleChoice.ConditionEvaluator = () => RuntimeEnableFxaa || RuntimeEnableSmaa || RuntimeNeedsTsrUpscale;
            {
                var upscaleCmds = new ViewportRenderCommandContainer(this);

                // Apply the selected anti-aliasing path. These passes render to
                // offscreen full-size FBOs, so each command pushes an origin-zero
                // destination render area instead of the window viewport region.
                var tsrOrPostAa = upscaleCmds.Add<VPRC_IfElse>();
                tsrOrPostAa.ConditionEvaluator = () => RuntimeNeedsTsrUpscale;
                {
                    var tsrUpscale = new ViewportRenderCommandContainer(this);
                    using (tsrUpscale.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyTsrUpscaleProgramBindings))
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
        }
        EndGpuScope(c, "AA Upscale");
    }

    /// <summary>Dispatches the auto-exposure compute shader before the post-process pass.</summary>
    private void AppendExposureUpdate(ViewportRenderCommandContainer c)
    {
        // The HDR scene buffer is fully rendered (opaque + transparent + bloom) by the
        // time this runs.  Placing the compute dispatch before the post-process quad lets
        // the 1x1 exposure texture and UseGpuAutoExposure flag be current in the same frame.
        string exposureSource = HDRSceneTextureName;
        c.Add<VPRC_ExposureUpdate>().SetOptions(exposureSource, true);

        // Always materialize the post-process quad into a texture-backed output FBO so
        // final presentation does not depend on the quad-FBO fallback path.
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        using (c.AddUsing<VPRC_PushProgramBindings>(t => t.ApplyUniforms = ApplyPostProcessProgramBindings))
        {
            BeginGpuScope(c, "Post-Processing");
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(PostProcessFBOName, PostProcessOutputFBOName)
                .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.PostProcess());
            EndGpuScope(c, "Post-Processing");
        }
    }

    private void AppendLateDebugOverlay(ViewportRenderCommandContainer c)
    {
        const string lateDebugPassName = "LateDebugOverlay";
        BeginGpuScope(c, "Late Debug Overlay");
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
        EndGpuScope(c, "Late Debug Overlay");
    }

    private void AppendFinalPostProcess(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Final PostProcess");
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyFinalPostProcessProgramBindings))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(FinalPostProcessFBOName, FinalPostProcessOutputFBOName)
                .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.FinalPostProcess());
        }
        EndGpuScope(c, "Final PostProcess");
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
        BeginGpuScope(c, "Final Output");
        var outputChoice = c.Add<VPRC_IfElse>();
        outputChoice.ConditionEvaluator = IsOffscreenSceneCaptureOutput;
        outputChoice.TrueCommands = CreateOffscreenCaptureFinalOutputCommands();
        outputChoice.FalseCommands = CreateViewportFinalOutputCommands(bypassVendorUpscale);
        EndGpuScope(c, "Final Output");
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
                c.Add<VPRC_RenderQuadToFBO>().SetOptions(ForwardPassFBOName);
                AppendDebugOverlay(c);
            }
        }
        return c;
    }

    private ViewportRenderCommandContainer CreateViewportFinalOutputCommands(bool bypassVendorUpscale)
    {
        ViewportRenderCommandContainer c = new(this);
        // Final output to screen uses the full viewport region (with panel offset if applicable).
        // All subsequent commands target the swapchain, keeping them in one contiguous
        // Vulkan render pass so a LoadOp.Clear restart cannot wipe the composited scene.
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                //c.Add<VPRC_ClearByBoundFBO>();
                c.Add<VPRC_ColorMask>().Set(true, true, true, true);
                AppendRuntimeDebugOrStandardFinalOutput(c, bypassVendorUpscale);
            }
        }
        return c;
    }

    private void AppendRuntimeDebugOrStandardFinalOutput(ViewportRenderCommandContainer c, bool bypassVendorUpscale)
    {
        var transformIdChoice = c.Add<VPRC_IfElse>();
        transformIdChoice.ConditionEvaluator = () => DebugVariableIsTrue(DebugVizTransformIdVariableName);
        transformIdChoice.TrueCommands = CreateTransformIdFinalOutputCommands();
        transformIdChoice.FalseCommands = CreateTransparencyDebugSelectionCommands(bypassVendorUpscale);
    }

    private ViewportRenderCommandContainer CreateTransformIdFinalOutputCommands()
    {
        var commands = new ViewportRenderCommandContainer(this);
        using (commands.AddUsing<VPRC_BindTexture>(x =>
        {
            x.TextureName = TransformIdTextureName;
            x.TextureUnit = 0;
        }))
        using (commands.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyTransformIdDebugProgramBindings))
        {
            commands.Add<VPRC_RenderQuadToFBO>().SetTargets(TransformIdDebugQuadFBOName, null);
        }
        AppendDebugOverlay(commands, visible: false);
        return commands;
    }

    private ViewportRenderCommandContainer CreateTransparencyDebugSelectionCommands(bool bypassVendorUpscale)
    {
        var debugOutputs = new[]
        {
            (VariableName: DebugVizFullOverdrawVariableName, FboName: FullOverdrawDebugFBOName),
            (VariableName: DebugVizTransparencyAccumulationVariableName, FboName: TransparentAccumulationDebugFBOName),
            (VariableName: DebugVizTransparencyRevealageVariableName, FboName: TransparentRevealageDebugFBOName),
            (VariableName: DebugVizTransparencyOverdrawVariableName, FboName: TransparentOverdrawDebugFBOName),
            (VariableName: DebugVizPpllFragmentsVariableName, FboName: PpllFragmentCountDebugFBOName),
            (VariableName: DebugVizDepthPeelingLayerVariableName, FboName: DepthPeelingDebugFBOName),
        };

        return CreateTransparencyDebugSelectionCommands(debugOutputs, 0, CreateStandardFinalOutputWithOverlayCommands(bypassVendorUpscale));
    }

    private ViewportRenderCommandContainer CreateTransparencyDebugSelectionCommands(
        (string VariableName, string FboName)[] debugOutputs,
        int index,
        ViewportRenderCommandContainer fallbackCommands)
    {
        if (index >= debugOutputs.Length)
            return fallbackCommands;

        var commands = new ViewportRenderCommandContainer(this);
        var choice = commands.Add<VPRC_IfElse>();
        string variableName = debugOutputs[index].VariableName;
        string fboName = debugOutputs[index].FboName;
        choice.ConditionEvaluator = () => DebugVariableIsTrue(variableName);
        choice.TrueCommands = CreateTransparencyDebugFinalOutputCommands(fboName);
        choice.FalseCommands = CreateTransparencyDebugSelectionCommands(debugOutputs, index + 1, fallbackCommands);
        return commands;
    }

    private ViewportRenderCommandContainer CreateTransparencyDebugFinalOutputCommands(string fboName)
    {
        var commands = new ViewportRenderCommandContainer(this);
        AppendTransparencyDebugOutput(commands, fboName);
        AppendDebugOverlay(commands, visible: false);
        return commands;
    }

    private ViewportRenderCommandContainer CreateStandardFinalOutputWithOverlayCommands(bool bypassVendorUpscale)
    {
        var commands = new ViewportRenderCommandContainer(this);
        AppendViewportFinalOutputSourceCommands(commands, bypassVendorUpscale);
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

    private void AppendTransparencyDebugOutput(ViewportRenderCommandContainer c, string fboName)
    {
        switch (fboName)
        {
            case TransparentAccumulationDebugFBOName:
                using (c.AddUsing<VPRC_BindTexture>(x =>
                {
                    x.TextureName = TransparentAccumTextureName;
                    x.TextureUnit = 0;
                }))
                {
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(fboName, null);
                }
                break;
            case TransparentRevealageDebugFBOName:
                using (c.AddUsing<VPRC_BindTexture>(x =>
                {
                    x.TextureName = TransparentRevealageTextureName;
                    x.TextureUnit = 0;
                }))
                {
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(fboName, null);
                }
                break;
            case TransparentOverdrawDebugFBOName:
                using (c.AddUsing<VPRC_BindTexture>(x =>
                {
                    x.TextureName = TransparentRevealageTextureName;
                    x.TextureUnit = 0;
                }))
                using (c.AddUsing<VPRC_BindTexture>(x =>
                {
                    x.TextureName = TransparentAccumTextureName;
                    x.TextureUnit = 1;
                }))
                {
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(fboName, null);
                }
                break;
            case FullOverdrawDebugFBOName:
                using (c.AddUsing<VPRC_BindTexture>(x =>
                {
                    x.TextureName = FullOverdrawCountTextureName;
                    x.TextureUnit = 0;
                }))
                using (c.AddUsing<VPRC_BindTexture>(x =>
                {
                    x.TextureName = PostProcessOutputTextureName;
                    x.TextureUnit = 1;
                }))
                using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = FullOverdrawDebugFBO_SettingUniforms))
                {
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(fboName, null);
                }
                break;
            case PpllFragmentCountDebugFBOName:
                using (c.AddUsing<VPRC_BindTexture>(x =>
                {
                    x.TextureName = PpllFragmentCountTextureName;
                    x.TextureUnit = 0;
                }))
                {
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(fboName, null);
                }
                break;
            case DepthPeelingDebugFBOName:
                using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyDepthPeelingDebugProgramBindings))
                {
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(fboName, null);
                }
                break;
            default:
                c.Add<VPRC_RenderQuadToFBO>().SetTargets(fboName, null);
                break;
        }
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
        capture.OutputFilePath = Path.Combine("Build", "Diagnostics", "FrameCaptures", $"DefaultPipeline2_{label}.png");
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
        capture.OutputFilePath = Path.Combine("Build", "Diagnostics", "FrameCaptures", $"DefaultPipeline2_{label}.png");
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
                $"DefaultRenderPipeline2.InvalidOutputSourceFbo.{sourceFboName}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] XRE_OUTPUT_SOURCE_FBO='{0}' does not resolve to a known FBO. Falling back to standard final output.",
                sourceFboName);
            return false;
        }
        catch (Exception ex)
        {
            Debug.RenderingWarningEvery(
                $"DefaultRenderPipeline2.InvalidOutputSourceFbo.Exception.{sourceFboName}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] XRE_OUTPUT_SOURCE_FBO='{0}' could not be validated before final blit: {1}. Falling back to standard final output.",
                sourceFboName,
                ex.Message);
            return false;
        }
    }


    /// <summary>
    /// Builds the final blit command container that presents the given source FBO to the output.
    /// Used by the FXAA/non-FXAA runtime switch so the output source is resolved per-camera.
    /// </summary>
    private ViewportRenderCommandContainer CreateFinalBlitCommands(string sourceFboName, bool bypassVendorUpscale)
    {
        if (bypassVendorUpscale)
            return CreateDirectWindowPresentCommands(sourceFboName);

        var cmds = new ViewportRenderCommandContainer(this);
        var presentChoice = cmds.Add<VPRC_IfElse>();
        presentChoice.Label = "FinalPresentPath";
        presentChoice.ConditionEvaluator = ShouldUseDirectFinalPresent;
        presentChoice.TrueCommands = CreateDirectWindowPresentCommands(sourceFboName);
        presentChoice.FalseCommands = CreateVendorUpscaleBlitCommands(sourceFboName, false);
        return cmds;
    }

    private ViewportRenderCommandContainer CreateDirectWindowPresentCommands(string sourceFboName)
    {
        var cmds = new ViewportRenderCommandContainer(this);
        var present = cmds.Add<VPRC_RenderToWindow>();
        string? sourceTextureName = ResolveVendorUpscaleSourceTextureName(sourceFboName);
        if (!string.IsNullOrWhiteSpace(sourceTextureName))
            present.SourceTextureName = sourceTextureName;
        else
            present.SourceFBOName = sourceFboName;

        present.FlipSourceYOnVulkan = ShouldFlipVulkanPresentSourceY(sourceFboName);
        return cmds;
    }

    private static bool ShouldUseDirectVulkanFinalPresent()
        => AbstractRenderer.Current is XREngine.Rendering.Vulkan.VulkanRenderer && !RuntimeEnableVendorUpscale;

    private static bool ShouldUseDirectFinalPresent()
        => IsRenderingExternalSwapchainTarget() || ShouldUseDirectVulkanFinalPresent();

    private ViewportRenderCommandContainer CreateVendorUpscaleBlitCommands(string sourceFboName, bool forceFallback)
    {
        var cmds = new ViewportRenderCommandContainer(this);
        var vendorBlit = cmds.Add<VPRC_VendorUpscale>();
        vendorBlit.FrameBufferName = sourceFboName;
        vendorBlit.SourceTextureName = ResolveVendorUpscaleSourceTextureName(sourceFboName);
        vendorBlit.DepthTextureName = DepthViewTextureName;
        vendorBlit.DepthStencilTextureName = DepthStencilTextureName;
        vendorBlit.MotionTextureName = VelocityTextureName;
        vendorBlit.MotionFrameBufferName = VelocityFBOName;
        vendorBlit.ForceFallbackBlit = forceFallback;
        vendorBlit.FlipSourceYOnVulkanFallback = ShouldFlipVulkanPresentSourceY(sourceFboName);
        return cmds;
    }

    private static bool ShouldFlipVulkanPresentSourceY(string sourceFboName)
        => sourceFboName is not (FxaaFBOName or SmaaFBOName);

    private static string? ResolveVendorUpscaleSourceTextureName(string sourceFboName)
        => sourceFboName switch
        {
            PostProcessOutputFBOName => PostProcessOutputTextureName,
            FinalPostProcessOutputFBOName => FinalPostProcessOutputTextureName,
            FxaaFBOName => FxaaOutputTextureName,
            SmaaFBOName => SmaaOutputTextureName,
            TsrUpscaleFBOName => TsrOutputTextureName,
            _ => null,
        };

    private string TransparentResolveShaderName()
        => Stereo ? "TransparentResolveStereo.fs" : "TransparentResolve.fs";

    private string TransparentAccumulationDebugShaderName()
        => Stereo ? "TransparentAccumulationDebugStereo.fs" : "TransparentAccumulationDebug.fs";

    private string TransparentRevealageDebugShaderName()
        => Stereo ? "TransparentRevealageDebugStereo.fs" : "TransparentRevealageDebug.fs";

    private string TransparentOverdrawDebugShaderName()
        => Stereo ? "TransparentOverdrawDebugStereo.fs" : "TransparentOverdrawDebug.fs";

    private ViewportRenderCommandContainer CreateVendorUpscaleCommands(string sourceFboName)
        => CreateVendorUpscaleBlitCommands(sourceFboName, false);


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
            NeedsRecreateTextureForwardDepthNormalPrePassSize,
            ResizeTextureForwardDepthNormalPrePassSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardContactDepthStencilTextureName,
            CreateForwardContactDepthStencilTexture,
            NeedsRecreateTextureForwardDepthNormalPrePassSize,
            ResizeTextureForwardDepthNormalPrePassSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DeferredGBufferPreForwardDepthStencilTextureName,
            CreateDeferredGBufferPreForwardDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardContactDepthViewTextureName,
            CreateForwardContactDepthViewTexture,
            t => NeedsRecreateTextureView(t, ForwardContactDepthStencilTextureName),
            t => RetargetTextureView(t, ForwardContactDepthStencilTextureName));

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPassMsaaDepthStencilTextureName,
            CreateForwardPassMsaaDepthStencilTexture,
            NeedsRecreateMsaaTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardPassMsaaDepthViewTextureName,
            CreateForwardPassMsaaDepthViewTexture,
            t => NeedsRecreateTextureView(t, ForwardPassMsaaDepthStencilTextureName),
            t => RetargetTextureView(t, ForwardPassMsaaDepthStencilTextureName));

        //Depth view texture
        //This is a view of the depth/stencil texture that only shows the depth values.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DepthViewTextureName,
            CreateDepthViewTexture,
            t => NeedsRecreateTextureView(t, DepthStencilTextureName),
            t => RetargetTextureView(t, DepthStencilTextureName));

        //Stencil view texture
        //This is a view of the depth/stencil texture that only shows the stencil values.
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            StencilViewTextureName,
            CreateStencilViewTexture,
            t => NeedsRecreateTextureView(t, DepthStencilTextureName),
            t => RetargetTextureView(t, DepthStencilTextureName));

        //History depth + view textures
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryDepthStencilTextureName,
            CreateHistoryDepthStencilTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HistoryDepthViewTextureName,
            CreateHistoryDepthViewTexture,
            t => NeedsRecreateTextureView(t, HistoryDepthStencilTextureName),
            t => RetargetTextureView(t, HistoryDepthStencilTextureName));

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
            NeedsRecreateTextureForwardDepthNormalPrePassSize,
            ResizeTextureForwardDepthNormalPrePassSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            ForwardContactNormalTextureName,
            CreateForwardContactNormalTexture,
            NeedsRecreateTextureForwardDepthNormalPrePassSize,
            ResizeTextureForwardDepthNormalPrePassSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            DeferredGBufferPreForwardNormalTextureName,
            CreateDeferredGBufferPreForwardNormalTexture,
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
            t => RetargetTextureView(t, MsaaDepthStencilTextureName));
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
            LightingAccumTextureName,
            CreateLightingAccumTexture,
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
        AddConditionalTextureCache(
            c,
            "MotionBlurTexture",
            ShouldUseMotionBlur,
            MotionBlurTextureName,
            CreateMotionBlurTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        AddConditionalTextureCache(
            c,
            "DepthOfFieldTexture",
            ShouldUseDepthOfField,
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
            NeedsRecreatePostProcessTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            FullOverdrawCountTextureName,
            CreateFullOverdrawCountTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        if (EnableFxaa)
        {
            // FXAA output is full resolution (FXAA performs the upscale)
            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                FxaaOutputTextureName,
                CreateFxaaOutputTexture,
                NeedsRecreatePostProcessTextureFullSize,
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
        AddConditionalTextureCache(
            c,
            "TransparencySceneCopyTexture",
            () => EnableTransparencySceneCopyResources,
            TransparentSceneCopyTextureName,
            CreateTransparentSceneCopyTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        AddConditionalTextureCache(
            c,
            "WeightedBlendedOitAccumTexture",
            () => EnableWeightedBlendedOitPasses,
            TransparentAccumTextureName,
            CreateTransparentAccumTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        AddConditionalTextureCache(
            c,
            "WeightedBlendedOitRevealageTexture",
            () => EnableWeightedBlendedOitPasses,
            TransparentRevealageTextureName,
            CreateTransparentRevealageTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        CacheExactTransparencyTextures(c);
    }

    /// <summary>Caches GI textures: ReSTIR, light volumes, radiance cascades, surfel GI, and VCT volume.</summary>
    private void CacheGITextures(ViewportRenderCommandContainer c)
    {
        AddConditionalTextureCache(
            c,
            "RestirGITexture",
            () => UsesRestirGI,
            RestirGITextureName,
            CreateRestirGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        AddConditionalTextureCache(
            c,
            "LightVolumeGITexture",
            () => UsesLightVolumes,
            LightVolumeGITextureName,
            CreateLightVolumeGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        AddConditionalTextureCache(
            c,
            "RadianceCascadeGITexture",
            () => UsesRadianceCascades,
            RadianceCascadeGITextureName,
            CreateRadianceCascadeGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        AddConditionalTextureCache(
            c,
            "SurfelGITexture",
            () => UsesSurfelGI,
            SurfelGITextureName,
            CreateSurfelGITexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        AddConditionalTextureCache(
            c,
            "VoxelConeTracingVolumeTexture",
            () => UsesVoxelConeTracing,
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

    private void AddConditionalTextureCache(
        ViewportRenderCommandContainer c,
        string label,
        Func<bool> condition,
        string name,
        Func<XRTexture> factory,
        Func<XRTexture, bool>? needsRecreate,
        Action<XRTexture>? resize)
    {
        var conditional = c.Add<VPRC_IfElse>();
        conditional.Label = label;
        conditional.ConditionEvaluator = condition;

        var commands = new ViewportRenderCommandContainer(this);
        commands.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            name,
            factory,
            needsRecreate,
            resize);
        conditional.TrueCommands = commands;
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
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionGenerate(AmbientOcclusionRawTextureName));
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionBlurFBOName, GBufferFBOName)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionFinal(AmbientOcclusionRawTextureName, variant: null));
        return container;
    }

    private ViewportRenderCommandContainer CreateAmbientOcclusionDisabledResolveCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName)
            .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantDisabled)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionGenerate(
                AmbientOcclusionIntensityTextureName,
                disabled: true));
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionBlurFBOName, GBufferFBOName)
            .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantDisabled)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionFinal(
                AmbientOcclusionIntensityTextureName,
                DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantDisabled,
                disabled: true));
        return container;
    }

    private ViewportRenderCommandContainer CreateHBAOPlusResolveCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName)
            .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantHBAOPlus)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionGenerate(HBAOPlusRawTextureName));
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionBlurFBOName, HBAOPlusBlurIntermediateFBOName)
            .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantHBAOPlus)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionIntermediateBlur(
                HBAOPlusRawTextureName,
                HBAOPlusBlurIntermediateTextureName,
                DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantHBAOPlus));
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(HBAOPlusBlurIntermediateFBOName, GBufferFBOName)
            .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantHBAOPlus)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionFinalBlur(
                HBAOPlusBlurIntermediateTextureName,
                HBAOPlusBlurIntermediateFBOName,
                DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantHBAOPlus));
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
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName, matchDestinationRenderArea: true)
            .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantGTAO)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionGenerate(GTAORawTextureName));
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(AmbientOcclusionBlurFBOName, GTAOBlurIntermediateFBOName, matchDestinationRenderArea: true)
            .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantGTAO)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionIntermediateBlur(
                GTAORawTextureName,
                GTAOBlurIntermediateTextureName,
                DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantGTAO));
        container.Add<VPRC_RenderQuadToFBO>()
            .SetTargets(GTAOBlurIntermediateFBOName, GBufferFBOName, matchDestinationRenderArea: true)
            .SetRenderGraphPassVariant(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantGTAO)
            .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.AmbientOcclusionFinalBlur(
                GTAOBlurIntermediateTextureName,
                GTAOBlurIntermediateFBOName,
                DefaultRenderPipelineQuadDescriptors.AmbientOcclusionResolveVariantGTAO));
        return container;
    }

    private ViewportRenderCommandContainer CreateSpatialHashAOResolveCommands()
        => new(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };

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
        using (container.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyMotionBlurProgramBindings))
        {
            container.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(MotionBlurFBOName, ForwardPassFBOName)
                .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.MotionBlur());
        }

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
            GetDesiredFBOSizeForwardDepthNormalPrePass)
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
        using (container.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyDepthOfFieldProgramBindings))
        {
            container.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(DepthOfFieldFBOName, ForwardPassFBOName)
                .SetRenderGraphResources(DefaultRenderPipelineQuadDescriptors.DepthOfField());
        }

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
        if (UseOpenXrVulkanDesktopStartupSafePath)
            return AmbientOcclusionDisabledMode;

        AmbientOcclusionSettings? aoSettings = ResolveAmbientOcclusionSettings();
        if (aoSettings is null || !aoSettings.Enabled)
            return AmbientOcclusionDisabledMode;

        return MapAmbientOcclusionMode(aoSettings.Type);
    }

    private static int MapAmbientOcclusionMode(AmbientOcclusionSettings.EType type)
        => AmbientOcclusionSettings.NormalizeType(type) switch
        {
            AmbientOcclusionSettings.EType.ScreenSpace => (int)AmbientOcclusionSettings.EType.ScreenSpace,
            AmbientOcclusionSettings.EType.HorizonBasedPlus => (int)AmbientOcclusionSettings.EType.HorizonBasedPlus,
            AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion => (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion,
            AmbientOcclusionSettings.EType.VoxelAmbientOcclusion => (int)AmbientOcclusionSettings.EType.VoxelAmbientOcclusion,
            AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion => (int)AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion,
            AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance => (int)AmbientOcclusionSettings.EType.MultiScaleVolumetricObscurance,
            AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion => (int)AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion,
            _ => (int)AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion,
        };

    /// <summary>
    /// Appends the half-resolution atmospheric-scattering chain.
    /// </summary>
    private void AppendAtmosphericScattering(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Atmospheric Scattering");

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

        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyAtmosphereHalfScatterProgramBindings))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(AtmosphereHalfScatterQuadFBOName, AtmosphereHalfScatterFBOName, matchDestinationRenderArea: true);
        }

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
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyAtmosphereReprojectProgramBindings))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(AtmosphereReprojectQuadFBOName, AtmosphereReprojectFBOName, matchDestinationRenderArea: true);
        }

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

        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyAtmosphereUpscaleProgramBindings))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(AtmosphereUpscaleQuadFBOName, AtmosphereUpscaleFBOName, matchDestinationRenderArea: true);
        }

        c.Add<VPRC_BlitFrameBuffer>().SetOptions(
            AtmosphereReprojectFBOName,
            AtmosphereHistoryFBOName,
            EReadBufferMode.ColorAttachment0,
            blitColor: true,
            blitDepth: false,
            blitStencil: false,
            linearFilter: false);

        c.Add<VPRC_AtmosphereHistoryPass>().Phase = VPRC_AtmosphereHistoryPass.EPhase.Commit;
        EndGpuScope(c, "Atmospheric Scattering");
    }

    /// <summary>
    /// Appends the half-resolution volumetric fog chain:
    ///   1. Half-resolution depth downsample (<c>VolumetricFogHalfDepth</c>).
    ///   2. Half-resolution scatter raymarch (<c>VolumetricFogHalfScatter</c>).
    ///   3. Half-resolution temporal reprojection (<c>VolumetricFogHalfTemporal</c>).
    ///   4. Full-resolution bilateral upscale (<c>VolumetricFogColor</c>).
    /// The scatter shader early-outs to (0,0,0,1) when no volumes are present
    /// or the effect is disabled, so the post-process composite degrades to a
    /// no-op without an external gate. Each pass uses
    /// <see cref="VPRC_RenderQuadToFBO.MatchDestinationRenderArea"/> so the
    /// viewport follows the destination FBO's size automatically.
    /// </summary>
    private void AppendVolumetricFog(ViewportRenderCommandContainer c)
    {
        BeginGpuScope(c, "Volumetric Fog");
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

        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyVolumetricFogHalfScatterProgramBindings))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(VolumetricFogHalfScatterQuadFBOName, VolumetricFogHalfScatterFBOName, matchDestinationRenderArea: true);
        }

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
        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyVolumetricFogReprojectProgramBindings))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(VolumetricFogReprojectQuadFBOName, VolumetricFogReprojectFBOName, matchDestinationRenderArea: true);
        }

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

        using (c.AddUsing<VPRC_PushProgramBindings>(x => x.ApplyUniforms = ApplyVolumetricFogUpscaleProgramBindings))
        {
            c.Add<VPRC_RenderQuadToFBO>()
                .SetTargets(VolumetricFogUpscaleQuadFBOName, VolumetricFogUpscaleFBOName, matchDestinationRenderArea: true);
        }

        c.Add<VPRC_BlitFrameBuffer>().SetOptions(
            VolumetricFogReprojectFBOName,
            VolumetricFogHistoryFBOName,
            EReadBufferMode.ColorAttachment0,
            blitColor: true,
            blitDepth: false,
            blitStencil: false,
            linearFilter: false);

        c.Add<VPRC_VolumetricFogHistoryPass>().Phase = VPRC_VolumetricFogHistoryPass.EPhase.Commit;
        EndGpuScope(c, "Volumetric Fog");
    }

    #endregion
}

