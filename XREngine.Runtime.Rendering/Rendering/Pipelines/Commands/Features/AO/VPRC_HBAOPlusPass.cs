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
    public class VPRC_HBAOPlusPass : ViewportRenderCommand, IDeclaredAoResourceProvider
    {
        private static void Log(string message)
            => Debug.Rendering(EOutputVerbosity.Normal, false, "[AO][HBAO+] {0}", message);

        private string HBAOPlusGenShaderName()
            => Stereo ? "HBAOPlusGenStereo.fs" : "HBAOPlusGen.fs";

        private string HBAOPlusBlurShaderName()
            => Stereo ? "HBAOPlusBlurStereo.fs" : "HBAOPlusBlur.fs";

        public string FinalIntensityTextureName { get; set; } = "AmbientOcclusionTexture";
        public string RawIntensityTextureName { get; set; } = "HBAOPlusRawTexture";
        public string IntermediateIntensityTextureName { get; set; } = "HBAOPlusBlurIntermediateTexture";
        public string InputSamplerName { get; set; } = "HBAOInputTexture";

        public string GenerationFBOName { get; set; } = "AmbientOcclusionFBO";
        public string BlurFBOName { get; set; } = "AmbientOcclusionBlurFBO";
        public string BlurIntermediateFBOName { get; set; } = "HBAOPlusBlurIntermediateFBO";
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
                Log("Missing required declared textures; skipping HBAO+ resource refresh.");
                return;
            }

            var area = RuntimeEngine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
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

            if (!forceRebuild && width == state.LastWidth && height == state.LastHeight)
                return;

            RefreshDeclaredResources(state, normalTex, depthViewTex, albedoTex, rmseTex, transformIdTex,
                depthStencilTex, rawAoTex, horizontalBlurTex, finalAoTex, width, height);
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
            int height)
        {
            state.LastWidth = width;
            state.LastHeight = height;
            state.NormalTexture = normalTex;
            state.DepthViewTexture = depthViewTex;
            state.AlbedoTexture = albedoTex;
            state.RmseTexture = rmseTex;
            state.TransformIdTexture = transformIdTex;
            state.DepthStencilTexture = depthStencilTex;
            state.RawAoTexture = rawAoTex;
            state.HorizontalBlurTexture = horizontalBlurTex;
            state.FinalAoTexture = finalAoTex;
            ConfigureAoSampler(rawAoTex, InputSamplerName);
            ConfigureAoSampler(horizontalBlurTex, InputSamplerName);
            ConfigureAoSampler(finalAoTex, FinalIntensityTextureName);
        }

        public XRFrameBuffer CreateDeclaredFrameBuffer(XRRenderPipelineInstance instance, string name)
        {
            RenderingParameters renderParams = CreateRenderParameters();
            XRShader blurShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, HBAOPlusBlurShaderName()), EShaderType.Fragment);

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
                frameBuffer.SettingUniforms += HBAOPlusHorizontalBlur_SetUniforms;
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
                frameBuffer.SettingUniforms += HBAOPlusVerticalBlur_SetUniforms;
                return frameBuffer;
            }

            if (!string.Equals(name, GenerationFBOName, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsupported HBAO+ framebuffer '{name}'.");

            XRShader genShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, HBAOPlusGenShaderName()), EShaderType.Fragment);
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
            genFbo.SettingUniforms += HBAOPlusGen_SetUniforms;
            return genFbo;
        }

        private static RenderingParameters CreateRenderParameters()
            => new()
            {
                DepthTest =
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    UpdateDepth = false,
                    Function = EComparison.Always,
                }
            };

        private static XRTexture RequireTexture(XRRenderPipelineInstance instance, string textureName)
            => instance.GetTexture<XRTexture>(textureName)
                ?? throw new InvalidOperationException($"Missing declared HBAO+ texture '{textureName}'.");

        private static IFrameBufferAttachement RequireAttachment(XRRenderPipelineInstance instance, string textureName)
            => RequireTexture(instance, textureName) as IFrameBufferAttachement
                ?? throw new InvalidOperationException($"Declared HBAO+ texture '{textureName}' is not framebuffer-attachable.");

        private static void ConfigureAoSampler(XRTexture texture, string samplerName)
        {
            texture.SamplerName = samplerName;
        }

        private void HBAOPlusGen_SetUniforms(XRRenderProgram program)
        {
            var camera = GetCurrentCamera();
            if (camera is null)
                return;

            camera.SetUniforms(program);

            if (RuntimeEngine.Rendering.State.IsStereoPass)
                ActivePipelineInstance.RenderState.StereoRightEyeCamera?.SetUniforms(program, false);

            camera.SetAmbientOcclusionUniforms(
                program,
                AmbientOcclusionSettings.EType.HorizonBasedPlus,
                ResolveSettingsPipeline());

            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)region.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)region.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), Vector2.Zero);
        }

        private void HBAOPlusHorizontalBlur_SetUniforms(XRRenderProgram program)
            => SetBlurUniforms(program, new Vector2(1.0f, 0.0f));

        private void HBAOPlusVerticalBlur_SetUniforms(XRRenderProgram program)
            => SetBlurUniforms(program, new Vector2(0.0f, 1.0f));

        private void SetBlurUniforms(XRRenderProgram program, Vector2 direction)
        {
            var camera = GetCurrentCamera();
            if (camera is not null)
                program.Uniform(EEngineUniform.DepthMode.ToStringFast(), (int)camera.DepthMode);

            var settings = GetCurrentSettings();
            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)region.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)region.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), Vector2.Zero);
            program.Uniform("BlurDirection", direction);
            program.Uniform("BlurRadius", Math.Clamp(settings?.HBAOBlurRadius ?? 8, 0, 16));
            program.Uniform("BlurSharpness", settings?.HBAOBlurSharpness is > 0.0f ? settings.HBAOBlurSharpness : 4.0f);
            program.Uniform("BlurEnabled", settings?.HBAOBlurEnabled ?? true);
            program.Uniform("UseInputNormals", settings?.HBAOUseInputNormals ?? true);
        }

        private AmbientOcclusionSettings? GetCurrentSettings()
        {
            var stage = GetCurrentCamera()?.GetPostProcessStageState<AmbientOcclusionSettings>(ResolveSettingsPipeline());
            return stage?.TryGetBacking(out AmbientOcclusionSettings? backing) == true ? backing : null;
        }

        private RenderPipeline? ResolveSettingsPipeline()
            => ActivePipelineInstance?.AssignedPipeline ?? ParentPipeline;

        private XRCamera? GetCurrentCamera()
            => ActivePipelineInstance.RenderState.SceneCamera
                ?? ActivePipelineInstance.RenderState.RenderingCamera
                ?? ActivePipelineInstance.LastSceneCamera
                ?? ActivePipelineInstance.LastRenderingCamera;

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            GetInstanceState(instance).ResourcesDirty = true;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (_instanceStates.TryGetValue(instance, out var state))
            {
                state.ResourcesDirty = true;
                state.RawAoTexture = null;
                state.HorizontalBlurTexture = null;
                state.FinalAoTexture = null;
                state.LastWidth = 0;
                state.LastHeight = 0;
            }
        }
    }
}
