using XREngine.Data.Rendering;
using XREngine.Data.Geometry;
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

        /// <summary>
        /// A 1x1x1 dummy depth texture array bound to the ShadowMapArray sampler
        /// when cascaded shadows are not active, preventing GL_INVALID_OPERATION
        /// from sampler type mismatch on the default texture unit.
        /// </summary>
        private static XRTexture2DArray? _dummyShadowMapArray;
        private static XRTexture2DArray DummyShadowMapArray => _dummyShadowMapArray ??= new XRTexture2DArray(
            1, 1, 1,
            EPixelInternalFormat.DepthComponent16,
            EPixelFormat.DepthComponent,
            EPixelType.Float);

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

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            var world = viewport?.World;
            var lights = world?.Lights;
            if (lights is null)
                return;

            BoundingRectangle region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            int directionalLightCount = lights.DynamicDirectionalLights.Count;
            int pointLightCount = lights.DynamicPointLights.Count;
            int spotLightCount = lights.DynamicSpotLights.Count;

            Debug.RenderingEvery(
                $"RenderDiag.LightCombine.{ActivePipelineInstance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] LightCombine: VP={0} World={1} Play={2} Region={3}x{4} Msaa={5} Lights(D/P/S)={6}/{7}/{8} GBuffer={9} | {10} | {11} | {12}",
                viewport?.Index ?? -1,
                world?.TargetWorld?.Name ?? "<null>",
                world?.PlayState.ToString() ?? "<null>",
                region.Width,
                region.Height,
                msaaActive,
                directionalLightCount,
                pointLightCount,
                spotLightCount,
                DescribeTexture(albOpacTex),
                DescribeTexture(normTex),
                DescribeTexture(rmseTex),
                DescribeTexture(depthViewTex));

            if (viewport is { Width: > 512, Height: > 512 } && (region.Width <= 128 || region.Height <= 128))
            {
                Debug.RenderingWarningEvery(
                    $"RenderDiag.LightCombine.SmallRegion.{ActivePipelineInstance.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] LightCombine is running with unexpectedly small region {0}x{1} while viewport is {2}x{3}. World={4} VP={5}",
                    region.Width,
                    region.Height,
                    viewport.Width,
                    viewport.Height,
                    world?.TargetWorld?.Name ?? "<null>",
                    viewport.Index);
            }

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
            => BindCurrentLightUniforms(materialProgram);

        private void MsaaLightManager_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => BindCurrentLightUniforms(materialProgram);

        private void BindMsaaDeferredTextures(XRMaterialBase _, XRRenderProgram materialProgram)
        {
            if (_msaaAlbedoOpacityTextureCache is null ||
                _msaaNormalTextureCache is null ||
                _msaaRMSETextureCache is null ||
                _msaaDepthViewTextureCache is null)
            {
                return;
            }

            materialProgram.Sampler("AlbedoOpacity", _msaaAlbedoOpacityTextureCache, 0);
            materialProgram.Sampler("Normal", _msaaNormalTextureCache, 1);
            materialProgram.Sampler("RMSE", _msaaRMSETextureCache, 2);
            materialProgram.Sampler("DepthView", _msaaDepthViewTextureCache, 3);
        }

        private void BindCurrentLightUniforms(XRRenderProgram materialProgram)
        {
            if (_currentLightComponent is null)
                return;

            _currentLightComponent.SetUniforms(materialProgram);

            bool useCascadedDirectionalShadows = false;
            if (_currentLightComponent is DirectionalLightComponent directionalLight)
            {
                var cameraComponent = ActivePipelineInstance.RenderState.WindowViewport?.CameraComponent;
                useCascadedDirectionalShadows =
                    cameraComponent?.DirectionalShadowRenderingMode == global::XREngine.Components.EDirectionalShadowRenderingMode.Cascaded &&
                    directionalLight.EnableCascadedShadows &&
                    directionalLight.CascadedShadowMapTexture is not null &&
                    directionalLight.ActiveCascadeCount > 0;

                if (useCascadedDirectionalShadows)
                    materialProgram.Sampler("ShadowMapArray", directionalLight.CascadedShadowMapTexture!, 5);
                else
                    materialProgram.Sampler("ShadowMapArray", DummyShadowMapArray, 5);
            }

            materialProgram.Uniform("UseCascadedDirectionalShadows", useCascadedDirectionalShadows);

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

            CreateMsaaLightRenderers();
        }

        private void CreateMsaaLightRenderers()
        {
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

            XRMaterial msaaPointMat = new(Array.Empty<XRTexture?>(), msaaPointShader) { RenderOptions = msaaParams, RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial msaaSpotMat = new(Array.Empty<XRTexture?>(), msaaSpotShader) { RenderOptions = msaaParams, RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial msaaDirMat = new(Array.Empty<XRTexture?>(), msaaDirShader) { RenderOptions = msaaParams, RenderPass = (int)EDefaultRenderPass.OpaqueForward };

            msaaPointMat.SettingUniforms += BindMsaaDeferredTextures;
            msaaSpotMat.SettingUniforms += BindMsaaDeferredTextures;
            msaaDirMat.SettingUniforms += BindMsaaDeferredTextures;

            XRMesh pointMesh = PointLightComponent.GetVolumeMesh();
            XRMesh spotMesh = SpotLightComponent.GetVolumeMesh();
            XRMesh dirMesh = DirectionalLightComponent.GetVolumeMesh();

            MsaaPointLightRenderer = new XRMeshRenderer(pointMesh, msaaPointMat);
            MsaaPointLightRenderer.SettingUniforms += MsaaLightManager_SettingUniforms;

            MsaaSpotLightRenderer = new XRMeshRenderer(spotMesh, msaaSpotMat);
            MsaaSpotLightRenderer.SettingUniforms += MsaaLightManager_SettingUniforms;

            MsaaDirectionalLightRenderer = new XRMeshRenderer(dirMesh, msaaDirMat);
            MsaaDirectionalLightRenderer.SettingUniforms += MsaaLightManager_SettingUniforms;
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
                    },
                    StencilTest = new()
                    {
                        Enabled = ERenderParamUsage.Disabled,
                }
            };
            return additiveRenderParams;
        }

        private static string DescribeTexture(XRTexture? texture)
        {
            if (texture is null)
                return "<null>";

            var size = texture.WidthHeightDepth;
            return $"{texture.Name ?? texture.GetDescribingName()}({(int)size.X}x{(int)size.Y}x{(int)size.Z})";
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
