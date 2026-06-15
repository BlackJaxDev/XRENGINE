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
    public class VPRC_MSVO : ViewportRenderCommand
    {
        private static void Log(string message)
            => Debug.Rendering(EOutputVerbosity.Normal, false, "[AO][MSVO] {0}", message);

        private string MSVOGenShaderName()
            => Stereo ? "MSVOGenStereo.fs" : "MSVOGen.fs";

        private string MSVOBlurShaderName()
            => Stereo ? "SSAOBlurStereo.fs" : "SSAOBlur.fs";

        public string MSVORawTextureName { get; set; } = "AmbientOcclusionRawTexture";
        public string MSVOIntensityTextureName { get; set; } = "AmbientOcclusionTexture";
        public string MSVOFBOName { get; set; } = "AmbientOcclusionFBO";
        public string MSVOBlurFBOName { get; set; } = "AmbientOcclusionBlurFBO";
        public string GBufferFBOFBOName { get; set; } = "GBufferFBO";
        public string NormalTextureName { get; set; } = "Normal";
        public string DepthViewTextureName { get; set; } = "DepthView";
        public string AlbedoTextureName { get; set; } = "AlbedoOpacity";
        public string RMSETextureName { get; set; } = "RMSE";
        public string TransformIdTextureName { get; set; } = "TransformId";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";
        public string[] DependentFboNames { get; set; } = Array.Empty<string>();

        public Vector4 ScaleFactors { get; set; } = new(0.1f, 0.2f, 0.4f, 0.8f);
        public bool Stereo { get; set; }

        private sealed class InstanceState
        {
            public bool ResourcesDirty = true;
            public int LastWidth;
            public int LastHeight;
            public XRTexture? RawAoTexture;
            public XRTexture? FinalAoTexture;
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

        public void SetOutputNames(string intensityTexture, string generationFbo, string blurFbo, string gBufferFBO)
        {
            MSVORawTextureName = "AmbientOcclusionRawTexture";
            MSVOIntensityTextureName = intensityTexture;
            MSVOFBOName = generationFbo;
            MSVOBlurFBOName = blurFbo;
            GBufferFBOFBOName = gBufferFBO;
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

            if (normalTex is null ||
                depthViewTex is null ||
                albedoTex is null ||
                rmseTex is null ||
                transformIdTex is null ||
                depthStencilTex is null)
            {
                Log("Missing required GBuffer textures; skipping MSVO resource refresh.");
                return;
            }

            var area = RuntimeEngine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
            bool forceRebuild = state.ResourcesDirty;
            state.ResourcesDirty = false;

            if (!forceRebuild)
            {
                XRTexture? registeredRawAo = instance.GetTexture<XRTexture>(MSVORawTextureName);
                XRTexture? registeredFinalAo = instance.GetTexture<XRTexture>(MSVOIntensityTextureName);
                forceRebuild = state.RawAoTexture is null
                    || state.FinalAoTexture is null
                    || registeredRawAo is null
                    || registeredFinalAo is null
                    || !ReferenceEquals(state.RawAoTexture, registeredRawAo)
                    || !ReferenceEquals(state.FinalAoTexture, registeredFinalAo);
            }

            if (!forceRebuild)
                forceRebuild = !instance.TryGetFBO(MSVOFBOName, out _);

            if (!forceRebuild && width == state.LastWidth && height == state.LastHeight)
                return;

            Debug.RenderingEvery(
                $"AO.MSVO.Execute.{RuntimeHelpers.GetHashCode(instance)}",
                TimeSpan.FromSeconds(1),
                "[AO][MSVO] Execute forceRebuild={0} size={1}x{2} stereo={3} normal={4} depth={5} output={6}",
                forceRebuild,
                width,
                height,
                Stereo,
                normalTex.Name ?? "null",
                depthViewTex.Name ?? "null",
                MSVOIntensityTextureName);

            RegenerateFBOs(
                instance,
                state,
                normalTex,
                depthViewTex,
                albedoTex,
                rmseTex,
                transformIdTex,
                depthStencilTex,
                width,
                height);
        }

        private void RegenerateFBOs(
            XRRenderPipelineInstance instance,
            InstanceState state,
            XRTexture normalTex,
            XRTexture depthViewTex,
            XRTexture albedoTex,
            XRTexture rmseTex,
            XRTexture transformIdTex,
            XRTexture depthStencilTex,
            int width,
            int height)
        {
            state.LastWidth = width;
            state.LastHeight = height;

            state.RawAoTexture = ResolveAoTexture(instance, state.RawAoTexture, width, height, MSVORawTextureName, MSVOIntensityTextureName);
            state.FinalAoTexture = ResolveAoTexture(instance, state.FinalAoTexture, width, height, MSVOIntensityTextureName, MSVOIntensityTextureName);
            InvalidateDependentFbos(instance);

            RenderingParameters renderParams = new()
            {
                DepthTest =
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    UpdateDepth = false,
                    Function = EComparison.Always,
                }
            };

            XRShader msvoGenShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, MSVOGenShaderName()), EShaderType.Fragment);
            XRShader msvoBlurShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, MSVOBlurShaderName()), EShaderType.Fragment);

            XRTexture[] msvoGenTexRefs =
            [
                normalTex,
                depthViewTex,
            ];

            XRTexture[] msvoBlurTexRefs =
            [
                state.RawAoTexture!,
            ];

            XRMaterial msvoGenMat = new(msvoGenTexRefs, msvoGenShader) { RenderOptions = renderParams };
            XRMaterial msvoBlurMat = new(msvoBlurTexRefs, msvoBlurShader) { RenderOptions = renderParams };

            if (albedoTex is not IFrameBufferAttachement albedoAttach)
                throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");

            if (normalTex is not IFrameBufferAttachement normalAttach)
                throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");

            if (rmseTex is not IFrameBufferAttachement rmseAttach)
                throw new ArgumentException("RMSE texture must be an IFrameBufferAttachement");

            if (transformIdTex is not IFrameBufferAttachement transformIdAttach)
                throw new ArgumentException("TransformId texture must be an IFrameBufferAttachement");

            if (depthStencilTex is not IFrameBufferAttachement depthStencilAttach)
                throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");

            if (state.RawAoTexture is not IFrameBufferAttachement rawAoAttach)
                throw new ArgumentException("Raw ambient occlusion texture must be an IFrameBufferAttachement");

            if (state.FinalAoTexture is not IFrameBufferAttachement finalAoAttach)
                throw new ArgumentException("Final ambient occlusion texture must be an IFrameBufferAttachement");

            XRQuadFrameBuffer msvoGenFBO = new(msvoGenMat, true,
                (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (transformIdAttach, EFrameBufferAttachment.ColorAttachment3, 0, -1),
                (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = MSVOFBOName
            };
            msvoGenFBO.SettingUniforms += MSVOGen_SetUniforms;

            XRQuadFrameBuffer msvoBlurFBO = new(msvoBlurMat, true, (rawAoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = MSVOBlurFBOName
            };
            msvoBlurFBO.SettingUniforms += MSVOBlur_SetUniforms;

            XRFrameBuffer outputFbo = new((finalAoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = GBufferFBOFBOName
            };

            instance.SetFBO(msvoGenFBO);
            instance.SetFBO(msvoBlurFBO);
            instance.SetFBO(outputFbo);
        }

        private XRTexture ResolveAoTexture(
            XRRenderPipelineInstance instance,
            XRTexture? previousTexture,
            int width,
            int height,
            string textureName,
            string samplerName)
        {
            XRTexture? registeredTexture = instance.GetTexture<XRTexture>(textureName);
            if (registeredTexture is not null && TextureMatchesSize(registeredTexture, width, height))
            {
                ConfigureAoSampler(registeredTexture, samplerName);
                return registeredTexture;
            }

            if (previousTexture is not null && !ReferenceEquals(previousTexture, registeredTexture))
                previousTexture.Destroy();

            XRTexture createdTexture = CreateAoTexture(width, height, textureName, samplerName);
            instance.SetTexture(createdTexture);
            return createdTexture;
        }

        private static bool TextureMatchesSize(XRTexture texture, int width, int height)
        {
            Vector3 dims = texture.WidthHeightDepth;
            return (int)MathF.Round(dims.X) == Math.Max(width, 1) &&
                   (int)MathF.Round(dims.Y) == Math.Max(height, 1);
        }

        private static void ConfigureAoSampler(XRTexture texture, string samplerName)
        {
            texture.SamplerName = samplerName;
        }

        private XRTexture CreateAoTexture(int width, int height, string textureName, string samplerName)
        {
            if (Stereo)
            {
                var texture = XRTexture2DArray.CreateFrameBufferTexture(
                    2,
                    (uint)width,
                    (uint)height,
                    EPixelInternalFormat.R16f,
                    EPixelFormat.Red,
                    EPixelType.HalfFloat,
                    EFrameBufferAttachment.ColorAttachment0);
                texture.Resizable = false;
                texture.SizedInternalFormat = ESizedInternalFormat.R16f;
                texture.OVRMultiViewParameters = new(0, 2u);
                texture.Name = textureName;
                texture.SamplerName = samplerName;
                texture.MinFilter = ETexMinFilter.Nearest;
                texture.MagFilter = ETexMagFilter.Nearest;
                texture.UWrap = ETexWrapMode.ClampToEdge;
                texture.VWrap = ETexWrapMode.ClampToEdge;
                return texture;
            }

            var aoTexture = XRTexture2D.CreateFrameBufferTexture(
                (uint)width,
                (uint)height,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            aoTexture.Name = textureName;
            aoTexture.SamplerName = samplerName;
            aoTexture.MinFilter = ETexMinFilter.Nearest;
            aoTexture.MagFilter = ETexMagFilter.Nearest;
            aoTexture.UWrap = ETexWrapMode.ClampToEdge;
            aoTexture.VWrap = ETexWrapMode.ClampToEdge;
            return aoTexture;
        }

        private void MSVOGen_SetUniforms(XRRenderProgram program)
        {
            program.Uniform("ScaleFactors", ScaleFactors);

            var rc = ActivePipelineInstance.RenderState.SceneCamera;
            if (rc is null)
                return;

            rc.SetUniforms(program);

            if (RuntimeEngine.Rendering.State.IsStereoPass)
                ActivePipelineInstance.RenderState.StereoRightEyeCamera?.SetUniforms(program, false);

            rc.SetAmbientOcclusionUniforms(program, AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype);

            Debug.RenderingEvery(
                $"AO.MSVO.GenUniforms.{RuntimeHelpers.GetHashCode(ActivePipelineInstance)}",
                TimeSpan.FromSeconds(1),
                "[AO][MSVO] Gen depthMode={0} region={1}x{2} stereoPass={3} scaleFactors={4}",
                rc.DepthMode,
                ActivePipelineInstance.RenderState.CurrentRenderRegion.Width,
                ActivePipelineInstance.RenderState.CurrentRenderRegion.Height,
                RuntimeEngine.Rendering.State.IsStereoPass,
                ScaleFactors);
            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)ActivePipelineInstance.RenderState.CurrentRenderRegion.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)ActivePipelineInstance.RenderState.CurrentRenderRegion.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), Vector2.Zero);
        }

        private void MSVOBlur_SetUniforms(XRRenderProgram program)
        {
            var region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), region.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), region.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), Vector2.Zero);
        }

        private void InvalidateDependentFbos(XRRenderPipelineInstance instance)
        {
            if (DependentFboNames.Length == 0)
                return;

            foreach (string name in DependentFboNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                instance.Resources.RemoveFrameBuffer(name);
            }
        }

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            GetInstanceState(instance).ResourcesDirty = true;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (_instanceStates.TryGetValue(instance, out var state))
            {
                state.ResourcesDirty = true;
                state.RawAoTexture?.Destroy();
                state.RawAoTexture = null;
                state.FinalAoTexture?.Destroy();
                state.FinalAoTexture = null;
                state.LastWidth = 0;
                state.LastHeight = 0;
            }
        }
    }
}
