using Extensions;
using System.Collections;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline : RenderPipeline
{
    public const string SceneShaderPath = "Scene3D";

    private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
    private readonly FarToNearRenderCommandSorter _farToNearSorter = new();

    //TODO: these options below should not be controlled by this render pipeline object, 
    // but rather in branches in the command chain.

    private readonly Lazy<XRMaterial> _voxelConeTracingVoxelizationMaterial;
    private readonly Lazy<XRMaterial> _motionVectorsMaterial;

    private const float TemporalFeedbackMin = 0.05f;
    private const float TemporalFeedbackMax = 0.95f;
    private const float TemporalVarianceGamma = 1.25f;
    private const float TemporalCatmullRadius = 1.0f;
    private const float TemporalDepthRejectThreshold = 0.0025f;
    private static readonly Vector2 TemporalReactiveTransparencyRange = new(0.05f, 0.35f);
    private const float TemporalReactiveVelocityScale = 0.35f;
    private const float TemporalReactiveLumaThreshold = 0.2f;
    private const float TemporalDepthDiscontinuityScale = 900.0f;
    private const float TemporalConfidencePower = 1.0f;

    private EGlobalIlluminationMode _globalIlluminationMode = EGlobalIlluminationMode.LightProbesAndIbl;
    public EGlobalIlluminationMode GlobalIlluminationMode
    {
        get => _globalIlluminationMode;
        set => SetField(ref _globalIlluminationMode, value);
    }

    public bool UsesRestirGI => _globalIlluminationMode == EGlobalIlluminationMode.Restir;
    public bool UsesVoxelConeTracing => _globalIlluminationMode == EGlobalIlluminationMode.VoxelConeTracing;
    public bool UsesLightVolumes => _globalIlluminationMode == EGlobalIlluminationMode.LightVolumes;
    public bool UsesLightProbeGI => _globalIlluminationMode == EGlobalIlluminationMode.LightProbesAndIbl;
    public bool UsesRadianceCascades => _globalIlluminationMode == EGlobalIlluminationMode.RadianceCascades;
    public bool UsesSurfelGI => _globalIlluminationMode == EGlobalIlluminationMode.SurfelGI;

    // Light probe debug accessors (for editor/state panels)
    public XRTexture2DArray? ProbeIrradianceArray => _probeIrradianceArray;
    public XRTexture2DArray? ProbePrefilterArray => _probePrefilterArray;
    public int ProbeCount => _probePositionBuffer is null ? 0 : (int)_probePositionBuffer.ElementCount;

    protected static bool GPURenderDispatch => Engine.EffectiveSettings.GPURenderDispatch;

    private bool EnableMsaa
        => Engine.Rendering.Settings.AntiAliasingMode == EAntiAliasingMode.Msaa
        && Engine.Rendering.Settings.MsaaSampleCount > 1u;
    private bool EnableFxaa => Engine.Rendering.Settings.AntiAliasingMode == EAntiAliasingMode.Fxaa;
    private uint MsaaSampleCount => Math.Max(1u, Engine.Rendering.Settings.MsaaSampleCount);

    private string BrightPassShaderName() => 
        Stereo ? "BrightPassStereo.fs" : 
        "BrightPass.fs";

    private string HudFBOShaderName() => 
        Stereo ? "HudFBOStereo.fs" : 
        "HudFBO.fs";

    private string PostProcessShaderName() => 
        Stereo ? "PostProcessStereo.fs" : 
        "PostProcess.fs";

    private string DeferredLightCombineShaderName() => 
        Stereo ? "DeferredLightCombineStereo.fs" : 
        "DeferredLightCombine.fs";

    /// <summary>
    /// Affects how textures and FBOs are created for single-pass stereo rendering.
    /// </summary>
    public bool Stereo { get; }

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => new()
        {
            { (int)EDefaultRenderPass.PreRender, null },
            { (int)EDefaultRenderPass.Background, null },
            { (int)EDefaultRenderPass.OpaqueDeferred, _nearToFarSorter },
            { (int)EDefaultRenderPass.DeferredDecals, _farToNearSorter },
            { (int)EDefaultRenderPass.OpaqueForward, _nearToFarSorter },
            { (int)EDefaultRenderPass.TransparentForward, _farToNearSorter },
            { (int)EDefaultRenderPass.OnTopForward, null },
            { (int)EDefaultRenderPass.PostRender, null }
        };

    protected override Lazy<XRMaterial> InvalidMaterialFactory => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);

    private XRMaterial MakeInvalidMaterial() =>
        //Debug.Out("Generating invalid material");
        XRMaterial.CreateColorMaterialDeferred();

    //FBOs
    public const string AmbientOcclusionFBOName = "SSAOFBO";
    public const string AmbientOcclusionBlurFBOName = "SSAOBlurFBO";
    public const string GBufferFBOName = "GBufferFBO";
    public const string LightCombineFBOName = "LightCombineFBO";
    public const string ForwardPassFBOName = "ForwardPassFBO";
    public const string ForwardPassMsaaFBOName = "ForwardPassMSAAFBO";
    public const string PostProcessFBOName = "PostProcessFBO";
    public const string PostProcessOutputTextureName = "PostProcessOutputTexture";
    public const string PostProcessOutputFBOName = "PostProcessOutputFBO";
    public const string FxaaFBOName = "FxaaFBO";
    public const string UserInterfaceFBOName = "UserInterfaceFBO";
    public const string TransformIdDebugQuadFBOName = "TransformIdDebugQuadFBO";
    public const string TransformIdDebugOutputTextureName = "TransformIdDebugOutputTexture";
    public const string TransformIdDebugOutputFBOName = "TransformIdDebugOutputFBO";
    public const string RestirCompositeFBOName = "RestirCompositeFBO";
    public const string LightVolumeCompositeFBOName = "LightVolumeCompositeFBO";
    public const string VelocityFBOName = "VelocityFBO";
    public const string HistoryCaptureFBOName = "HistoryCaptureFBO";
    public const string TemporalInputFBOName = "TemporalInputFBO";
    public const string TemporalAccumulationFBOName = "TemporalAccumulationFBO";
    public const string HistoryExposureFBOName = "HistoryExposureFBO";
    public const string MotionBlurCopyFBOName = "MotionBlurCopyFBO";
    public const string MotionBlurFBOName = "MotionBlurFBO";
    public const string DepthOfFieldCopyFBOName = "DepthOfFieldCopyFBO";
    public const string DepthOfFieldFBOName = "DepthOfFieldFBO";
    public const string DepthPreloadFBOName = "DepthPreloadFBO";
    public const string FxaaOutputTextureName = "FxaaOutputTexture";
    public const string RadianceCascadeCompositeFBOName = "RadianceCascadeCompositeFBO";
    public const string SurfelGICompositeFBOName = "SurfelGICompositeFBO";

    //Textures
    public const string SSAONoiseTextureName = "SSAONoiseTexture";
    public const string AmbientOcclusionIntensityTextureName = "SSAOIntensityTexture";
    public const string NormalTextureName = "Normal";
    public const string DepthViewTextureName = "DepthView";
    public const string StencilViewTextureName = "StencilView";
    public const string AlbedoOpacityTextureName = "AlbedoOpacity";
    public const string RMSETextureName = "RMSE";
    public const string TransformIdTextureName = "TransformId";
    public const string DepthStencilTextureName = "DepthStencil";
    public const string DiffuseTextureName = "LightingTexture";
    public const string HDRSceneTextureName = "HDRSceneTex";
    //public const string HDRSceneTexture2Name = "HDRSceneTex2";
    public const string AutoExposureTextureName = "AutoExposureTex";
    public const string BloomBlurTextureName = "BloomBlurTexture";
    public const string UserInterfaceTextureName = "HUDTex";
    public const string BRDFTextureName = "BRDF";
    public const string RestirGITextureName = "RestirGITexture";
    public const string LightVolumeGITextureName = "LightVolumeGITexture";
    public const string VoxelConeTracingVolumeTextureName = "VoxelConeTracingVolume";
    public const string VelocityTextureName = "Velocity";
    public const string HistoryColorTextureName = "HistoryColor";
    public const string HistoryDepthStencilTextureName = "HistoryDepthStencil";
    public const string HistoryDepthViewTextureName = "HistoryDepth";
    public const string TemporalColorInputTextureName = "TemporalColorInput";
    public const string TemporalExposureVarianceTextureName = "TemporalExposureVariance";
    public const string HistoryExposureVarianceTextureName = "HistoryExposureVariance";
    public const string MotionBlurTextureName = "MotionBlur";
    public const string DepthOfFieldTextureName = "DepthOfField";
    public const string RadianceCascadeGITextureName = "RadianceCascadeGI";
    public const string SurfelGITextureName = "SurfelGITexture";

    private const string TonemappingStageKey = "tonemapping";
    private const string ColorGradingStageKey = "colorGrading";
    private const string BloomStageKey = "bloom";
    private const string AmbientOcclusionStageKey = "ambientOcclusion";
    private const string MotionBlurStageKey = "motionBlur";
    private const string DepthOfFieldStageKey = "depthOfField";
    private const string LensDistortionStageKey = "lensDistortion";
    private const string ChromaticAberrationStageKey = "chromaticAberration";
    private const string FogStageKey = "fog";

    public DefaultRenderPipeline() : this(false)
    {
    }

    public DefaultRenderPipeline(bool stereo = false) : base(true)
    {
        Stereo = stereo;
        GlobalIlluminationMode = Engine.UserSettings.GlobalIlluminationMode;
        _voxelConeTracingVoxelizationMaterial = new Lazy<XRMaterial>(CreateVoxelConeTracingVoxelizationMaterial, LazyThreadSafetyMode.PublicationOnly);
        _motionVectorsMaterial = new Lazy<XRMaterial>(CreateMotionVectorsMaterial, LazyThreadSafetyMode.PublicationOnly);
        Engine.Rendering.SettingsChanged += HandleRenderingSettingsChanged;
        ApplyAntiAliasingResolutionHint();
        CommandChain = GenerateCommandChain();
    }

    private bool EnableTransformIdVisualization
        => !Stereo && Engine.EditorPreferences.Debug.VisualizeTransformId;

    private void HandleRenderingSettingsChanged()
    {
        Engine.InvokeOnMainThread(() =>
        {
            ApplyAntiAliasingResolutionHint();
            CommandChain = GenerateCommandChain();
            foreach (var instance in Instances)
                instance.DestroyCache();
        }, "DefaultRenderPipeline: Rendering settings changed", true);
    }

    private void ApplyAntiAliasingResolutionHint()
    {
        // Avoid fighting other upscalers when DLSS or XeSS is enabled.
        if (Engine.Rendering.Settings.EnableNvidiaDlss || Engine.Rendering.Settings.EnableIntelXess)
        {
            RequestedInternalResolution = null;
            return;
        }

        if (Engine.Rendering.Settings.AntiAliasingMode == EAntiAliasingMode.Tsr)
        {
            RequestedInternalResolution = Math.Clamp(Engine.Rendering.Settings.TsrRenderScale, 0.5f, 1.0f);
        }
        else
        {
            // Null means "use viewport default".
            RequestedInternalResolution = null;
        }
    }

    internal XRMaterial GetVoxelConeTracingVoxelizationMaterial()
        => _voxelConeTracingVoxelizationMaterial.Value;

    internal XRMaterial GetMotionVectorsMaterial()
        => _motionVectorsMaterial.Value;

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

    protected override void DescribeRenderPasses(RenderPassMetadataCollection metadata)
    {
        base.DescribeRenderPasses(metadata);

        static void Chain(RenderPassMetadataCollection collection, EDefaultRenderPass pass, params EDefaultRenderPass[] dependencies)
        {
            var builder = collection.ForPass((int)pass, pass.ToString(), RenderGraphPassStage.Graphics);
            foreach (var dep in dependencies)
                builder.DependsOn((int)dep);
        }

        Chain(metadata, EDefaultRenderPass.PreRender);
        Chain(metadata, EDefaultRenderPass.Background, EDefaultRenderPass.PreRender, EDefaultRenderPass.DeferredDecals);
        Chain(metadata, EDefaultRenderPass.OpaqueDeferred, EDefaultRenderPass.PreRender);
        Chain(metadata, EDefaultRenderPass.DeferredDecals, EDefaultRenderPass.OpaqueDeferred);
        Chain(metadata, EDefaultRenderPass.OpaqueForward, EDefaultRenderPass.Background);
        Chain(metadata, EDefaultRenderPass.TransparentForward, EDefaultRenderPass.OpaqueForward);
        Chain(metadata, EDefaultRenderPass.OnTopForward, EDefaultRenderPass.TransparentForward);
        Chain(metadata, EDefaultRenderPass.PostRender, EDefaultRenderPass.OnTopForward);
    }

    public ViewportRenderCommandContainer CreateFBOTargetCommands()
    {
        ViewportRenderCommandContainer c = new(this);

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
                c.Add<VPRC_ForwardPlusLightCullingPass>();
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GPURenderDispatch);
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

        c.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Begin;

        CacheTextures(c);

        c.Add<VPRC_VoxelConeTracingPass>().SetOptions(VoxelConeTracingVolumeTextureName,
            [
                (int)EDefaultRenderPass.OpaqueDeferred,
                (int)EDefaultRenderPass.OpaqueForward
            ],
            GPURenderDispatch,
            true);
            
        //Create FBOs only after all their texture dependencies have been cached.

        c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PreRender, false);

        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
        {
            // Render to the ambient occlusion FBO using a switch to select SSAO, MVAO, or spatial hash AO
            var aoSwitch = c.Add<VPRC_Switch>();
            aoSwitch.SwitchEvaluator = EvaluateAmbientOcclusionMode;
            aoSwitch.Cases = new()
            {
                [(int)AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion] = CreateMVAOPassCommands(),
                [(int)AmbientOcclusionSettings.EType.SpatialHashRaytraced] = CreateSpatialHashAOPassCommands(),
            };
            aoSwitch.DefaultCase = CreateSSAOPassCommands();

            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(AmbientOcclusionFBOName)))
            {
                c.Add<VPRC_StencilMask>().Set(~0u);
                    c.Add<VPRC_DepthTest>().Enable = true;
                    c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueDeferred, GPURenderDispatch);
                    c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.DeferredDecals, GPURenderDispatch);
            }

            c.Add<VPRC_DepthTest>().Enable = false;
            c.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionFBOName, AmbientOcclusionBlurFBOName);
            c.Add<VPRC_RenderQuadToFBO>().SetTargets(AmbientOcclusionBlurFBOName, GBufferFBOName);

            //LightCombine FBO
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                LightCombineFBOName,
                CreateLightCombineFBO,
                GetDesiredFBOSizeInternal);

            //Render the GBuffer fbo to the LightCombine fbo
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(LightCombineFBOName)))
            {
                c.Add<VPRC_StencilMask>().Set(~0u);
                c.Add<VPRC_LightCombinePass>().SetOptions(
                    AlbedoOpacityTextureName,
                    NormalTextureName,
                    RMSETextureName,
                    DepthViewTextureName);
            }

            if (EnableMsaa)
            {
                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    ForwardPassMsaaFBOName,
                    CreateForwardPassMsaaFBO,
                    GetDesiredFBOSizeInternal);
                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    DepthPreloadFBOName,
                    CreateDepthPreloadFBO,
                    GetDesiredFBOSizeInternal);
            }

            //ForwardPass FBO
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                ForwardPassFBOName,
                CreateForwardPassFBO,
                GetDesiredFBOSizeInternal);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                RestirCompositeFBOName,
                CreateRestirCompositeFBO,
                GetDesiredFBOSizeInternal);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                LightVolumeCompositeFBOName,
                CreateLightVolumeCompositeFBO,
                GetDesiredFBOSizeInternal);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                RadianceCascadeCompositeFBOName,
                CreateRadianceCascadeCompositeFBO,
                GetDesiredFBOSizeInternal);

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                SurfelGICompositeFBOName,
                CreateSurfelGICompositeFBO,
                GetDesiredFBOSizeInternal);

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
            var forwardTargetName = EnableMsaa ? ForwardPassMsaaFBOName : ForwardPassFBOName;
            // When MSAA is enabled, we need to clear the MSAA renderbuffers first since they contain garbage.
            // Clear color to transparent and depth to 1.0 (far plane).
            // Note: Forward meshes won't be occluded by deferred meshes with MSAA, but they'll render correctly otherwise.
            // Always clear stencil each frame so post-process outline is driven only by current-frame writes.
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(forwardTargetName, true, EnableMsaa, EnableMsaa, true)))
            {
                if (EnableMsaa)
                    c.Add<VPRC_RenderQuadToFBO>().SetTargets(DepthPreloadFBOName, ForwardPassMsaaFBOName);

                //Render the deferred pass lighting result, no depth testing
                c.Add<VPRC_DepthTest>().Enable = false;
                c.Add<VPRC_RenderQuadToFBO>().SourceQuadFBOName = LightCombineFBOName;

                //Backgrounds (skybox) should honor the depth buffer but avoid modifying it
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.Background, GPURenderDispatch);

                //Enable depth testing and writing for forward passes
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = true;
                c.Add<VPRC_ForwardPlusLightCullingPass>();
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GPURenderDispatch);

                //c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GPURenderDispatch);

                c.Add<VPRC_ReSTIRPass>();
                c.Add<VPRC_LightVolumesPass>();
                c.Add<VPRC_RadianceCascadesPass>();
                c.Add<VPRC_SurfelGIPass>();

                c.Add<VPRC_RenderDebugShapes>();
                c.Add<VPRC_RenderDebugPhysics>();
            }

            if (EnableMsaa)
            {
                c.Add<VPRC_BlitFrameBuffer>().SetOptions(
                    ForwardPassMsaaFBOName,
                    ForwardPassFBOName,
                    EReadBufferMode.ColorAttachment0,
                    blitColor: true,
                    blitDepth: false,
                    blitStencil: true,
                    linearFilter: false);
            }

            c.Add<VPRC_DepthTest>().Enable = false;

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                VelocityFBOName,
                CreateVelocityFBO,
                GetDesiredFBOSizeInternal);

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
                        (int)EDefaultRenderPass.TransparentForward,
                    });
                c.Add<VPRC_DepthWrite>().Allow = true;
            }
            // Restore clears for subsequent passes to the pipeline defaults.
            c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);

            c.Add<VPRC_DepthTest>().Enable = false;

            c.Add<VPRC_BloomPass>().SetTargetFBONames(
                ForwardPassFBOName,
                BloomBlurTextureName,
                Stereo);

            var motionBlurChoice = c.Add<VPRC_IfElse>();
            motionBlurChoice.ConditionEvaluator = ShouldUseMotionBlur;
            motionBlurChoice.TrueCommands = CreateMotionBlurPassCommands();

            var dofChoice = c.Add<VPRC_IfElse>();
            dofChoice.ConditionEvaluator = ShouldUseDepthOfField;
            dofChoice.TrueCommands = CreateDepthOfFieldPassCommands();

            var temporalAccumulate = c.Add<VPRC_TemporalAccumulationPass>();
            temporalAccumulate.Phase = VPRC_TemporalAccumulationPass.EPhase.Accumulate;
            temporalAccumulate.ConfigureAccumulationTargets(
                ForwardPassFBOName,
                TemporalInputFBOName,
                TemporalAccumulationFBOName,
                HistoryCaptureFBOName,
                HistoryExposureFBOName);

            //PostProcess FBO
            //This FBO is created here because it relies on BloomBlurTextureName, which is created in the BloomPass.
            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                PostProcessFBOName,
                CreatePostProcessFBO,
                GetDesiredFBOSizeInternal);

            if (EnableTransformIdVisualization)
            {
                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    TransformIdDebugQuadFBOName,
                    CreateTransformIdDebugQuadFBO,
                    GetDesiredFBOSizeInternal);
            }

            if (EnableFxaa)
            {
                c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                    FxaaOutputTextureName,
                    CreateFxaaOutputTexture,
                    NeedsRecreateTextureFullSize,
                    ResizeTextureFullSize);

                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    PostProcessOutputFBOName,
                    CreatePostProcessOutputFBO,
                    GetDesiredFBOSizeInternal);

                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    FxaaFBOName,
                    CreateFxaaFBO,
                    GetDesiredFBOSizeFull);
            }

            //c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            //    UserInterfaceFBOName,
            //    CreateUserInterfaceFBO,
            //    GetDesiredFBOSizeInternal);

        }

        // FXAA chain: first blit at internal resolution, then FXAA upscales to full resolution.
        if (EnableFxaa)
        {
            // First pass: PostProcess quad renders to PostProcessOutputTexture at internal resolution
            using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = true))
            {
                c.Add<VPRC_RenderQuadToFBO>().SetTargets(PostProcessFBOName, PostProcessOutputFBOName);
            }

            // Second pass: FXAA reads from internal-res PostProcessOutputTexture and upscales to full-res FxaaOutputTexture
            using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
            {
                c.Add<VPRC_RenderQuadToFBO>().SetTargets(FxaaFBOName, FxaaFBOName);
            }
        }

        // Final output to screen uses the full viewport region (with panel offset if applicable).
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
                else
                {
                    string finalSource = EnableFxaa ? FxaaFBOName : PostProcessFBOName;
                    var vendorBlit = c.Add<VPRC_VendorUpscale>();
                    vendorBlit.FrameBufferName = finalSource;
                    vendorBlit.DepthTextureName = DepthViewTextureName;
                    vendorBlit.MotionTextureName = VelocityTextureName;
                }
            }
        }

        // Auto exposure should be computed from the scene HDR buffer *before* exposure/tonemapping.
        // Sampling a post-processed LDR output (e.g. after tonemapping/FXAA) tends to self-normalize
        // and makes exposure appear to have no effect.
        string exposureSource = HDRSceneTextureName;
        c.Add<VPRC_ExposureUpdate>().SetOptions(exposureSource, true);

        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        c.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Commit;
        c.Add<VPRC_RenderScreenSpaceUI>();
        return c;
    }

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

        //SSAO FBO texture, this is created later by the SSAO command
        //c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
        //    SSAOIntensityTextureName,
        //    CreateSSAOTexture,
        //    NeedsRecreateTextureInternalSize,
        //    ResizeTextureInternalSize);

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

        if (EnableFxaa)
        {
            // PostProcessOutput is intermediate before FXAA - use internal resolution
            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                PostProcessOutputTextureName,
                CreatePostProcessOutputTexture,
                NeedsRecreateTextureInternalSize,
                ResizeTextureInternalSize);

            // FXAA output is full resolution (FXAA performs the upscale)
            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                FxaaOutputTextureName,
                CreateFxaaOutputTexture,
                NeedsRecreateTextureFullSize,
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

    private static void LogAo(string message)
        => Debug.Out(EOutputVerbosity.Normal, false, "[AO][Pipeline] {0}", message);

    private ViewportRenderCommandContainer CreateMVAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this)
        {
            BranchResources = ViewportRenderCommandContainer.BranchResourceBehavior.DisposeResourcesOnBranchExit
        };
        ConfigureMVAOPass(container.Add<VPRC_MVAOPass>());
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
            SSAONoiseTextureName,
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
            SSAONoiseTextureName,
            AmbientOcclusionIntensityTextureName,
            AmbientOcclusionFBOName,
            AmbientOcclusionBlurFBOName,
            GBufferFBOName);
        pass.DependentFboNames = new[] { LightCombineFBOName };
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
        if (aoStage?.TryGetBacking(out AmbientOcclusionSettings? aoSettings) == true && aoSettings is not null && aoSettings.Enabled)
        {
            int result = (int)aoSettings.Type;
            //LogAo($"EvaluateAmbientOcclusionMode -> camera={State.SceneCamera?.GetType().Name ?? "<none>"}, type={aoSettings.Type}");
            return result;
        }

        //LogAo("EvaluateAmbientOcclusionMode -> disabled or missing; defaulting to ScreenSpace");
        return (int)AmbientOcclusionSettings.EType.ScreenSpace;
    }

    #endregion




    #region Setting Uniforms

    private XRTexture2DArray? _probeIrradianceArray;
    private XRTexture2DArray? _probePrefilterArray;
    private XRDataBuffer? _probePositionBuffer;
    private XRDataBuffer? _probeTetraBuffer;
    private XRDataBuffer? _probeParamBuffer;
    private XRDataBuffer? _probeGridCellBuffer;
    private XRDataBuffer? _probeGridIndexBuffer;
    private Vector3 _probeGridOrigin;
    private float _probeGridCellSize;
    private IVector3 _probeGridDims;
    private bool _useProbeGridAcceleration = true;
    private int _lastProbeCount = 0;
    private readonly Dictionary<Guid, Vector3> _cachedProbePositions = new();
    private readonly Dictionary<Guid, (XRTexture2D Irradiance, XRTexture2D Prefilter)> _cachedProbeTextures = new();
    private readonly Dictionary<Guid, uint> _cachedProbeCaptureVersions = new();
    private volatile bool _pendingProbeRefresh;
    private Job? _probeTessellationJob;

    public bool UseProbeGridAcceleration
    {
        get => _useProbeGridAcceleration;
        set => _useProbeGridAcceleration = value;
    }

    private struct ProbePositionData
    {
        public Vector4 Position;
    }

    private struct ProbeParamData
    {
        public Vector4 InfluenceInner;       // xyz inner extents or inner radius
        public Vector4 InfluenceOuter;       // xyz outer extents or outer radius
        public Vector4 InfluenceOffsetShape; // xyz offset, w shape (0 sphere, 1 box)
        public Vector4 ProxyCenterEnable;    // xyz center offset, w enable (1/0)
        public Vector4 ProxyHalfExtents;     // xyz half extents, w normalization scale
        public Vector4 ProxyRotation;        // xyzw quaternion
    }

    private struct ProbeGridCell
    {
        public IVector2 OffsetCount;
    }

    private struct ProbeTetraData
    {
        public Vector4 Indices;
    }

    private void LightCombineFBO_SettingUniforms(XRRenderProgram program)
    {
        if (!UsesLightProbeGI)
            return;

        var world = RenderingWorld;
        if (world is null)
            return;

        IReadOnlyList<LightProbeComponent> probes = world.Lights.LightProbes;
        var readyProbes = GetReadyProbes(probes);
        if (readyProbes.Count == 0)
        {
            ClearProbeResources();
            return;
        }

        if (_pendingProbeRefresh || ProbeConfigurationChanged(readyProbes))
            BuildProbeResources(readyProbes);

        if (_probeIrradianceArray is null || _probePrefilterArray is null || _probePositionBuffer is null || _probeParamBuffer is null)
            return;

        // Use explicit texture units to match the shader's fixed bindings (layout(binding = 7/8)).
        const int irradianceUnit = 7;
        const int prefilterUnit = 8;
        program.Sampler("IrradianceArray", _probeIrradianceArray, irradianceUnit);
        program.Sampler("PrefilterArray", _probePrefilterArray, prefilterUnit);

        int probeCount = (int)_probePositionBuffer.ElementCount;
        program.Uniform("ProbeCount", probeCount);
        _probePositionBuffer.BindTo(program, 0);
        _probeParamBuffer.BindTo(program, 2);
        program.Uniform("UseProbeGrid", _useProbeGridAcceleration && _probeGridCellBuffer is not null && _probeGridIndexBuffer is not null);

        if (_useProbeGridAcceleration && _probeGridCellBuffer is not null && _probeGridIndexBuffer is not null)
        {
            _probeGridCellBuffer.BindTo(program, 3);
            _probeGridIndexBuffer.BindTo(program, 4);
            program.Uniform("ProbeGridOrigin", _probeGridOrigin);
            program.Uniform("ProbeGridCellSize", _probeGridCellSize);
            program.Uniform("ProbeGridDims", _probeGridDims);
        }

        int tetraCount = _probeTetraBuffer != null ? (int)_probeTetraBuffer.ElementCount : 0;
        program.Uniform("TetraCount", tetraCount);
        if (tetraCount > 0)
        {
            _probeTetraBuffer!.BindTo(program, 1);

            if (Engine.EditorPreferences.Debug.RenderLightProbeTetrahedra)
                RenderProbeTetrahedra(readyProbes, tetraCount);
        }
    }

    private void RenderProbeTetrahedra(List<LightProbeComponent> readyProbes, int tetraCount)
    {
        for (uint i = 0; i < tetraCount; ++i)
        {
            var tetraData = _probeTetraBuffer!.GetDataRawAtIndex<ProbeTetraData>(i);
            var indices = tetraData.Indices;
            Vector3 p0 = readyProbes[(int)indices.X].Transform.RenderTranslation;
            Vector3 p1 = readyProbes[(int)indices.Y].Transform.RenderTranslation;
            Vector3 p2 = readyProbes[(int)indices.Z].Transform.RenderTranslation;
            Vector3 p3 = readyProbes[(int)indices.W].Transform.RenderTranslation;
            Engine.Rendering.Debug.RenderLine(p0, p1, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p0, p2, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p0, p3, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p1, p2, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p1, p3, ColorF4.Cyan);
            Engine.Rendering.Debug.RenderLine(p2, p3, ColorF4.Cyan);
        }
    }

    private void BuildProbeGrid(List<ProbePositionData> positions)
    {
        _probeGridCellBuffer?.Dispose();
        _probeGridCellBuffer = null;
        _probeGridIndexBuffer?.Dispose();
        _probeGridIndexBuffer = null;
        _probeGridOrigin = Vector3.Zero;
        _probeGridCellSize = 0f;
        _probeGridDims = IVector3.Zero;

        if (positions.Count == 0)
            return;

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p.Position.XYZ());
            max = Vector3.Max(max, p.Position.XYZ());
        }

        Vector3 extents = max - min;
        float maxExtent = Math.Max(extents.X, Math.Max(extents.Y, extents.Z));
        if (maxExtent <= 0.0001f)
            maxExtent = 1.0f;

        const int targetCellsPerAxis = 16;
        _probeGridCellSize = maxExtent / targetCellsPerAxis;
        _probeGridOrigin = min;
        Vector3 dimsF = extents / _probeGridCellSize + Vector3.One;
        IVector3 dimsI = new(
            Math.Max(1, (int)Math.Ceiling(dimsF.X)),
            Math.Max(1, (int)Math.Ceiling(dimsF.Y)),
            Math.Max(1, (int)Math.Ceiling(dimsF.Z)));
        dimsI = IVector3.Min(dimsI, new IVector3(64, 64, 64));
        _probeGridDims = dimsI;

        int cellCount = dimsI.X * dimsI.Y * dimsI.Z;
        var cellLists = new List<int>[cellCount];
        for (int i = 0; i < cellCount; ++i)
            cellLists[i] = new List<int>(4);

        for (int i = 0; i < positions.Count; ++i)
        {
            Vector4 pos4 = positions[i].Position;
            Vector3 rel = (new Vector3(pos4.X, pos4.Y, pos4.Z) - _probeGridOrigin) / _probeGridCellSize;
            IVector3 cell = new(
                Math.Clamp((int)MathF.Floor(rel.X), 0, dimsI.X - 1),
                Math.Clamp((int)MathF.Floor(rel.Y), 0, dimsI.Y - 1),
                Math.Clamp((int)MathF.Floor(rel.Z), 0, dimsI.Z - 1));
            int flat = cell.X + cell.Y * dimsI.X + cell.Z * dimsI.X * dimsI.Y;
            cellLists[flat].Add(i);
        }

        var offsets = new List<ProbeGridCell>(cellCount);
        var indices = new List<int>();
        for (int c = 0; c < cellCount; ++c)
        {
            var list = cellLists[c];
            int offset = indices.Count;
            indices.AddRange(list);
            offsets.Add(new ProbeGridCell { OffsetCount = new IVector2(offset, list.Count) });
        }

        _probeGridCellBuffer = new XRDataBuffer("LightProbeGridCells", EBufferTarget.ShaderStorageBuffer, (uint)offsets.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeGridCell>(), false, false)
        {
            BindingIndexOverride = 3,
        };
        _probeGridCellBuffer.SetDataRaw(offsets);
        _probeGridCellBuffer.PushData();

        _probeGridIndexBuffer = new XRDataBuffer("LightProbeGridIndices", EBufferTarget.ShaderStorageBuffer, (uint)indices.Count, EComponentType.Int, sizeof(int), false, false)
        {
            BindingIndexOverride = 4,
        };
        _probeGridIndexBuffer.SetDataRaw(indices);
        _probeGridIndexBuffer.PushData();
    }

    private static List<LightProbeComponent> GetReadyProbes(IReadOnlyList<LightProbeComponent> probes)
    {
        var readyProbes = new List<LightProbeComponent>(probes.Count);
        foreach (var probe in probes)
        {
            if (probe.IrradianceTexture != null && probe.PrefilterTexture != null)
                readyProbes.Add(probe);
        }

        return readyProbes;
    }

    private bool ProbeConfigurationChanged(IReadOnlyList<LightProbeComponent> readyProbes)
    {
        if (_lastProbeCount != readyProbes.Count)
        {
            _pendingProbeRefresh = true;
            return true;
        }

        if (_cachedProbePositions.Count != readyProbes.Count || _cachedProbeTextures.Count != readyProbes.Count)
        {
            _pendingProbeRefresh = true;
            return true;
        }

        foreach (var probe in readyProbes)
        {
            var position = probe.Transform.RenderTranslation;
            if (!_cachedProbePositions.TryGetValue(probe.ID, out var cachedPos) || cachedPos != position)
            {
                _pendingProbeRefresh = true;
                return true;
            }

            if (!_cachedProbeTextures.TryGetValue(probe.ID, out var cachedTex)
                || cachedTex.Irradiance != probe.IrradianceTexture
                || cachedTex.Prefilter != probe.PrefilterTexture)
            {
                _pendingProbeRefresh = true;
                return true;
            }

            if (!_cachedProbeCaptureVersions.TryGetValue(probe.ID, out var cachedVersion)
                || cachedVersion != probe.CaptureVersion)
            {
                _pendingProbeRefresh = true;
                return true;
            }
        }

        return false;
    }

    private void ClearProbeResources()
    {
        _probeIrradianceArray?.Destroy();
        _probeIrradianceArray = null;
        _probePrefilterArray?.Destroy();
        _probePrefilterArray = null;
        _probePositionBuffer?.Dispose();
        _probePositionBuffer = null;
        _probeParamBuffer?.Dispose();
        _probeParamBuffer = null;
        _probeTetraBuffer?.Dispose();
        _probeTetraBuffer = null;
        _probeGridCellBuffer?.Dispose();
        _probeGridCellBuffer = null;
        _probeGridIndexBuffer?.Dispose();
        _probeGridIndexBuffer = null;
        _probeGridOrigin = Vector3.Zero;
        _probeGridCellSize = 0f;
        _probeGridDims = IVector3.Zero;
        _probeTessellationJob?.Cancel();
        _probeTessellationJob = null;
        _cachedProbePositions.Clear();
        _cachedProbeTextures.Clear();
        _cachedProbeCaptureVersions.Clear();
        _lastProbeCount = 0;
        _pendingProbeRefresh = false;
    }

    private void BuildProbeResources(IList<LightProbeComponent> readyProbes)
    {
        ClearProbeResources();

        if (readyProbes.Count == 0)
        {
            _pendingProbeRefresh = false;
            return;
        }

        var irrTextures = new List<XRTexture2D>(readyProbes.Count);
        var preTextures = new List<XRTexture2D>(readyProbes.Count);
        var positions = new List<ProbePositionData>(readyProbes.Count);
        var parameters = new List<ProbeParamData>(readyProbes.Count);

        foreach (var probe in readyProbes)
        {
            irrTextures.Add(probe.IrradianceTexture!);
            preTextures.Add(probe.PrefilterTexture!);

            var position = probe.Transform.RenderTranslation;
            positions.Add(new ProbePositionData { Position = new Vector4(position, 1.0f) });

            parameters.Add(new ProbeParamData
            {
                InfluenceInner = new Vector4(probe.InfluenceBoxInnerExtents, probe.InfluenceSphereInnerRadius),
                InfluenceOuter = new Vector4(probe.InfluenceBoxOuterExtents, probe.InfluenceSphereOuterRadius),
                InfluenceOffsetShape = new Vector4(probe.InfluenceOffset, probe.InfluenceShape == LightProbeComponent.EInfluenceShape.Box ? 1.0f : 0.0f),
                ProxyCenterEnable = new Vector4(probe.ProxyBoxCenterOffset, probe.ParallaxCorrectionEnabled ? 1.0f : 0.0f),
                ProxyHalfExtents = new Vector4(probe.ProxyBoxHalfExtents, probe.NormalizationScale),
                ProxyRotation = new Vector4(probe.ProxyBoxRotation.X, probe.ProxyBoxRotation.Y, probe.ProxyBoxRotation.Z, probe.ProxyBoxRotation.W),
            });
            _cachedProbePositions[probe.ID] = position;
            _cachedProbeTextures[probe.ID] = (probe.IrradianceTexture!, probe.PrefilterTexture!);
            _cachedProbeCaptureVersions[probe.ID] = probe.CaptureVersion;
        }

        if (irrTextures.Count == 0 || preTextures.Count == 0)
            return;

        _probeIrradianceArray = new XRTexture2DArray([.. irrTextures])
        {
            Name = "LightProbeIrradianceArray",
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            SizedInternalFormat = ESizedInternalFormat.Rgb8,  // Match irradiance texture format
        };

        _probePrefilterArray = new XRTexture2DArray([.. preTextures])
        {
            Name = "LightProbePrefilterArray",
            MinFilter = ETexMinFilter.LinearMipmapLinear,
            MagFilter = ETexMagFilter.Linear,
            SizedInternalFormat = ESizedInternalFormat.Rgb16f,  // Match prefilter texture format
        };

        _probePositionBuffer = new XRDataBuffer("LightProbePositions", EBufferTarget.ShaderStorageBuffer, (uint)positions.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbePositionData>(), false, false)
        {
            BindingIndexOverride = 0,
        };
        _probePositionBuffer.SetDataRaw<ProbePositionData>(positions);
        _probePositionBuffer.PushData();

        _probeParamBuffer = new XRDataBuffer("LightProbeParameters", EBufferTarget.ShaderStorageBuffer, (uint)parameters.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeParamData>(), false, false)
        {
            BindingIndexOverride = 2,
        };
        _probeParamBuffer.SetDataRaw<ProbeParamData>(parameters);
        _probeParamBuffer.PushData();

        if (_useProbeGridAcceleration)
            BuildProbeGrid(positions);

        _lastProbeCount = positions.Count;
        _pendingProbeRefresh = false;

        StartTetrahedralizationJob(readyProbes);
    }

    private void StartTetrahedralizationJob(IList<LightProbeComponent> probes)
    {
        _probeTessellationJob?.Cancel();
        _probeTessellationJob = Engine.Jobs.Schedule(() => RunTetrahedralization(probes));
    }

    private IEnumerable RunTetrahedralization(IList<LightProbeComponent> probes)
    {
        var probeIndices = new Dictionary<LightProbeComponent, int>(probes.Count);
        for (int i = 0; i < probes.Count; ++i)
            probeIndices[probes[i]] = i;

        // If we don't have enough probes for a tetrahedralization, create a minimal fallback so shaders still have data.
        if (probes.Count is > 0 and < 5)
        {
            UploadTetrahedralization(BuildFallbackTetraData(probeIndices));
            yield break;
        }

        if (!Lights3DCollection.TryCreateDelaunay(probes, out var triangulation))
        {
            Debug.LogWarning("Probe tetrahedralization failed; skipping tetra buffer upload.");
            UploadTetrahedralization([]);
            yield break;
        }

        if (triangulation is null)
        {
            Debug.LogWarning("Probe tetrahedralization returned null data; skipping tetra buffer upload.");
            UploadTetrahedralization([]);
            yield break;
        }

        var cells = triangulation.Cells?.ToList();
        if (cells is null || cells.Count == 0)
        {
            Debug.LogWarning("Probe tetrahedralization produced no cells; skipping tetra buffer upload.");
            UploadTetrahedralization([]);
            yield break;
        }

        var tetraData = new List<ProbeTetraData>(cells.Count);
        foreach (var cell in cells)
        {
            var v = cell.Vertices;
            if (v.Length >= 4)
            {
                tetraData.Add(new ProbeTetraData
                {
                    Indices = new Vector4(
                        probeIndices[v[0]],
                        probeIndices[v[1]],
                        probeIndices[v[2]],
                        probeIndices[v[3]])
                });
            }
        }

        UploadTetrahedralization(tetraData);
        yield break;
    }

    private static List<ProbeTetraData> BuildFallbackTetraData(Dictionary<LightProbeComponent, int> indices)
    {
        int count = indices.Count;
        var list = new List<ProbeTetraData>(1);

        int a = indices.Values.ElementAt(0);
        int b = count >= 2 ? indices.Values.ElementAt(1) : a;
        int c = count >= 3 ? indices.Values.ElementAt(2) : b;
        int d = count >= 4 ? indices.Values.ElementAt(3) : c;

        // Build one degenerate tetra that repeats available probes; shaders can treat this as a single-sample approximation.
        list.Add(new ProbeTetraData
        {
            Indices = new Vector4(a, b, c, d)
        });

        return list;
    }

    private void UploadTetrahedralization(IReadOnlyList<ProbeTetraData> tetraData)
    {
        _probeTetraBuffer?.Dispose();
        if (tetraData.Count == 0)
        {
            _probeTetraBuffer = null;
            return;
        }

        var tetraList = tetraData as IList<ProbeTetraData> ?? [.. tetraData];

        _probeTetraBuffer = new XRDataBuffer("LightProbeTetra", EBufferTarget.ShaderStorageBuffer, (uint)tetraList.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeTetraData>(), false, false)
        {
            BindingIndexOverride = 1,
        };
        _probeTetraBuffer.SetDataRaw(tetraList);
        _probeTetraBuffer.PushData();
    }


    private void RestirCompositeFBO_SettingUniforms(XRRenderProgram program)
    {
    var region = RenderingPipelineState?.CurrentRenderRegion;
        float width = region?.Width > 0 ? region.Value.Width : InternalWidth;
        float height = region?.Height > 0 ? region.Value.Height : InternalHeight;
        program.Uniform("ScreenWidth", width);
        program.Uniform("ScreenHeight", height);
    }

    private void SurfelGICompositeFBO_SettingUniforms(XRRenderProgram program)
    {
        var region = RenderingPipelineState?.CurrentRenderRegion;
        float width = region?.Width > 0 ? region.Value.Width : InternalWidth;
        float height = region?.Height > 0 ? region.Value.Height : InternalHeight;
        program.Uniform("ScreenWidth", width);
        program.Uniform("ScreenHeight", height);
    }

    private void LightVolumeCompositeFBO_SettingUniforms(XRRenderProgram program)
    {
        var region = RenderingPipelineState?.CurrentRenderRegion;
        float width = region?.Width > 0 ? region.Value.Width : InternalWidth;
        float height = region?.Height > 0 ? region.Value.Height : InternalHeight;
        program.Uniform("ScreenWidth", width);
        program.Uniform("ScreenHeight", height);
    }

    #endregion

    #region Highlighting

    /// <summary>
    /// Stencil reference value for hover highlighting (bit 0).
    /// </summary>
    public const int StencilRefHover = 1;

    /// <summary>
    /// Stencil reference value for selection highlighting (bit 1).
    /// </summary>
    public const int StencilRefSelection = 2;

    /// <summary>
    /// This pipeline is set up to use the stencil buffer to highlight objects.
    /// This will highlight the given material.
    /// </summary>
    /// <param name="material">The material to highlight.</param>
    /// <param name="enabled">Whether to enable or disable highlighting.</param>
    /// <param name="isSelection">If true, uses the selection stencil value; otherwise uses hover stencil value.</param>
    public static void SetHighlighted(XRMaterial? material, bool enabled, bool isSelection = false)
    {
        if (material is null)
            return;

        //Set stencil buffer to indicate objects that should be highlighted.
        //material?.SetFloat("Highlighted", enabled ? 1.0f : 0.0f);
        var refValue = enabled ? (isSelection ? StencilRefSelection : StencilRefHover) : 0;
        var stencil = material.RenderOptions.StencilTest;
        stencil.Enabled = ERenderParamUsage.Enabled;
        stencil.FrontFace = new StencilTestFace()
        {
            Function = EComparison.Always,
            Reference = refValue,
            ReadMask = 3,
            WriteMask = 3,
            BothFailOp = EStencilOp.Keep,
            StencilPassDepthFailOp = EStencilOp.Keep,
            BothPassOp = EStencilOp.Replace,
        };
        stencil.BackFace = new StencilTestFace()
        {
            Function = EComparison.Always,
            Reference = refValue,
            ReadMask = 3,
            WriteMask = 3,
            BothFailOp = EStencilOp.Keep,
            StencilPassDepthFailOp = EStencilOp.Keep,
            BothPassOp = EStencilOp.Replace,
        };
    }

    /// <summary>
    /// This pipeline is set up to use the stencil buffer to highlight objects.
    /// This will highlight the given model.
    /// </summary>
    /// <param name="model">The model component to highlight.</param>
    /// <param name="enabled">Whether to enable or disable highlighting.</param>
    /// <param name="isSelection">If true, uses the selection stencil value; otherwise uses hover stencil value.</param>
    public static void SetHighlighted(ModelComponent? model, bool enabled, bool isSelection = false)
        => model?.Meshes.ForEach(m => m.LODs.ForEach(lod => SetHighlighted(lod.Renderer.Material, enabled, isSelection)));

    /// <summary>
    /// This pipeline is set up to use the stencil buffer to highlight objects.
    /// This will highlight the model representing the given rigid body.
    /// The model component must be a sibling component of the rigid body, or this will do nothing.
    /// </summary>
    /// <param name="body">The rigid body whose model to highlight.</param>
    /// <param name="enabled">Whether to enable or disable highlighting.</param>
    /// <param name="isSelection">If true, uses the selection stencil value; otherwise uses hover stencil value.</param>
    public static void SetHighlighted(PhysxDynamicRigidBody? body, bool enabled, bool isSelection = false)
        => SetHighlighted(body?.OwningComponent?.GetSiblingComponent<ModelComponent>(), enabled, isSelection);

    #endregion
}
