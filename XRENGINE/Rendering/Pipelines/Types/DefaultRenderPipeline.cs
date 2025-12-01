using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Extensions;
using XREngine;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Components.Capture.Lights;
using static XREngine.Engine.Rendering.State;

namespace XREngine.Rendering;

public class DefaultRenderPipeline : RenderPipeline
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

    private EGlobalIlluminationMode _globalIlluminationMode;
    public EGlobalIlluminationMode GlobalIlluminationMode
    {
        get => _globalIlluminationMode;
        set => SetField(ref _globalIlluminationMode, value);
    }

    public bool UsesRestirGI => _globalIlluminationMode == EGlobalIlluminationMode.Restir;
    public bool UsesVoxelConeTracing => _globalIlluminationMode == EGlobalIlluminationMode.VoxelConeTracing;
    public bool UsesLightProbeGI => _globalIlluminationMode == EGlobalIlluminationMode.LightProbesAndIbl;

    protected static bool GPURenderDispatch => Engine.UserSettings.GPURenderDispatch;

    private bool EnableMsaa
        => Engine.Rendering.Settings.AntiAliasingMode == Engine.Rendering.EAntiAliasingMode.Msaa
        && Engine.Rendering.Settings.MsaaSampleCount > 1u;
    private bool EnableFxaa => Engine.Rendering.Settings.AntiAliasingMode == Engine.Rendering.EAntiAliasingMode.Fxaa;
    private uint MsaaSampleCount => Math.Max(1u, Engine.Rendering.Settings.MsaaSampleCount);

    private string BrightPassShaderName() => 
        //Stereo ? "BrightPassStereo.fs" : 
        "BrightPass.fs";

    private string HudFBOShaderName() => 
        //Stereo ? "HudFBOStereo.fs" : 
        "HudFBO.fs";

    private string PostProcessShaderName() => 
        //Stereo ? "PostProcessStereo.fs" : 
        "PostProcess.fs";

    private string DeferredLightCombineShaderName() => 
        //Stereo ? "DeferredLightCombineStereo.fs" : 
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
    public const string RestirCompositeFBOName = "RestirCompositeFBO";
    public const string VelocityFBOName = "VelocityFBO";
    public const string HistoryCaptureFBOName = "HistoryCaptureFBO";
    public const string TemporalInputFBOName = "TemporalInputFBO";
    public const string TemporalAccumulationFBOName = "TemporalAccumulationFBO";
    public const string HistoryExposureFBOName = "HistoryExposureFBO";
        public const string MotionBlurCopyFBOName = "MotionBlurCopyFBO";
        public const string MotionBlurFBOName = "MotionBlurFBO";
        public const string DepthPreloadFBOName = "DepthPreloadFBO";

    //Textures
    public const string SSAONoiseTextureName = "SSAONoiseTexture";
    public const string AmbientOcclusionIntensityTextureName = "SSAOIntensityTexture";
    public const string NormalTextureName = "Normal";
    public const string DepthViewTextureName = "DepthView";
    public const string StencilViewTextureName = "StencilView";
    public const string AlbedoOpacityTextureName = "AlbedoOpacity";
    public const string RMSETextureName = "RMSE";
    public const string DepthStencilTextureName = "DepthStencil";
    public const string DiffuseTextureName = "LightingTexture";
    public const string HDRSceneTextureName = "HDRSceneTex";
    //public const string HDRSceneTexture2Name = "HDRSceneTex2";
    public const string BloomBlurTextureName = "BloomBlurTexture";
    public const string UserInterfaceTextureName = "HUDTex";
    public const string BRDFTextureName = "BRDF";
    public const string RestirGITextureName = "RestirGITexture";
    public const string VoxelConeTracingVolumeTextureName = "VoxelConeTracingVolume";
    public const string VelocityTextureName = "Velocity";
    public const string HistoryColorTextureName = "HistoryColor";
    public const string HistoryDepthStencilTextureName = "HistoryDepthStencil";
    public const string HistoryDepthViewTextureName = "HistoryDepth";
    public const string TemporalColorInputTextureName = "TemporalColorInput";
    public const string TemporalExposureVarianceTextureName = "TemporalExposureVariance";
    public const string HistoryExposureVarianceTextureName = "HistoryExposureVariance";
    public const string MotionBlurTextureName = "MotionBlur";

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
        CommandChain = GenerateCommandChain();
    }

    private void HandleRenderingSettingsChanged()
    {
        Engine.InvokeOnMainThread(() =>
        {
            CommandChain = GenerateCommandChain();
            foreach (var instance in Instances)
                instance.DestroyCache();
        }, true);
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
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GPURenderDispatch);
                c.Add<VPRC_DepthFunc>().Comp = EComparison.Always;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GPURenderDispatch);
            }
        }
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, GPURenderDispatch);
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
            //Render to the ambient occlusion FBO using SSAO or MVAO depending on the active settings
            var ambientOcclusionChoice = c.Add<VPRC_IfElse>();
            ambientOcclusionChoice.ConditionEvaluator = ShouldUseMVAO;
            ambientOcclusionChoice.TrueCommands = CreateMVAOPassCommands();
            ambientOcclusionChoice.FalseCommands = CreateSSAOPassCommands();

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
            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(forwardTargetName, true, EnableMsaa, EnableMsaa, EnableMsaa)))
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
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, GPURenderDispatch);

                //c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.TransparentForward, GPURenderDispatch);
                c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OnTopForward, GPURenderDispatch);
                c.Add<VPRC_ReSTIRPass>();

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
                    blitStencil: false,
                    linearFilter: false);
            }

            c.Add<VPRC_DepthTest>().Enable = false;

            c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                VelocityFBOName,
                CreateVelocityFBO,
                GetDesiredFBOSizeInternal);

            using (c.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(VelocityFBOName, true, true, false, false)))
            {
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = false;
                c.Add<VPRC_RenderMotionVectorsPass>().SetOptions(GPURenderDispatch);
                c.Add<VPRC_DepthWrite>().Allow = true;
            }

            c.Add<VPRC_DepthTest>().Enable = false;

            c.Add<VPRC_BloomPass>().SetTargetFBONames(
                ForwardPassFBOName,
                BloomBlurTextureName,
                Stereo);

            var motionBlurChoice = c.Add<VPRC_IfElse>();
            motionBlurChoice.ConditionEvaluator = ShouldUseMotionBlur;
            motionBlurChoice.TrueCommands = CreateMotionBlurPassCommands();

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

            if (EnableFxaa)
            {
                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    PostProcessOutputFBOName,
                    CreatePostProcessOutputFBO,
                    GetDesiredFBOSizeFull);

                c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
                    FxaaFBOName,
                    CreateFxaaFBO,
                    GetDesiredFBOSizeFull);
            }

            //c.Add<VPRC_CacheOrCreateFBO>().SetOptions(
            //    UserInterfaceFBOName,
            //    CreateUserInterfaceFBO,
            //    GetDesiredFBOSizeInternal);

            c.Add<VPRC_ExposureUpdate>().SetOptions(HDRSceneTextureName, true);
        }
        using (c.AddUsing<VPRC_PushViewportRenderArea>(t => t.UseInternalResolution = false))
        {
            if (EnableFxaa)
            {
                c.Add<VPRC_RenderQuadToFBO>().SetTargets(PostProcessFBOName, PostProcessOutputFBOName);
            }

            using (c.AddUsing<VPRC_BindOutputFBO>())
            {
                //c.Add<VPRC_ClearByBoundFBO>();
                c.Add<VPRC_RenderQuadFBO>().FrameBufferName = EnableFxaa ? FxaaFBOName : PostProcessFBOName;

                //We're not rendering to an FBO, we're rendering direct to the screen on top of the scene
                c.Add<VPRC_RenderScreenSpaceUI>()/*.OutputTargetFBOName = UserInterfaceFBOName*/;
            }
        }
        c.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        c.Add<VPRC_TemporalAccumulationPass>().Phase = VPRC_TemporalAccumulationPass.EPhase.Commit;
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
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            VelocityTextureName,
            CreateVelocityTexture,
            NeedsRecreateTextureInternalSize,
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

        if (EnableFxaa)
        {
            c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
                PostProcessOutputTextureName,
                CreatePostProcessOutputTexture,
                NeedsRecreateTextureFullSize,
                ResizeTextureFullSize);
        }

        //HDR Scene texture
        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            HDRSceneTextureName,
            CreateHDRSceneTexture,
            NeedsRecreateTextureInternalSize,
            ResizeTextureInternalSize);

        c.Add<VPRC_CacheOrCreateTexture>().SetOptions(
            RestirGITextureName,
            CreateRestirGITexture,
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
        var container = new ViewportRenderCommandContainer(this);
        ConfigureSSAOPass(container.Add<VPRC_SSAOPass>());
        return container;
    }

    private ViewportRenderCommandContainer CreateMVAOPassCommands()
    {
        var container = new ViewportRenderCommandContainer(this);
        ConfigureMVAOPass(container.Add<VPRC_MVAOPass>());
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
    }

    private static bool ShouldUseMVAO()
    {
        var aoSettings = State.SceneCamera?.PostProcessing?.AmbientOcclusion;

        if (aoSettings is { Enabled: true })
            return aoSettings.Type == AmbientOcclusionSettings.EType.MultiViewAmbientOcclusion;

        if (aoSettings is { Enabled: false })
            return false;

        return Engine.UserSettings.AmbientOcclusionMode == EAmbientOcclusionMode.MultiView;
    }

    private static MotionBlurSettings? GetMotionBlurSettings()
    {
        var renderState = Engine.Rendering.State.RenderingPipelineState;
        return renderState?.SceneCamera?.PostProcessing?.MotionBlur;
    }

    private static bool ShouldUseMotionBlur()
        => GetMotionBlurSettings() is { Enabled: true };

    #endregion

    #region Texture Creation

    private XRTexture CreateBRDFTexture()
        => PrecomputeBRDF();
    private XRTexture CreateDepthStencilTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = DepthStencilTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = DepthStencilTextureName;
            return t;
        }
    }
    private XRTexture CreateDepthViewTexture()
    {
        if (Stereo)
        {
            return new XRTexture2DArrayView(
                GetTexture<XRTexture2DArray>(DepthStencilTextureName)!,
                0u, 1u,
                0u, 2u, //Viewing both eyes, so 2 layers
                EPixelInternalFormat.Depth24Stencil8,
                false, false)
            {
                //We're viewing depth values only
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = DepthViewTextureName,
            };
        }
        else
        {
            return new XRTexture2DView(
                GetTexture<XRTexture2D>(DepthStencilTextureName)!,
                0u, 1u,
                EPixelInternalFormat.Depth24Stencil8,
                false, false)
            {
                //We're viewing depth values only
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = DepthViewTextureName,
            };
        }
    }
    private XRTexture CreateStencilViewTexture()
    {
        if (Stereo)
        {
            return new XRTexture2DArrayView(
                GetTexture<XRTexture2DArray>(DepthStencilTextureName)!,
                0u, 1u,
                0u, 2u, //Viewing both eyes, so 2 layers
                EPixelInternalFormat.Depth24Stencil8,
                false, false)
            {
                //We're viewing stencil values only
                DepthStencilViewFormat = EDepthStencilFmt.Stencil,
                Name = StencilViewTextureName,
            };
        }
        else
        {
            return new XRTexture2DView(
                GetTexture<XRTexture2D>(DepthStencilTextureName)!,
                0u, 1u,
                EPixelInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Stencil,
                Name = StencilViewTextureName,
            };
        }
    }

    private XRTexture CreateHistoryDepthStencilTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryDepthStencilTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryDepthStencilTextureName;
            return t;
        }
    }

    private XRTexture CreateHistoryDepthViewTexture()
    {
        if (Stereo)
        {
            return new XRTexture2DArrayView(
                GetTexture<XRTexture2DArray>(HistoryDepthStencilTextureName)!,
                0u, 1u,
                0u, 2u,
                EPixelInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = HistoryDepthViewTextureName,
            };
        }
        else
        {
            return new XRTexture2DView(
                GetTexture<XRTexture2D>(HistoryDepthStencilTextureName)!,
                0u, 1u,
                EPixelInternalFormat.Depth24Stencil8,
                false, false)
            {
                DepthStencilViewFormat = EDepthStencilFmt.Depth,
                Name = HistoryDepthViewTextureName,
            };
        }
    }
    private XRTexture CreateAlbedoOpacityTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = AlbedoOpacityTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            //t.Resizable = false;
            //t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = AlbedoOpacityTextureName;
            return t;
        }
    }
    private XRTexture CreateNormalTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgb16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgb16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = NormalTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgb16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat);
            //t.Resizable = false;
            //t.SizedInternalFormat = ESizedInternalFormat.Rgb16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = NormalTextureName;
            return t;
        }
    }
    private XRTexture CreateRMSETexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba8;
            t.OVRMultiViewParameters = new(0, 2u);
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte);
            //t.Resizable = false;
            //t.SizedInternalFormat = ESizedInternalFormat.Rgba8;
            return t;
        }
    }
    private XRTexture CreateLightingTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgb16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat);
            t.OVRMultiViewParameters = new(0, 2u);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgb16f;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgb16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat);
            //t.Resizable = false;
            //t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            return t;
        }
    }

    private XRTexture CreateVelocityTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = VelocityTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = VelocityTextureName;
            return t;
        }
    }

    private XRTexture CreateHistoryColorTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryColorTextureName;
            t.SamplerName = HistoryColorTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryColorTextureName;
            t.SamplerName = HistoryColorTextureName;
            return t;
        }
    }

    private XRTexture CreateTemporalColorInputTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TemporalColorInputTextureName;
            t.SamplerName = TemporalColorInputTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TemporalColorInputTextureName;
            t.SamplerName = TemporalColorInputTextureName;
            return t;
        }
    }

    private XRTexture CreateTemporalExposureVarianceTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TemporalExposureVarianceTextureName;
            t.SamplerName = TemporalExposureVarianceTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = TemporalExposureVarianceTextureName;
            t.SamplerName = TemporalExposureVarianceTextureName;
            return t;
        }
    }

    private XRTexture CreateHistoryExposureVarianceTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryExposureVarianceTextureName;
            t.SamplerName = HistoryExposureVarianceTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = HistoryExposureVarianceTextureName;
            t.SamplerName = HistoryExposureVarianceTextureName;
            return t;
        }
    }

    private XRTexture CreateMotionBlurTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = MotionBlurTextureName;
            t.SamplerName = MotionBlurTextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.Name = MotionBlurTextureName;
            t.SamplerName = MotionBlurTextureName;
            return t;
        }
    }
    private XRTexture CreateRestirGITexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.OVRMultiViewParameters = new(0, 2u);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = "Texture0";
            t.Name = RestirGITextureName;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.SamplerName = "Texture0";
            t.Name = RestirGITextureName;
            return t;
        }
    }

    private XRTexture CreateVoxelConeTracingVolumeTexture()
    {
        XRTexture3D texture = XRTexture3D.Create(128, 128, 128, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
        texture.MinFilter = ETexMinFilter.LinearMipmapLinear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.WWrap = ETexWrapMode.ClampToEdge;
        texture.AutoGenerateMipmaps = true;
        texture.Name = VoxelConeTracingVolumeTextureName;
        return texture;
    }

    private XRMaterial CreateVoxelConeTracingVoxelizationMaterial()
    {
        XRShader vertexShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "VoxelConeTracing", "voxelization.vert"), EShaderType.Vertex);
        XRShader geometryShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "VoxelConeTracing", "voxelization.geom"), EShaderType.Geometry);
        XRShader fragmentShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "VoxelConeTracing", "voxelization.frag"), EShaderType.Fragment);

        XRMaterial material = new(vertexShader, geometryShader, fragmentShader)
        {
            Name = "VoxelConeTracingVoxelization"
        };

        var options = material.RenderOptions;
        options.RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights;
        options.CullMode = ECullMode.None;
        options.WriteRed = false;
        options.WriteGreen = false;
        options.WriteBlue = false;
        options.WriteAlpha = false;
        options.DepthTest.Enabled = ERenderParamUsage.Disabled;
        options.DepthTest.UpdateDepth = false;
        options.DepthTest.Function = EComparison.Always;

        return material;
    }

    private static bool NeedsRecreateVoxelVolumeTexture(XRTexture texture)
        => texture is not XRTexture3D tex3D || tex3D.Width != 128 || tex3D.Height != 128 || tex3D.Depth != 128;

    private static void ResizeVoxelVolumeTexture(XRTexture texture)
    {
        if (texture is XRTexture3D tex3D)
            tex3D.Resize(128, 128, 128);
    }
    private XRTexture CreateHDRSceneTexture()
    {
        if (Stereo)
        {
            var t = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgb,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            t.Resizable = false;
            t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.OVRMultiViewParameters = new(0, 2u);
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = HDRSceneTextureName;
            t.Name = HDRSceneTextureName;
            //t.MultiSampleCount = 4;
            return t;
        }
        else
        {
            var t = XRTexture2D.CreateFrameBufferTexture(InternalWidth, InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            //t.Resizable = false;
            //t.SizedInternalFormat = ESizedInternalFormat.Rgba16f;
            t.MinFilter = ETexMinFilter.Nearest;
            t.MagFilter = ETexMagFilter.Nearest;
            t.UWrap = ETexWrapMode.ClampToEdge;
            t.VWrap = ETexWrapMode.ClampToEdge;
            t.SamplerName = HDRSceneTextureName;
            t.Name = HDRSceneTextureName;
            //t.MultiSampleCount = 4;
            return t;
        }
    }

    private XRTexture CreatePostProcessOutputTexture()
    {
        var (width, height) = GetDesiredFBOSizeFull();
        bool outputHdr = Engine.Rendering.Settings.OutputHDR;

        EPixelInternalFormat internalFormat = outputHdr ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8;
        EPixelType pixelType = outputHdr ? EPixelType.HalfFloat : EPixelType.UnsignedByte;
        ESizedInternalFormat sized = outputHdr ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8;

        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            internalFormat,
            EPixelFormat.Rgba,
            pixelType,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Resizable = true;
        texture.SizedInternalFormat = sized;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = PostProcessOutputTextureName;
        texture.Name = PostProcessOutputTextureName;
        return texture;
    }
    //private XRTexture CreateHUDTexture()
    //{
    //    uint w = (uint)State.WindowViewport!.Width;
    //    uint h = (uint)State.WindowViewport!.Height;
    //    if (Stereo)
    //    {
    //        var t = XRTexture2DArray.CreateFrameBufferTexture(
    //            2, w, h,
    //            EPixelInternalFormat.Rgba8,
    //            EPixelFormat.Rgba,
    //            EPixelType.UnsignedByte);
    //        t.Resizable = false;
    //        t.SizedInternalFormat = ESizedInternalFormat.Rgba8;
    //        t.OVRMultiViewParameters = new(0, 2u);
    //        t.MinFilter = ETexMinFilter.Nearest;
    //        t.MagFilter = ETexMagFilter.Nearest;
    //        t.UWrap = ETexWrapMode.ClampToEdge;
    //        t.VWrap = ETexWrapMode.ClampToEdge;
    //        t.SamplerName = UserInterfaceTextureName;
    //        return t;
    //    }
    //    else
    //    {
    //        var t = XRTexture2D.CreateFrameBufferTexture(
    //            w, h,
    //            EPixelInternalFormat.Rgba8,
    //            EPixelFormat.Rgba,
    //            EPixelType.UnsignedByte);
    //        //t.Resizable = false;
    //        //t.SizedInternalFormat = ESizedInternalFormat.Rgba8;
    //        t.MinFilter = ETexMinFilter.Nearest;
    //        t.MagFilter = ETexMagFilter.Nearest;
    //        t.UWrap = ETexWrapMode.ClampToEdge;
    //        t.VWrap = ETexWrapMode.ClampToEdge;
    //        t.SamplerName = UserInterfaceTextureName;
    //        return t;
    //    }
    //}

    #endregion

    #region FBO Creation

    //private XRFrameBuffer CreateUserInterfaceFBO()
    //{
    //    var hudTexture = GetTexture<XRTexture>(UserInterfaceTextureName)!;
    //    XRShader hudShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, HudFBOShaderName()), EShaderType.Fragment);
    //    XRMaterial hudMat = new([hudTexture], hudShader)
    //    {
    //        RenderOptions = new RenderingParameters()
    //        {
    //            DepthTest = new()
    //            {
    //                Enabled = ERenderParamUsage.Unchanged,
    //                Function = EComparison.Always,
    //                UpdateDepth = false,
    //            },
    //        }
    //    };
    //    var uiFBO = new XRQuadFrameBuffer(hudMat);

    //    if (hudTexture is not IFrameBufferAttachement hudAttach)
    //        throw new InvalidOperationException("HUD texture must be an FBO-attachable texture.");

    //    uiFBO.SetRenderTargets((hudAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1));

    //    return uiFBO;
    //}
    private XRFrameBuffer CreatePostProcessFBO()
    {
        XRTexture[] postProcessRefs =
        [
            GetTexture<XRTexture>(HDRSceneTextureName)!, //2
            GetTexture<XRTexture>(BloomBlurTextureName)!,
            GetTexture<XRTexture>(DepthViewTextureName)!,
            GetTexture<XRTexture>(StencilViewTextureName)!,
        ];
        XRShader postProcessShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, PostProcessShaderName()), EShaderType.Fragment);
        XRMaterial postProcessMat = new(postProcessRefs, postProcessShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };
        var PostProcessFBO = new XRQuadFrameBuffer(postProcessMat);
        PostProcessFBO.SettingUniforms += PostProcessFBO_SettingUniforms;
        return PostProcessFBO;
    }

    private XRFrameBuffer CreatePostProcessOutputFBO()
    {
        XRTexture outputTexture = GetTexture<XRTexture>(PostProcessOutputTextureName)!;
        if (outputTexture is not IFrameBufferAttachement attach)
            throw new InvalidOperationException("Post-process output texture must be FBO attachable.");

        return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = PostProcessOutputFBOName
        };
    }

    private XRFrameBuffer CreateFxaaFBO()
    {
        XRTexture fxaaSource = GetTexture<XRTexture>(PostProcessOutputTextureName)!;
        XRShader fxaaShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "FXAA.fs"), EShaderType.Fragment);
        XRMaterial fxaaMaterial = new([fxaaSource], fxaaShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                }
            }
        };
        XRQuadFrameBuffer fxaaFbo = new(fxaaMaterial)
        {
            Name = FxaaFBOName
        };
        fxaaFbo.SettingUniforms += FxaaFBO_SettingUniforms;
        return fxaaFbo;
    }
    private XRFrameBuffer CreateForwardPassFBO()
    {
        XRTexture hdrSceneTex = GetTexture<XRTexture>(HDRSceneTextureName)!;

        XRTexture[] brightRefs = [hdrSceneTex];
        XRMaterial brightMat = new(
            [
                new ShaderFloat(1.0f, "BloomIntensity"),
                new ShaderFloat(1.0f, "BloomThreshold"),
                new ShaderFloat(0.5f, "SoftKnee"),
                new ShaderVector3(Engine.Rendering.Settings.DefaultLuminance, "Luminance")
            ],
            brightRefs,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, BrightPassShaderName()), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            }
        };

        var fbo = new XRQuadFrameBuffer(brightMat, false);
        fbo.SettingUniforms += BrightPassFBO_SettingUniforms;

        if (hdrSceneTex is not IFrameBufferAttachement hdrAttach)
            throw new InvalidOperationException("HDR Scene texture is not an FBO-attachable texture.");

        if (GetTexture<XRTexture>(DepthStencilTextureName) is not IFrameBufferAttachement dsAttach)
            throw new InvalidOperationException("Depth/Stencil texture is not an FBO-attachable texture.");

        fbo.SetRenderTargets(
            (hdrAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1), //2
            (dsAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));

        return fbo;
    }

    private XRFrameBuffer CreateForwardPassMsaaFBO()
    {
        XRRenderBuffer colorBuffer = new(InternalWidth, InternalHeight, GetForwardMsaaColorFormat(), MsaaSampleCount)
        {
            FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            Name = $"{ForwardPassMsaaFBOName}_Color"
        };
        colorBuffer.Allocate();

        XRRenderBuffer depthBuffer = new(InternalWidth, InternalHeight, ERenderBufferStorage.Depth24Stencil8, MsaaSampleCount)
        {
            FrameBufferAttachment = EFrameBufferAttachment.DepthStencilAttachment,
            Name = $"{ForwardPassMsaaFBOName}_DepthStencil"
        };
        depthBuffer.Allocate();

        XRFrameBuffer fbo = new(
            (colorBuffer, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthBuffer, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = ForwardPassMsaaFBOName
        };

        return fbo;
    }

    private XRMaterial CreateMotionVectorsMaterial()
    {
        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "MotionVectors.fs"), EShaderType.Fragment);
        XRMaterial material = new(Array.Empty<XRTexture?>(), shader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Lequal,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.None
            }
        };

        material.SettingUniforms += MotionVectorsMaterial_SettingUniforms;
        return material;
    }

    private void MotionVectorsMaterial_SettingUniforms(XRMaterialBase material, XRRenderProgram program)
    {
        if (VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporal))
        {
            program.Uniform("HistoryReady", temporal.HistoryReady);
            program.Uniform("CurrViewProjection", temporal.CurrViewProjectionUnjittered);
            program.Uniform("PrevViewProjection", temporal.PrevViewProjectionUnjittered);
        }
        else
        {
            program.Uniform("HistoryReady", false);
            program.Uniform("CurrViewProjection", Matrix4x4.Identity);
            program.Uniform("PrevViewProjection", Matrix4x4.Identity);
        }
    }

    private XRFrameBuffer CreateVelocityFBO()
    {
        if (GetTexture<XRTexture>(VelocityTextureName) is not IFrameBufferAttachement velocityAttachment)
            throw new InvalidOperationException("Velocity texture is not an FBO-attachable texture.");

        if (GetTexture<XRTexture>(DepthStencilTextureName) is not IFrameBufferAttachement depthAttachment)
            throw new InvalidOperationException("Depth/Stencil texture is not an FBO-attachable texture.");

        return new XRFrameBuffer(
            (velocityAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = VelocityFBOName
        };
    }

    private XRFrameBuffer CreateHistoryCaptureFBO()
    {
        if (GetTexture<XRTexture>(HistoryColorTextureName) is not IFrameBufferAttachement colorAttachment)
            throw new InvalidOperationException("History color texture is not an FBO-attachable texture.");

        if (GetTexture<XRTexture>(HistoryDepthStencilTextureName) is not IFrameBufferAttachement depthAttachment)
            throw new InvalidOperationException("History depth texture is not an FBO-attachable texture.");

        return new XRFrameBuffer(
            (colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = HistoryCaptureFBOName
        };
    }

    private XRFrameBuffer CreateTemporalInputFBO()
    {
        if (GetTexture<XRTexture>(TemporalColorInputTextureName) is not IFrameBufferAttachement colorAttachment)
            throw new InvalidOperationException("Temporal color input texture is not FBO attachable.");

        return new XRFrameBuffer((colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = TemporalInputFBOName
        };
    }

    private XRFrameBuffer CreateTemporalAccumulationFBO()
    {
        XRTexture[] references =
        [
            GetTexture<XRTexture>(TemporalColorInputTextureName)!,
            GetTexture<XRTexture>(HistoryColorTextureName)!,
            GetTexture<XRTexture>(VelocityTextureName)!,
            GetTexture<XRTexture>(DepthViewTextureName)!,
            GetTexture<XRTexture>(HistoryDepthViewTextureName)!,
            GetTexture<XRTexture>(HistoryExposureVarianceTextureName)!,
        ];

        XRMaterial material = new(references,
            XRShader.EngineShader(Path.Combine(SceneShaderPath, "TemporalAccumulation.fs"), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                }
            }
        };

        var fbo = new XRQuadFrameBuffer(material) { Name = TemporalAccumulationFBOName };

        if (GetTexture<XRTexture>(HDRSceneTextureName) is not IFrameBufferAttachement filteredAttachment)
            throw new InvalidOperationException("HDR scene texture is not FBO attachable.");

        if (GetTexture<XRTexture>(TemporalExposureVarianceTextureName) is not IFrameBufferAttachement exposureAttachment)
            throw new InvalidOperationException("Temporal exposure texture is not FBO attachable.");

        fbo.SetRenderTargets(
            (filteredAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (exposureAttachment, EFrameBufferAttachment.ColorAttachment1, 0, -1));

        fbo.SettingUniforms += TemporalAccumulationFBO_SettingUniforms;
        return fbo;
    }

    private XRFrameBuffer CreateMotionBlurCopyFBO()
    {
        if (GetTexture<XRTexture>(MotionBlurTextureName) is not IFrameBufferAttachement attachment)
            throw new InvalidOperationException("Motion blur texture is not FBO attachable.");

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = MotionBlurCopyFBOName
        };
    }

    private XRFrameBuffer CreateMotionBlurFBO()
    {
        XRTexture motionBlurCopy = GetTexture<XRTexture>(MotionBlurTextureName)!;
        XRTexture velocityTex = GetTexture<XRTexture>(VelocityTextureName)!;
        XRTexture depthTex = GetTexture<XRTexture>(DepthViewTextureName)!;

        XRMaterial material = new(
            [motionBlurCopy, velocityTex, depthTex],
            XRShader.EngineShader(Path.Combine(SceneShaderPath, "MotionBlur.fs"), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                }
            }
        };

        var fbo = new XRQuadFrameBuffer(material) { Name = MotionBlurFBOName };
        fbo.SettingUniforms += MotionBlurFBO_SettingUniforms;
        return fbo;
    }

    private XRFrameBuffer CreateHistoryExposureFBO()
    {
        if (GetTexture<XRTexture>(HistoryExposureVarianceTextureName) is not IFrameBufferAttachement attachment)
            throw new InvalidOperationException("History exposure texture is not FBO attachable.");

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
        {
            Name = HistoryExposureFBOName
        };
    }

    private ERenderBufferStorage GetForwardMsaaColorFormat()
        => Engine.Rendering.Settings.OutputHDR ? ERenderBufferStorage.Rgba16f : ERenderBufferStorage.Rgba8;

    private XRFrameBuffer CreateDepthPreloadFBO()
    {
        XRTexture depthViewTexture = GetTexture<XRTexture>(DepthViewTextureName)!;

        XRMaterial material = new(
            [depthViewTexture],
            XRShader.EngineShader(Path.Combine(SceneShaderPath, "CopyDepthFromTexture.fs"), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = true,
                },
                WriteRed = false,
                WriteGreen = false,
                WriteBlue = false,
                WriteAlpha = false,
            }
        };

        return new XRQuadFrameBuffer(material, false) { Name = DepthPreloadFBOName };
    }
    private XRFrameBuffer CreateLightCombineFBO()
    {
        var diffuseTexture = GetTexture<XRTexture>(DiffuseTextureName)!;

        XRTexture[] lightCombineTextures = [
            GetTexture<XRTexture>(AlbedoOpacityTextureName)!,
            GetTexture<XRTexture>(NormalTextureName)!,
            GetTexture<XRTexture>(RMSETextureName)!,
            GetTexture<XRTexture>(AmbientOcclusionIntensityTextureName)!,
            GetTexture<XRTexture>(DepthViewTextureName)!,
            diffuseTexture,
            GetTexture<XRTexture2D>(BRDFTextureName)!,
            //irradiance
            //prefilter
        ];
        XRShader lightCombineShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, DeferredLightCombineShaderName()), EShaderType.Fragment);
        XRMaterial lightCombineMat = new(lightCombineTextures, lightCombineShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                RequiredEngineUniforms = EUniformRequirements.Camera
            }
        };

        var lightCombineFBO = new XRQuadFrameBuffer(lightCombineMat) { Name = LightCombineFBOName };

        if (diffuseTexture is IFrameBufferAttachement attach)
            lightCombineFBO.SetRenderTargets((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        else
            throw new InvalidOperationException("Diffuse texture is not an FBO-attachable texture.");

        lightCombineFBO.SettingUniforms += LightCombineFBO_SettingUniforms;
        return lightCombineFBO;
    }
    private XRFrameBuffer CreateRestirCompositeFBO()
    {
        XRTexture restirTexture = GetTexture<XRTexture>(RestirGITextureName)!;
        XRShader restirCompositeShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "RestirComposite.fs"), EShaderType.Fragment);
        BlendMode additiveBlend = new()
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = EBlendingFactor.One,
            AlphaSrcFactor = EBlendingFactor.One,
            RgbDstFactor = EBlendingFactor.One,
            AlphaDstFactor = EBlendingFactor.One,
            RgbEquation = EBlendEquationMode.FuncAdd,
            AlphaEquation = EBlendEquationMode.FuncAdd
        };
        XRMaterial restirCompositeMaterial = new([restirTexture], restirCompositeShader)
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                BlendModeAllDrawBuffers = additiveBlend
            }
        };
        var fbo = new XRQuadFrameBuffer(restirCompositeMaterial) { Name = RestirCompositeFBOName };
        fbo.SettingUniforms += RestirCompositeFBO_SettingUniforms;
        return fbo;
    }

    #endregion

    #region Setting Uniforms

    private void PostProcessFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        var sceneCam = RenderingPipelineState?.SceneCamera;
        materialProgram.Uniform("OutputHDR", Engine.Rendering.Settings.OutputHDR);
        if (sceneCam is null)
            return;

        //sceneCam.SetUniforms(materialProgram);

        //if (IsStereoPass)
        //    RenderingPipelineState?.StereoRightEyeCamera?.SetUniforms(materialProgram, false);

        sceneCam.SetPostProcessUniforms(materialProgram);
    }

    private void FxaaFBO_SettingUniforms(XRRenderProgram materialProgram)
    {
        float width = Math.Max(1u, FullWidth);
        float height = Math.Max(1u, FullHeight);
        var texelStep = new Vector2(1.0f / width, 1.0f / height);
        materialProgram.Uniform("FxaaTexelStep", texelStep);
    }
    private void BrightPassFBO_SettingUniforms(XRRenderProgram program)
    {
        var sceneCam = RenderingPipelineState?.SceneCamera;
        if (sceneCam is null)
            return;

        sceneCam.SetBloomBrightPassUniforms(program);
    }
    private void LightCombineFBO_SettingUniforms(XRRenderProgram program)
    {
        if (!UsesLightProbeGI)
            return;

        if (RenderingWorld is null || RenderingWorld.Lights.LightProbes.Count == 0)
            return;

        LightProbeComponent probe = RenderingWorld.Lights.LightProbes[0];

        int baseCount = GetFBO<XRQuadFrameBuffer>(LightCombineFBOName)?.Material?.Textures?.Count ?? 0;

        if (probe.IrradianceTexture != null)
        {
            var tex = probe.IrradianceTexture;
            if (tex != null)
                program.Sampler("Irradiance", tex, baseCount);
        }

        ++baseCount;

        if (probe.PrefilterTexture != null)
        {
            var tex = probe.PrefilterTexture;
            if (tex != null)
                program.Sampler("Prefilter", tex, baseCount);
        }
    }

    private void RestirCompositeFBO_SettingUniforms(XRRenderProgram program)
    {
    var region = RenderingPipelineState?.CurrentRenderRegion;
        float width = region?.Width > 0 ? region.Value.Width : InternalWidth;
        float height = region?.Height > 0 ? region.Value.Height : InternalHeight;
        program.Uniform("ScreenWidth", width);
        program.Uniform("ScreenHeight", height);
    }

    private void MotionBlurFBO_SettingUniforms(XRRenderProgram program)
    {
        float width = Math.Max(1u, InternalWidth);
        float height = Math.Max(1u, InternalHeight);
        var texelSize = new Vector2(1.0f / width, 1.0f / height);

        var settings = GetMotionBlurSettings();
        if (settings is null || !settings.Enabled)
        {
            program.Uniform("TexelSize", texelSize);
            program.Uniform("ShutterScale", 0.0f);
            program.Uniform("VelocityThreshold", 1.0f);
            program.Uniform("DepthRejectThreshold", 0.0f);
            program.Uniform("MaxBlurPixels", 0.0f);
            program.Uniform("SampleFalloff", 1.0f);
            program.Uniform("MaxSamples", 1);
            return;
        }

        settings.SetUniforms(program, texelSize);
    }

    private void TemporalAccumulationFBO_SettingUniforms(XRRenderProgram program)
    {
        if (VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out var temporalData))
        {
            float width = Math.Max(1u, temporalData.Width);
            float height = Math.Max(1u, temporalData.Height);
            program.Uniform("HistoryReady", temporalData.HistoryReady);
            program.Uniform("TexelSize", new Vector2(1.0f / width, 1.0f / height));
        }
        else
        {
            program.Uniform("HistoryReady", false);
            program.Uniform("TexelSize", Vector2.Zero);
        }

        program.Uniform("FeedbackMin", TemporalFeedbackMin);
        program.Uniform("FeedbackMax", TemporalFeedbackMax);
        program.Uniform("VarianceGamma", TemporalVarianceGamma);
        program.Uniform("CatmullRadius", TemporalCatmullRadius);
        program.Uniform("DepthRejectThreshold", TemporalDepthRejectThreshold);
        program.Uniform("ReactiveTransparencyRange", TemporalReactiveTransparencyRange);
        program.Uniform("ReactiveVelocityScale", TemporalReactiveVelocityScale);
        program.Uniform("ReactiveLumaThreshold", TemporalReactiveLumaThreshold);
        program.Uniform("DepthDiscontinuityScale", TemporalDepthDiscontinuityScale);
        program.Uniform("ConfidencePower", TemporalConfidencePower);
    }

    #endregion

    #region Highlighting

    /// <summary>
    /// This pipeline is set up to use the stencil buffer to highlight objects.
    /// This will highlight the given material.
    /// </summary>
    /// <param name="material"></param>
    /// <param name="enabled"></param>
    public static void SetHighlighted(XRMaterial? material, bool enabled)
    {
        if (material is null)
            return;

        //Set stencil buffer to indicate objects that should be highlighted.
        //material?.SetFloat("Highlighted", enabled ? 1.0f : 0.0f);
        var refValue = enabled ? 1 : 0;
        var stencil = material.RenderOptions.StencilTest;
        stencil.Enabled = ERenderParamUsage.Enabled;
        stencil.FrontFace = new StencilTestFace()
        {
            Function = EComparison.Always,
            Reference = refValue,
            ReadMask = 1,
            WriteMask = 1,
            BothFailOp = EStencilOp.Keep,
            StencilPassDepthFailOp = EStencilOp.Keep,
            BothPassOp = EStencilOp.Replace,
        };
        stencil.BackFace = new StencilTestFace()
        {
            Function = EComparison.Always,
            Reference = refValue,
            ReadMask = 1,
            WriteMask = 1,
            BothFailOp = EStencilOp.Keep,
            StencilPassDepthFailOp = EStencilOp.Keep,
            BothPassOp = EStencilOp.Replace,
        };
    }

    /// <summary>
    /// This pipeline is set up to use the stencil buffer to highlight objects.
    /// This will highlight the given model.
    /// </summary>
    /// <param name="model"></param>
    /// <param name="enabled"></param>
    public static void SetHighlighted(ModelComponent? model, bool enabled)
        => model?.Meshes.ForEach(m => m.LODs.ForEach(lod => SetHighlighted(lod.Renderer.Material, enabled)));

    /// <summary>
    /// This pipeline is set up to use the stencil buffer to highlight objects.
    /// This will highlight the model representing the given rigid body.
    /// The model component must be a sibling component of the rigid body, or this will do nothing.
    /// </summary>
    /// <param name="body"></param>
    /// <param name="enabled"></param>
    public static void SetHighlighted(PhysxDynamicRigidBody? body, bool enabled)
        => SetHighlighted(body?.OwningComponent?.GetSiblingComponent<ModelComponent>(), enabled);

    #endregion
}
