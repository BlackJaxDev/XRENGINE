using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Scene;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_LightCombinePass : ViewportRenderCommand
    {
        private const string MsaaDeferredDefine = "XRENGINE_MSAA_DEFERRED";

        public string AlbedoOpacityTexture { get; set; } = "AlbedoOpacityTexture";
        public string NormalTexture { get; set; } = "NormalTexture";
        public string RMSETexture { get; set; } = "RMSETexture";
        public string DepthViewTexture { get; set; } = "DepthViewTexture";

        /// <summary>
        /// When true, the pass will perform two-pass rendering per light:
        /// simple pixels (sample 0) then complex pixels (per-sample via GL_SAMPLE_SHADING).
        /// Set by the pipeline based on whether MSAA deferred is active.
        /// </summary>
        public bool MsaaDeferred { get; set; }

        /// <summary>
        /// MSAA GBuffer texture names for the complex pixel per-sample pass.
        /// </summary>
        public string MsaaAlbedoOpacityTexture { get; set; } = DefaultRenderPipeline.MsaaAlbedoOpacityTextureName;
        public string MsaaNormalTexture { get; set; } = DefaultRenderPipeline.MsaaNormalTextureName;
        public string MsaaRMSETexture { get; set; } = DefaultRenderPipeline.MsaaRMSETextureName;
        public string MsaaDepthViewTexture { get; set; } = DefaultRenderPipeline.MsaaDepthViewTextureName;

        public void SetOptions(string albedoOpacityTexture, string normalTexture, string rmseTexture, string depthViewTexture)
        {
            AlbedoOpacityTexture = albedoOpacityTexture;
            NormalTexture = normalTexture;
            RMSETexture = rmseTexture;
            DepthViewTexture = depthViewTexture;
        }

        private XRTexture? _albedoOpacityTextureCache = null;
        private XRTexture? _normalTextureCache = null;
        private XRTexture? _rmseTextureCache = null;
        private XRTexture? _depthViewTextureCache = null;

        private XRTexture? _msaaAlbedoOpacityTextureCache = null;
        private XRTexture? _msaaNormalTextureCache = null;
        private XRTexture? _msaaRMSETextureCache = null;
        private XRTexture? _msaaDepthViewTextureCache = null;

        public XRMeshRenderer? PointLightRenderer { get; private set; }
        public XRMeshRenderer? SpotLightRenderer { get; private set; }
        public XRMeshRenderer? DirectionalLightRenderer { get; private set; }

        // MSAA per-sample renderers (complex pixel pass)
        public XRMeshRenderer? MsaaPointLightRenderer { get; private set; }
        public XRMeshRenderer? MsaaSpotLightRenderer { get; private set; }
        public XRMeshRenderer? MsaaDirectionalLightRenderer { get; private set; }

        protected override void Execute()
        {
            if (ActivePipelineInstance.RenderState.Scene is null)
                return;

            var albOpacTex = ActivePipelineInstance.GetTexture<XRTexture>(AlbedoOpacityTexture);
            var normTex = ActivePipelineInstance.GetTexture<XRTexture>(NormalTexture);
            var rmseTex = ActivePipelineInstance.GetTexture<XRTexture>(RMSETexture);
            var depthViewTex = ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTexture);
            if (albOpacTex is null || normTex is null || rmseTex is null || depthViewTex is null)
                throw new Exception("One or more required textures are missing.");

            if (_albedoOpacityTextureCache != albOpacTex ||
                _normalTextureCache != normTex ||
                _rmseTextureCache != rmseTex ||
                _depthViewTextureCache != depthViewTex)
            {
                _albedoOpacityTextureCache = albOpacTex;
                _normalTextureCache = normTex;
                _rmseTextureCache = rmseTex;
                _depthViewTextureCache = depthViewTex;
                CreateLightRenderers(albOpacTex, normTex, rmseTex, depthViewTex);
            }

            // Build MSAA renderers when MSAA deferred is active
            bool msaaActive = MsaaDeferred && DefaultRenderPipeline.RuntimeEnableMsaaDeferred;
            if (msaaActive)
                EnsureMsaaRenderers();

            var lights = ActivePipelineInstance.RenderState.WindowViewport?.World?.Lights;
            if (lights is null)
                return;

            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ActivePipelineInstance.RenderState.SceneCamera))
            {
                if (msaaActive)
                    RenderLightsMsaaDeferred(lights);
                else
                    RenderLightsStandard(lights);
            }
        }

        private void RenderLightsStandard(Lights3DCollection lights)
        {
            foreach (PointLightComponent c in lights.DynamicPointLights)
                RenderLight(PointLightRenderer!, c);
            foreach (SpotLightComponent c in lights.DynamicSpotLights)
                RenderLight(SpotLightRenderer!, c);
            foreach (DirectionalLightComponent c in lights.DynamicDirectionalLights)
                RenderLight(DirectionalLightRenderer!, c);
        }

        /// <summary>
        /// Two-pass MSAA deferred lighting. For each light:
        /// 1) Simple pixels (complex stencil bit NOT set): standard shader, sample 0
        /// 2) Complex pixels (complex stencil bit IS set): MSAA shader + GL_SAMPLE_SHADING per-sample
        /// </summary>
        private void RenderLightsMsaaDeferred(Lights3DCollection lights)
        {
            // --- Pass 1: Simple pixels (stencil bit NOT set) ---
            // The standard renderers read from resolved (non-MSAA) GBuffer.
            // GL_SAMPLE_SHADING is off, so fragment shader runs once per pixel
            // and the result is replicated to all MSAA samples.
            foreach (PointLightComponent c in lights.DynamicPointLights)
                RenderLight(PointLightRenderer!, c);
            foreach (SpotLightComponent c in lights.DynamicSpotLights)
                RenderLight(SpotLightRenderer!, c);
            foreach (DirectionalLightComponent c in lights.DynamicDirectionalLights)
                RenderLight(DirectionalLightRenderer!, c);

            // --- Pass 2: Complex pixels (stencil bit IS set) ---
            // The MSAA renderers read from MSAA GBuffer with sampler2DMS + gl_SampleID.
            // GL_SAMPLE_SHADING enables per-sample fragment invocation.
            Engine.Rendering.State.EnableSampleShading(1.0f);
            try
            {
                foreach (PointLightComponent c in lights.DynamicPointLights)
                    RenderLight(MsaaPointLightRenderer!, c);
                foreach (SpotLightComponent c in lights.DynamicSpotLights)
                    RenderLight(MsaaSpotLightRenderer!, c);
                foreach (DirectionalLightComponent c in lights.DynamicDirectionalLights)
                    RenderLight(MsaaDirectionalLightRenderer!, c);
            }
            finally
            {
                Engine.Rendering.State.DisableSampleShading();
            }
        }

        private void RenderDirLight(DirectionalLightComponent c)
            => RenderLight(DirectionalLightRenderer!, c);
        private void RenderPointLight(PointLightComponent c)
            => RenderLight(PointLightRenderer!, c);
        private void RenderSpotLight(SpotLightComponent c)
            => RenderLight(SpotLightRenderer!, c);

        private LightComponent? _currentLightComponent;

        private void RenderLight(XRMeshRenderer renderer, LightComponent comp)
        {
            _currentLightComponent = comp;
            renderer.Render(comp.LightMeshMatrix, comp.LightMeshMatrix, null);
            _currentLightComponent = null;
        }
        private void LightManager_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            if (_currentLightComponent is null)
                return;

            _currentLightComponent.SetUniforms(materialProgram);

            // Bind shadow map for deferred rendering at unit 4 (deferred shaders expect it there).
            // This is done here rather than in SetUniforms to avoid overwriting material texture units
            // during forward rendering.
            if (_currentLightComponent.CastsShadows && _currentLightComponent.ShadowMap?.Material?.Textures.Count > 0)
            {
                var shadowTex = _currentLightComponent.ShadowMap.Material.Textures[0];
                if (shadowTex != null)
                    materialProgram.Sampler("ShadowMap", shadowTex, 4);
            }

            // Point lights need the LightHasShadowMap uniform
            if (_currentLightComponent is PointLightComponent)
            {
                bool hasShadowMap = _currentLightComponent.CastsShadows && _currentLightComponent.ShadowMap?.Material?.Textures.Count > 0;
                materialProgram.Uniform("LightHasShadowMap", hasShadowMap);
            }
        }

        private void CreateLightRenderers(
            XRTexture albOpacTex,
            XRTexture normTex,
            XRTexture rmseTex,
            XRTexture depthViewTex)
        {
            XRTexture[] lightRefs =
            [
                albOpacTex,
                normTex,
                rmseTex,
                depthViewTex,
                //shadow map texture
            ];

            XRShader pointLightShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DeferredLightingPoint.fs"), EShaderType.Fragment);
            XRShader spotLightShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DeferredLightingSpot.fs"), EShaderType.Fragment);
            XRShader dirLightShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DeferredLightingDir.fs"), EShaderType.Fragment);

            RenderingParameters additiveRenderParams = GetAdditiveParameters();
            XRMaterial pointLightMat = new(lightRefs, pointLightShader) { RenderOptions = additiveRenderParams, RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial spotLightMat = new(lightRefs, spotLightShader) { RenderOptions = additiveRenderParams, RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial dirLightMat = new(lightRefs, dirLightShader) { RenderOptions = additiveRenderParams, RenderPass = (int)EDefaultRenderPass.OpaqueForward };

            XRMesh pointLightMesh = PointLightComponent.GetVolumeMesh();
            XRMesh spotLightMesh = SpotLightComponent.GetVolumeMesh();
            XRMesh dirLightMesh = DirectionalLightComponent.GetVolumeMesh();

            PointLightRenderer = new XRMeshRenderer(pointLightMesh, pointLightMat);
            PointLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            SpotLightRenderer = new XRMeshRenderer(spotLightMesh, spotLightMat);
            SpotLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            DirectionalLightRenderer = new XRMeshRenderer(dirLightMesh, dirLightMat);
            DirectionalLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            // Invalidate MSAA renderers since base textures changed
            MsaaPointLightRenderer = null;
            MsaaSpotLightRenderer = null;
            MsaaDirectionalLightRenderer = null;
        }

        private void EnsureMsaaRenderers()
        {
            var msaaAlbedoTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaAlbedoOpacityTexture);
            var msaaNormTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaNormalTexture);
            var msaaRmseTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaRMSETexture);
            var msaaDepthTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaDepthViewTexture);
            if (msaaAlbedoTex is null || msaaNormTex is null || msaaRmseTex is null || msaaDepthTex is null)
                return;

            if (_msaaAlbedoOpacityTextureCache == msaaAlbedoTex &&
                _msaaNormalTextureCache == msaaNormTex &&
                _msaaRMSETextureCache == msaaRmseTex &&
                _msaaDepthViewTextureCache == msaaDepthTex &&
                MsaaPointLightRenderer is not null)
                return;

            _msaaAlbedoOpacityTextureCache = msaaAlbedoTex;
            _msaaNormalTextureCache = msaaNormTex;
            _msaaRMSETextureCache = msaaRmseTex;
            _msaaDepthViewTextureCache = msaaDepthTex;

            CreateMsaaLightRenderers(msaaAlbedoTex, msaaNormTex, msaaRmseTex, msaaDepthTex);
        }

        private void CreateMsaaLightRenderers(
            XRTexture msaaAlbedoTex,
            XRTexture msaaNormTex,
            XRTexture msaaRmseTex,
            XRTexture msaaDepthTex)
        {
            XRTexture[] msaaLightRefs = [msaaAlbedoTex, msaaNormTex, msaaRmseTex, msaaDepthTex];

            // Create shader variants with XRENGINE_MSAA_DEFERRED define
            XRShader basePoint = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DeferredLightingPoint.fs"), EShaderType.Fragment);
            XRShader baseSpot = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DeferredLightingSpot.fs"), EShaderType.Fragment);
            XRShader baseDir = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DeferredLightingDir.fs"), EShaderType.Fragment);

            XRShader msaaPointShader = ShaderHelper.CreateDefinedShaderVariant(basePoint, MsaaDeferredDefine) ?? basePoint;
            XRShader msaaSpotShader = ShaderHelper.CreateDefinedShaderVariant(baseSpot, MsaaDeferredDefine) ?? baseSpot;
            XRShader msaaDirShader = ShaderHelper.CreateDefinedShaderVariant(baseDir, MsaaDeferredDefine) ?? baseDir;

            // Complex pixel pass: stencil test requires the complex bit to be set
            RenderingParameters msaaParams = GetAdditiveParametersWithStencil(
                VPRC_MarkComplexMsaaPixels.ComplexPixelStencilBit,
                EComparison.Equal);

            XRMaterial msaaPointMat = new(msaaLightRefs, msaaPointShader) { RenderOptions = msaaParams, RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial msaaSpotMat = new(msaaLightRefs, msaaSpotShader) { RenderOptions = msaaParams, RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial msaaDirMat = new(msaaLightRefs, msaaDirShader) { RenderOptions = msaaParams, RenderPass = (int)EDefaultRenderPass.OpaqueForward };

            XRMesh pointMesh = PointLightComponent.GetVolumeMesh();
            XRMesh spotMesh = SpotLightComponent.GetVolumeMesh();
            XRMesh dirMesh = DirectionalLightComponent.GetVolumeMesh();

            MsaaPointLightRenderer = new XRMeshRenderer(pointMesh, msaaPointMat);
            MsaaPointLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            MsaaSpotLightRenderer = new XRMeshRenderer(spotMesh, msaaSpotMat);
            MsaaSpotLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            MsaaDirectionalLightRenderer = new XRMeshRenderer(dirMesh, msaaDirMat);
            MsaaDirectionalLightRenderer.SettingUniforms += LightManager_SettingUniforms;
        }

        private static RenderingParameters GetAdditiveParameters()
        {
            RenderingParameters additiveRenderParams = new()
            {
                //Render only the backside so that the light still shows if the camera is inside of the volume
                //and the light does not add itself twice for the front and back faces.
                CullMode = ECullMode.Front,
                RequiredEngineUniforms = EUniformRequirements.Camera,
                BlendModeAllDrawBuffers = new()
                {
                    //Add the previous and current light colors together using FuncAdd with each mesh render
                    Enabled = ERenderParamUsage.Enabled,
                    RgbDstFactor = EBlendingFactor.One,
                    AlphaDstFactor = EBlendingFactor.One,
                    RgbSrcFactor = EBlendingFactor.One,
                    AlphaSrcFactor = EBlendingFactor.One,
                    RgbEquation = EBlendEquationMode.FuncAdd,
                    AlphaEquation = EBlendEquationMode.FuncAdd,
                },
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                }
            };
            return additiveRenderParams;
        }

        /// <summary>
        /// Creates additive rendering parameters with a stencil test that gates on the complex pixel bit.
        /// </summary>
        private static RenderingParameters GetAdditiveParametersWithStencil(uint stencilBit, EComparison comparison)
        {
            var stencilFace = new StencilTestFace
            {
                Function = comparison,
                Reference = (int)stencilBit,
                ReadMask = stencilBit,
                WriteMask = 0, // Don't modify stencil during lighting
                BothFailOp = EStencilOp.Keep,
                StencilPassDepthFailOp = EStencilOp.Keep,
                BothPassOp = EStencilOp.Keep,
            };

            return new RenderingParameters
            {
                CullMode = ECullMode.Front,
                RequiredEngineUniforms = EUniformRequirements.Camera,
                BlendModeAllDrawBuffers = new()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    RgbDstFactor = EBlendingFactor.One,
                    AlphaDstFactor = EBlendingFactor.One,
                    RgbSrcFactor = EBlendingFactor.One,
                    AlphaSrcFactor = EBlendingFactor.One,
                    RgbEquation = EBlendEquationMode.FuncAdd,
                    AlphaEquation = EBlendEquationMode.FuncAdd,
                },
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
                StencilTest = new()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    FrontFace = stencilFace,
                    BackFace = stencilFace,
                },
            };
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_LightCombinePass), ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeTextureResource(AlbedoOpacityTexture));
            builder.SampleTexture(MakeTextureResource(NormalTexture));
            builder.SampleTexture(MakeTextureResource(RMSETexture));
            builder.SampleTexture(MakeTextureResource(DepthViewTexture));

            if (context.CurrentRenderTarget is { } target)
                builder.UseColorAttachment(MakeFboColorResource(target.Name), target.ColorAccess, target.ConsumeColorLoadOp(), target.GetColorStoreOp());
        }
    }
}
