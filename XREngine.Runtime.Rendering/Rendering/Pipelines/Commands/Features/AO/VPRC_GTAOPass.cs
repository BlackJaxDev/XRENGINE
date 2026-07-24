using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_GTAOPass : ViewportRenderCommand, IDeclaredAoResourceProvider
    {
        private static void Log(string message)
            => Debug.Rendering(EOutputVerbosity.Normal, false, "[AO][GTAO] {0}", message);

        private string GTAOGenShaderName()
            => Stereo ? "GTAOGenStereo.fs" : "GTAOGen.fs";

        private string GTAOBlurShaderName()
            => Stereo ? "GTAOBlurStereo.fs" : "GTAOBlur.fs";

        public string FinalIntensityTextureName { get; set; } = "AmbientOcclusionTexture";
        public string RawIntensityTextureName { get; set; } = "GTAORawTexture";
        public string IntermediateIntensityTextureName { get; set; } = "GTAOBlurIntermediateTexture";
        public string InputSamplerName { get; set; } = "GTAOInputTexture";

        public string GenerationFBOName { get; set; } = "AmbientOcclusionFBO";
        public string BlurFBOName { get; set; } = "AmbientOcclusionBlurFBO";
        public string BlurIntermediateFBOName { get; set; } = "GTAOBlurIntermediateFBO";
        public string OutputFBOName { get; set; } = "GBufferFBO";

        public string NormalTextureName { get; set; } = "Normal";
        public string DepthViewTextureName { get; set; } = "DepthView";
        public string AlbedoTextureName { get; set; } = "AlbedoOpacity";
        public string RMSETextureName { get; set; } = "RMSE";
        public string TransformIdTextureName { get; set; } = "TransformId";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";
        public string[] DependentFboNames { get; set; } = Array.Empty<string>();

        public bool Stereo { get; set; }

        private sealed class InstanceState
        {
            public bool ResourcesDirty = true;
            public int LastWidth;
            public int LastHeight;
            public int LastResolutionDivisor;
            public XRTexture? RawAoTexture;
            public XRTexture? HorizontalBlurTexture;
            public XRTexture? FinalAoTexture;
            public XRTexture? NormalTexture;
            public XRTexture? DepthViewTexture;
            public XRTexture? AlbedoTexture;
            public XRTexture? RmseTexture;
            public XRTexture? TransformIdTexture;
            public XRTexture? DepthStencilTexture;
        }

        private static readonly ConditionalWeakTable<XRRenderPipelineInstance, InstanceState> _instanceStates = new();

        private InstanceState GetInstanceState(XRRenderPipelineInstance instance)
            => _instanceStates.GetValue(instance, _ => new InstanceState());

        public void SetOptions(bool stereo)
            => Stereo = stereo;

        public void SetGBufferInputTextureNames(string normal, string depthView, string albedo, string rmse, string depthStencil, string transformId = "TransformId")
        {
            NormalTextureName = normal;
            DepthViewTextureName = depthView;
            AlbedoTextureName = albedo;
            RMSETextureName = rmse;
            DepthStencilTextureName = depthStencil;
            TransformIdTextureName = transformId;
        }

        public void SetOutputNames(
            string finalIntensityTexture,
            string generationFbo,
            string blurFbo,
            string blurIntermediateFbo,
            string outputFbo,
            string rawIntensityTexture,
            string intermediateIntensityTexture)
        {
            FinalIntensityTextureName = finalIntensityTexture;
            GenerationFBOName = generationFbo;
            BlurFBOName = blurFbo;
            BlurIntermediateFBOName = blurIntermediateFbo;
            OutputFBOName = outputFbo;
            RawIntensityTextureName = rawIntensityTexture;
            IntermediateIntensityTextureName = intermediateIntensityTexture;
        }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            var state = GetInstanceState(instance);

            XRTexture? normalTex = instance.GetTexture<XRTexture>(NormalTextureName);
            XRTexture? depthViewTex = instance.GetTexture<XRTexture>(DepthViewTextureName);
            XRTexture? albedoTex = instance.GetTexture<XRTexture>(AlbedoTextureName);
            XRTexture? rmseTex = instance.GetTexture<XRTexture>(RMSETextureName);
            XRTexture? transformIdTex = instance.GetTexture<XRTexture>(TransformIdTextureName);
            XRTexture? depthStencilTex = instance.GetTexture<XRTexture>(DepthStencilTextureName);
            XRTexture? rawAoTex = instance.GetTexture<XRTexture>(RawIntensityTextureName);
            XRTexture? horizontalBlurTex = instance.GetTexture<XRTexture>(IntermediateIntensityTextureName);
            XRTexture? finalAoTex = instance.GetTexture<XRTexture>(FinalIntensityTextureName);

            if (normalTex is null ||
                depthViewTex is null ||
                albedoTex is null ||
                rmseTex is null ||
                transformIdTex is null ||
                depthStencilTex is null ||
                rawAoTex is null ||
                horizontalBlurTex is null ||
                finalAoTex is null)
            {
                Log("Missing required declared textures; skipping GTAO resource refresh.");
                return;
            }

            ResolveActiveRenderSize(instance, out int width, out int height);
            bool forceRebuild = state.ResourcesDirty;
            state.ResourcesDirty = false;

            if (!forceRebuild)
            {
                forceRebuild = !ReferenceEquals(state.RawAoTexture, rawAoTex)
                    || !ReferenceEquals(state.HorizontalBlurTexture, horizontalBlurTex)
                    || !ReferenceEquals(state.FinalAoTexture, finalAoTex)
                    || !ReferenceEquals(state.NormalTexture, normalTex)
                    || !ReferenceEquals(state.DepthViewTexture, depthViewTex)
                    || !ReferenceEquals(state.AlbedoTexture, albedoTex)
                    || !ReferenceEquals(state.RmseTexture, rmseTex)
                    || !ReferenceEquals(state.TransformIdTexture, transformIdTex)
                    || !ReferenceEquals(state.DepthStencilTexture, depthStencilTex);
            }

            if (!forceRebuild)
                forceRebuild = !HasDeclaredFrameBuffers(instance);

            int resDivisor = (int)(GetCurrentSettings()?.GTAOResolution
                ?? GroundTruthAmbientOcclusionSettings.EResolution.Half);
            if (resDivisor < 1) resDivisor = 1;

            if (!forceRebuild && width == state.LastWidth && height == state.LastHeight && resDivisor == state.LastResolutionDivisor)
                return;

            RefreshDeclaredResources(state, normalTex, depthViewTex, albedoTex, rmseTex, transformIdTex,
                depthStencilTex, rawAoTex, horizontalBlurTex, finalAoTex, width, height, resDivisor);
        }

        private bool HasDeclaredFrameBuffers(XRRenderPipelineInstance instance)
            => instance.TryGetFBO(GenerationFBOName, out _)
            && instance.TryGetFBO(BlurFBOName, out _)
            && instance.TryGetFBO(BlurIntermediateFBOName, out _)
            && instance.TryGetFBO(OutputFBOName, out _);

        private void RefreshDeclaredResources(
            InstanceState state,
            XRTexture normalTex,
            XRTexture depthViewTex,
            XRTexture albedoTex,
            XRTexture rmseTex,
            XRTexture transformIdTex,
            XRTexture depthStencilTex,
            XRTexture rawAoTex,
            XRTexture horizontalBlurTex,
            XRTexture finalAoTex,
            int width,
            int height,
            int resDivisor)
        {
            state.LastWidth = width;
            state.LastHeight = height;
            state.LastResolutionDivisor = resDivisor;

            state.NormalTexture = normalTex;
            state.DepthViewTexture = depthViewTex;
            state.AlbedoTexture = albedoTex;
            state.RmseTexture = rmseTex;
            state.TransformIdTexture = transformIdTex;
            state.DepthStencilTexture = depthStencilTex;
            state.RawAoTexture = rawAoTex;
            state.HorizontalBlurTexture = horizontalBlurTex;
            state.FinalAoTexture = finalAoTex;
            ConfigureAoSampler(rawAoTex, InputSamplerName, bilinear: false);
            ConfigureAoSampler(horizontalBlurTex, InputSamplerName, bilinear: resDivisor > 1);
            ConfigureAoSampler(finalAoTex, FinalIntensityTextureName, bilinear: false);
        }

        public XRFrameBuffer CreateDeclaredFrameBuffer(XRRenderPipelineInstance instance, string name)
        {
            RenderingParameters renderParams = CreateRenderParameters();
            XRShader blurShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, GTAOBlurShaderName()), EShaderType.Fragment);

            if (string.Equals(name, BlurFBOName, StringComparison.Ordinal))
            {
                XRMaterial material = new([
                    RequireTexture(instance, RawIntensityTextureName),
                    RequireTexture(instance, DepthViewTextureName),
                    RequireTexture(instance, NormalTextureName)], blurShader) { RenderOptions = renderParams };
                XRQuadFrameBuffer frameBuffer = new(material, true,
                    (RequireAttachment(instance, RawIntensityTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = BlurFBOName
                };
                frameBuffer.SettingUniforms += GTAOHorizontalBlur_SetUniforms;
                return frameBuffer;
            }

            if (string.Equals(name, BlurIntermediateFBOName, StringComparison.Ordinal))
            {
                XRMaterial material = new([
                    RequireTexture(instance, IntermediateIntensityTextureName),
                    RequireTexture(instance, DepthViewTextureName),
                    RequireTexture(instance, NormalTextureName)], blurShader) { RenderOptions = renderParams };
                XRQuadFrameBuffer frameBuffer = new(material, true,
                    (RequireAttachment(instance, IntermediateIntensityTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = BlurIntermediateFBOName
                };
                frameBuffer.SettingUniforms += GTAOVerticalBlur_SetUniforms;
                return frameBuffer;
            }

            if (!string.Equals(name, GenerationFBOName, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsupported GTAO framebuffer '{name}'.");

            XRShader genShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, GTAOGenShaderName()), EShaderType.Fragment);
            XRMaterial genMaterial = new([
                RequireTexture(instance, NormalTextureName),
                RequireTexture(instance, DepthViewTextureName)], genShader) { RenderOptions = renderParams };
            XRQuadFrameBuffer genFbo = new(genMaterial, true,
                (RequireAttachment(instance, AlbedoTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (RequireAttachment(instance, NormalTextureName), EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (RequireAttachment(instance, RMSETextureName), EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (RequireAttachment(instance, TransformIdTextureName), EFrameBufferAttachment.ColorAttachment3, 0, -1),
                (RequireAttachment(instance, DepthStencilTextureName), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = GenerationFBOName
            };
            genFbo.SettingUniforms += GTAOGen_SetUniforms;
            return genFbo;
        }

        private static RenderingParameters CreateRenderParameters()
            => new()
            {
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy,
                DepthTest =
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    UpdateDepth = false,
                    Function = EComparison.Always,
                }
            };

        private static XRTexture RequireTexture(XRRenderPipelineInstance instance, string textureName)
            => instance.GetTexture<XRTexture>(textureName)
                ?? throw new InvalidOperationException($"Missing declared GTAO texture '{textureName}'.");

        private static IFrameBufferAttachement RequireAttachment(XRRenderPipelineInstance instance, string textureName)
            => RequireTexture(instance, textureName) as IFrameBufferAttachement
                ?? throw new InvalidOperationException($"Declared GTAO texture '{textureName}' is not framebuffer-attachable.");

        private static void ConfigureAoSampler(XRTexture texture, string samplerName, bool bilinear)
        {
            texture.SamplerName = samplerName;
            ETexMinFilter minFilter = bilinear ? ETexMinFilter.Linear : ETexMinFilter.Nearest;
            ETexMagFilter magFilter = bilinear ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
            if (texture is XRTexture2D texture2D)
            {
                texture2D.MinFilter = minFilter;
                texture2D.MagFilter = magFilter;
            }
            else if (texture is XRTexture2DArray textureArray)
            {
                textureArray.MinFilter = minFilter;
                textureArray.MagFilter = magFilter;
            }
        }

        private void GTAOGen_SetUniforms(XRRenderProgram program)
        {
            var camera = GetCurrentCamera();
            if (camera is null)
                return;

            camera.SetUniforms(program);

            if (IsStereoPassActive())
                GetRightEyeCamera()?.SetUniforms(program, false);

            camera.SetAmbientOcclusionUniforms(
                program,
                AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion,
                ResolveSettingsPipeline());

            SetScreenUniforms(program);
        }

        private void GTAOHorizontalBlur_SetUniforms(XRRenderProgram program)
        {
            SetBlurUniforms(program, new Vector2(1.0f, 0.0f));
            // Horizontal blur runs entirely at reduced resolution;
            // input and output texel grids match, so pass reduced-res texel size.
            var state = GetInstanceState(ActivePipelineInstance);
            int aoWidth = Math.Max(state.LastWidth / Math.Max(state.LastResolutionDivisor, 1), 1);
            int aoHeight = Math.Max(state.LastHeight / Math.Max(state.LastResolutionDivisor, 1), 1);
            program.Uniform("TexelSize", new Vector2(1.0f / aoWidth, 1.0f / aoHeight));
        }

        private void GTAOVerticalBlur_SetUniforms(XRRenderProgram program)
        {
            SetBlurUniforms(program, new Vector2(0.0f, 1.0f));
            // Vertical blur upscales to full output resolution;
            // pass full-res texel size so the blur kernel spans the correct
            // number of screen pixels instead of stretching by the divisor.
            var state = GetInstanceState(ActivePipelineInstance);
            int fullWidth = Math.Max(state.LastWidth, 1);
            int fullHeight = Math.Max(state.LastHeight, 1);
            program.Uniform("TexelSize", new Vector2(1.0f / fullWidth, 1.0f / fullHeight));
        }

        private void SetBlurUniforms(XRRenderProgram program, Vector2 direction)
        {
            var camera = GetCurrentCamera();
            if (camera is not null)
                program.Uniform(EEngineUniform.DepthMode.ToStringFast(), (int)camera.DepthMode);

            var settings = GetCurrentSettings();
            SetScreenUniforms(program);
            program.Uniform("BlurDirection", direction);
            program.Uniform("DenoiseRadius", Math.Clamp(settings?.GTAODenoiseRadius ?? GroundTruthAmbientOcclusionSettings.DefaultDenoiseRadius, 0, 16));
            program.Uniform("DenoiseSharpness", settings?.GTAODenoiseSharpness is > 0.0f ? settings.GTAODenoiseSharpness : GroundTruthAmbientOcclusionSettings.DefaultDenoiseSharpness);
            program.Uniform("DenoiseEnabled", settings?.GTAODenoiseEnabled ?? true);
            program.Uniform("UseInputNormals", settings?.GTAOUseInputNormals ?? true);
            program.Uniform("UseNormalWeightedBlur", settings?.GTAOUseNormalWeightedBlur ?? true);
        }

        private AmbientOcclusionSettings? GetCurrentSettings()
        {
            var stage = GetCurrentCamera()?.GetPostProcessStageState<AmbientOcclusionSettings>(ResolveSettingsPipeline());
            return stage?.TryGetBacking(out AmbientOcclusionSettings? backing) == true ? backing : null;
        }

        private RenderPipeline? ResolveSettingsPipeline()
            => ActivePipelineInstance?.AssignedPipeline ?? ParentPipeline;

        private static void ResolveActiveRenderSize(XRRenderPipelineInstance? instance, out int width, out int height)
        {
            var area = instance?.RenderState.CurrentRenderRegion ?? RuntimeEngine.Rendering.State.RenderArea;
            if (area.Width <= 0 || area.Height <= 0)
                area = RuntimeEngine.Rendering.State.RenderArea;

            width = Math.Max(area.Width, 1);
            height = Math.Max(area.Height, 1);
        }

        private void SetScreenUniforms(XRRenderProgram program)
        {
            ResolveActiveRenderSize(ActivePipelineInstance, out int width, out int height);
            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), Vector2.Zero);
        }

        private bool IsStereoPassActive()
        {
            var instance = ActivePipelineInstance;
            var renderState = RuntimeEngine.Rendering.State.ActiveRenderCommandExecutionState;
            return instance?.RenderState.StereoPass == true ||
                   renderState?.StereoPass == true ||
                   RuntimeEngine.Rendering.State.IsStereoPass;
        }

        private XRCamera? GetRightEyeCamera()
        {
            var instance = ActivePipelineInstance;
            var renderState = RuntimeEngine.Rendering.State.ActiveRenderCommandExecutionState;
            return instance?.RenderState.StereoRightEyeCamera
                ?? (renderState?.StereoRightEyeCamera as XRCamera)
                ?? RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;
        }

        private XRCamera? GetCurrentCamera()
        {
            var instance = ActivePipelineInstance;
            var renderState = RuntimeEngine.Rendering.State.ActiveRenderCommandExecutionState;
            return instance?.RenderState.SceneCamera
                ?? RuntimeEngine.Rendering.State.RenderingCamera
                ?? instance?.RenderState.RenderingCamera
                ?? (renderState?.SceneCamera as XRCamera)
                ?? (renderState?.RenderingCamera as XRCamera)
                ?? instance?.LastSceneCamera
                ?? instance?.LastRenderingCamera;
        }

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            GetInstanceState(instance).ResourcesDirty = true;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (!_instanceStates.TryGetValue(instance, out var state))
                return;
            
            state.ResourcesDirty = true;
            state.RawAoTexture = null;
            state.HorizontalBlurTexture = null;
            state.FinalAoTexture = null;
            state.LastWidth = 0;
            state.LastHeight = 0;
            state.LastResolutionDivisor = 0;
        }
    }
}
