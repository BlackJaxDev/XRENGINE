﻿using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Components.Capture.Lights.Types;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_LightCombinePass : ViewportRenderCommand
    {
        public string AlbedoOpacityTexture { get; set; } = "AlbedoOpacityTexture";
        public string NormalTexture { get; set; } = "NormalTexture";
        public string RMSETexture { get; set; } = "RMSETexture";
        public string DepthViewTexture { get; set; } = "DepthViewTexture";

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

        public XRMeshRenderer? PointLightRenderer { get; private set; }
        public XRMeshRenderer? SpotLightRenderer { get; private set; }
        public XRMeshRenderer? DirectionalLightRenderer { get; private set; }

        protected override void Execute()
        {
            if (Pipeline.RenderState.Scene is null)
                return;

            var albOpacTex = Pipeline.GetTexture<XRTexture>(AlbedoOpacityTexture);
            var normTex = Pipeline.GetTexture<XRTexture>(NormalTexture);
            var rmseTex = Pipeline.GetTexture<XRTexture>(RMSETexture);
            var depthViewTex = Pipeline.GetTexture<XRTexture>(DepthViewTexture);
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

            var lights = Pipeline.RenderState.WindowViewport?.World?.Lights;
            if (lights is null)
                return;

            using (Pipeline.RenderState.PushRenderingCamera(Pipeline.RenderState.SceneCamera))
            {
                foreach (PointLightComponent c in lights.DynamicPointLights)
                    RenderPointLight(c);
                foreach (SpotLightComponent c in lights.DynamicSpotLights)
                    RenderSpotLight(c);
                foreach (DirectionalLightComponent c in lights.DynamicDirectionalLights)
                    RenderDirLight(c);
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
            renderer.Render(comp.LightMeshMatrix);
            _currentLightComponent = null;
        }
        private void LightManager_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            if (_currentLightComponent is null)
                return;

            _currentLightComponent.SetUniforms(materialProgram);
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
        }

        private static RenderingParameters GetAdditiveParameters()
        {
            RenderingParameters additiveRenderParams = new()
            {
                //Render only the backside so that the light still shows if the camera is inside of the volume
                //and the light does not add itself twice for the front and back faces.
                CullMode = ECullMode.Front,
                RequiredEngineUniforms = EUniformRequirements.Camera,
                //BlendModesPerDrawBuffer = new()
                //{
                //    [0] = new BlendMode()
                //    {
                //        Enabled = ERenderParamUsage.Enabled,
                //        RgbSrcFactor = EBlendingFactor.One,
                //        AlphaSrcFactor = EBlendingFactor.One,
                //        RgbDstFactor = EBlendingFactor.One,
                //        AlphaDstFactor = EBlendingFactor.One,
                //        RgbEquation = EBlendEquationMode.FuncAdd,
                //        AlphaEquation = EBlendEquationMode.FuncAdd,
                //    }
                //},
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
    }
}
