using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_AODisabledPass : ViewportRenderCommand
    {
        private static void LogStub(string key, string message)
            => Debug.RenderingEvery(
                $"AO.Stub.{key}",
                TimeSpan.FromSeconds(2),
                "[AO][Stub] {0}",
                message);

        private string DisabledShaderName()
            => Stereo ? "AODisabledStereo.fs" : "AODisabled.fs";

        public string IntensityTextureName { get; set; } = "AmbientOcclusionTexture";
        public string GenerationFBOName { get; set; } = "AmbientOcclusionFBO";
        public string BlurFBOName { get; set; } = "AmbientOcclusionBlurFBO";
        public string OutputFBOName { get; set; } = "GBufferFBO";

        public string NormalTextureName { get; set; } = "Normal";
        public string DepthViewTextureName { get; set; } = "DepthView";
        public string AlbedoTextureName { get; set; } = "AlbedoOpacity";
        public string RMSETextureName { get; set; } = "RMSE";
        public string TransformIdTextureName { get; set; } = "TransformId";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";
        public IReadOnlyList<string> DependentFboNames { get; set; } = Array.Empty<string>();

        public bool Stereo { get; set; }
        public string? StubLogKey { get; set; }
        public string? StubLogMessage { get; set; }

        private sealed class InstanceState
        {
            public bool ResourcesDirty = true;
            public int LastWidth;
            public int LastHeight;
            public XRTexture? AoTexture;
        }

        private static readonly ConditionalWeakTable<XRRenderPipelineInstance, InstanceState> _instanceStates = new();

        private InstanceState GetInstanceState(XRRenderPipelineInstance instance)
            => _instanceStates.GetValue(instance, _ => new InstanceState());

        public void SetOptions(bool stereo)
            => Stereo = stereo;

        public void SetStubInfo(string? key, string? message)
        {
            StubLogKey = key;
            StubLogMessage = message;
        }

        public void SetGBufferInputTextureNames(string normal, string depthView, string albedo, string rmse, string depthStencil, string transformId = "TransformId")
        {
            NormalTextureName = normal;
            DepthViewTextureName = depthView;
            AlbedoTextureName = albedo;
            RMSETextureName = rmse;
            DepthStencilTextureName = depthStencil;
            TransformIdTextureName = transformId;
        }

        public void SetOutputNames(string intensity, string generationFbo, string blurFbo, string outputFbo)
        {
            IntensityTextureName = intensity;
            GenerationFBOName = generationFbo;
            BlurFBOName = blurFbo;
            OutputFBOName = outputFbo;
        }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            var state = GetInstanceState(instance);

            if (!string.IsNullOrWhiteSpace(StubLogKey) && !string.IsNullOrWhiteSpace(StubLogMessage))
                LogStub(StubLogKey, StubLogMessage);

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
                return;

            var area = Engine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
            if (width <= 0 || height <= 0)
                return;

            bool forceRebuild = state.ResourcesDirty;
            state.ResourcesDirty = false;

            if (!forceRebuild)
            {
                XRTexture? registeredAo = instance.GetTexture<XRTexture>(IntensityTextureName);
                forceRebuild = state.AoTexture is null
                    || registeredAo is null
                    || !ReferenceEquals(state.AoTexture, registeredAo);
            }

            if (!forceRebuild && width == state.LastWidth && height == state.LastHeight)
                return;

            RegenerateResources(instance, state, albedoTex, normalTex, rmseTex, transformIdTex, depthStencilTex, width, height);
        }

        private void RegenerateResources(
            XRRenderPipelineInstance instance,
            InstanceState state,
            XRTexture albedoTex,
            XRTexture normalTex,
            XRTexture rmseTex,
            XRTexture transformIdTex,
            XRTexture depthStencilTex,
            int width,
            int height)
        {
            state.LastWidth = width;
            state.LastHeight = height;

            state.AoTexture?.Destroy();
            state.AoTexture = CreateAoTexture(width, height);
            instance.SetTexture(state.AoTexture);
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

            XRShader disabledShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, DisabledShaderName()), EShaderType.Fragment);
            XRMaterial genMaterial = new(Array.Empty<XRTexture>(), disabledShader) { RenderOptions = renderParams };
            XRMaterial blurMaterial = new(Array.Empty<XRTexture>(), disabledShader) { RenderOptions = renderParams };

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

            if (state.AoTexture is not IFrameBufferAttachement aoAttach)
                throw new ArgumentException("Ambient occlusion texture must be an IFrameBufferAttachement");

            XRQuadFrameBuffer generationFbo = new(genMaterial, true,
                (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (transformIdAttach, EFrameBufferAttachment.ColorAttachment3, 0, -1),
                (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = GenerationFBOName
            };

            XRQuadFrameBuffer blurFbo = new(blurMaterial, true, (aoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = BlurFBOName
            };

            XRFrameBuffer outputFbo = new((aoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = OutputFBOName
            };

            instance.SetFBO(generationFbo);
            instance.SetFBO(blurFbo);
            instance.SetFBO(outputFbo);
        }

        private XRTexture CreateAoTexture(int width, int height)
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
                texture.Name = IntensityTextureName;
                texture.SamplerName = IntensityTextureName;
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
            aoTexture.Name = IntensityTextureName;
            aoTexture.SamplerName = IntensityTextureName;
            aoTexture.MinFilter = ETexMinFilter.Nearest;
            aoTexture.MagFilter = ETexMagFilter.Nearest;
            aoTexture.UWrap = ETexWrapMode.ClampToEdge;
            aoTexture.VWrap = ETexWrapMode.ClampToEdge;
            return aoTexture;
        }

        private void InvalidateDependentFbos(XRRenderPipelineInstance instance)
        {
            if (DependentFboNames.Count == 0)
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
                state.AoTexture?.Destroy();
                state.AoTexture = null;
                state.LastWidth = 0;
                state.LastHeight = 0;
            }
        }
    }
}