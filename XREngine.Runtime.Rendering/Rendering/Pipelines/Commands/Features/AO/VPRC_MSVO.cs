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
    public class VPRC_MSVO : ViewportRenderCommand, IDeclaredAoResourceProvider
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

            RefreshDeclaredResources(
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

        private void RefreshDeclaredResources(
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

            state.RawAoTexture = ResolveDeclaredAoTexture(instance, width, height, MSVORawTextureName, MSVOIntensityTextureName);
            state.FinalAoTexture = ResolveDeclaredAoTexture(instance, width, height, MSVOIntensityTextureName, MSVOIntensityTextureName);

            if (!instance.TryGetFBO(MSVOFBOName, out _) || !instance.TryGetFBO(MSVOBlurFBOName, out _))
                throw new InvalidOperationException("MSVO command requires its declared generation and blur framebuffers.");
        }

        public XRFrameBuffer CreateDeclaredFrameBuffer(XRRenderPipelineInstance instance, string name)
        {
            XRTexture rawAoTexture = RequireTexture(instance, MSVORawTextureName);
            XRTexture finalAoTexture = RequireTexture(instance, MSVOIntensityTextureName);

            if (string.Equals(name, GBufferFBOFBOName, StringComparison.Ordinal))
            {
                return new XRFrameBuffer((RequireAttachment(finalAoTexture, MSVOIntensityTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = GBufferFBOFBOName
                };
            }

            RenderingParameters renderParams = CreateRenderParameters();

            XRShader msvoGenShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, MSVOGenShaderName()), EShaderType.Fragment);
            XRShader msvoBlurShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, MSVOBlurShaderName()), EShaderType.Fragment);

            if (string.Equals(name, MSVOFBOName, StringComparison.Ordinal))
            {
                XRMaterial msvoGenMaterial = new(
                    [
                        RequireTexture(instance, NormalTextureName),
                        RequireTexture(instance, DepthViewTextureName),
                    ],
                    msvoGenShader)
                {
                    RenderOptions = renderParams
                };

                XRQuadFrameBuffer msvoGenFbo = new(msvoGenMaterial, true,
                    (RequireAttachment(instance, AlbedoTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1),
                    (RequireAttachment(instance, NormalTextureName), EFrameBufferAttachment.ColorAttachment1, 0, -1),
                    (RequireAttachment(instance, RMSETextureName), EFrameBufferAttachment.ColorAttachment2, 0, -1),
                    (RequireAttachment(instance, TransformIdTextureName), EFrameBufferAttachment.ColorAttachment3, 0, -1),
                    (RequireAttachment(instance, DepthStencilTextureName), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
                {
                    Name = MSVOFBOName
                };
                msvoGenFbo.SettingUniforms += MSVOGen_SetUniforms;
                return msvoGenFbo;
            }

            if (string.Equals(name, MSVOBlurFBOName, StringComparison.Ordinal))
            {
                XRMaterial msvoBlurMaterial = new([rawAoTexture], msvoBlurShader) { RenderOptions = renderParams };
                XRQuadFrameBuffer msvoBlurFbo = new(
                    msvoBlurMaterial,
                    true,
                    (RequireAttachment(rawAoTexture, MSVORawTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = MSVOBlurFBOName
                };
                msvoBlurFbo.SettingUniforms += MSVOBlur_SetUniforms;
                return msvoBlurFbo;
            }

            throw new InvalidOperationException($"Unsupported MSVO framebuffer '{name}'.");
        }

        private XRTexture ResolveDeclaredAoTexture(
            XRRenderPipelineInstance instance,
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

            throw new InvalidOperationException(
                $"Declared MSVO texture '{textureName}' is missing or does not match {width}x{height}.");
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
                ?? throw new InvalidOperationException($"Missing declared MSVO texture '{textureName}'.");

        private static IFrameBufferAttachement RequireAttachment(XRRenderPipelineInstance instance, string textureName)
            => RequireAttachment(RequireTexture(instance, textureName), textureName);

        private static IFrameBufferAttachement RequireAttachment(XRTexture texture, string textureName)
            => texture as IFrameBufferAttachement
                ?? throw new InvalidOperationException($"Declared MSVO texture '{textureName}' is not framebuffer-attachable.");

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

            rc.SetAmbientOcclusionUniforms(
                program,
                AmbientOcclusionSettings.EType.MultiRadiusObscurancePrototype,
                ActivePipelineInstance.AssignedPipeline ?? ParentPipeline);

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
                state.FinalAoTexture = null;
                state.LastWidth = 0;
                state.LastHeight = 0;
            }
        }
    }
}
