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
    public class VPRC_AODisabledPass : ViewportRenderCommand, IDeclaredAoResourceProvider
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
        public string[] DependentFboNames { get; set; } = Array.Empty<string>();

        public bool Stereo { get; set; }
        public string? StubLogKey { get; set; }
        public string? StubLogMessage { get; set; }

        private sealed class InstanceState
        {
            public bool ResourcesDirty = true;
            public int LastWidth;
            public int LastHeight;
            public XRTexture? AoTexture;
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

            var area = RuntimeEngine.Rendering.State.RenderArea;
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
                    || !ReferenceEquals(state.AoTexture, registeredAo)
                    || !ReferenceEquals(state.NormalTexture, normalTex)
                    || !ReferenceEquals(state.DepthViewTexture, depthViewTex)
                    || !ReferenceEquals(state.AlbedoTexture, albedoTex)
                    || !ReferenceEquals(state.RmseTexture, rmseTex)
                    || !ReferenceEquals(state.TransformIdTexture, transformIdTex)
                    || !ReferenceEquals(state.DepthStencilTexture, depthStencilTex);
            }

            if (!forceRebuild)
                forceRebuild = !instance.TryGetFBO(GenerationFBOName, out _);

            if (!forceRebuild && width == state.LastWidth && height == state.LastHeight)
                return;

            RefreshDeclaredResources(instance, state, albedoTex, normalTex, rmseTex, transformIdTex, depthStencilTex, width, height);
        }

        public XRFrameBuffer CreateDeclaredFrameBuffer(XRRenderPipelineInstance instance, string name)
        {
            XRTexture aoTexture = instance.GetTexture<XRTexture>(IntensityTextureName)
                ?? throw new InvalidOperationException($"Missing declared AO texture '{IntensityTextureName}'.");

            RenderingParameters renderParams = CreateRenderParameters();
            XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, DisabledShaderName()), EShaderType.Fragment);
            XRMaterial material = new(Array.Empty<XRTexture>(), shader) { RenderOptions = renderParams };

            if (string.Equals(name, BlurFBOName, StringComparison.Ordinal))
            {
                return new XRQuadFrameBuffer(material, true, (RequireAttachment(aoTexture, IntensityTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = BlurFBOName
                };
            }

            if (!string.Equals(name, GenerationFBOName, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsupported disabled AO framebuffer '{name}'.");

            return new XRQuadFrameBuffer(material, true,
                (RequireAttachment(instance, AlbedoTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (RequireAttachment(instance, NormalTextureName), EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (RequireAttachment(instance, RMSETextureName), EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (RequireAttachment(instance, TransformIdTextureName), EFrameBufferAttachment.ColorAttachment3, 0, -1),
                (RequireAttachment(instance, DepthStencilTextureName), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = GenerationFBOName
            };
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

        private static IFrameBufferAttachement RequireAttachment(XRRenderPipelineInstance instance, string textureName)
            => RequireAttachment(
                instance.GetTexture<XRTexture>(textureName)
                    ?? throw new InvalidOperationException($"Missing declared AO input '{textureName}'."),
                textureName);

        private static IFrameBufferAttachement RequireAttachment(XRTexture texture, string textureName)
            => texture as IFrameBufferAttachement
                ?? throw new InvalidOperationException($"Declared AO texture '{textureName}' is not framebuffer-attachable.");

        private void RefreshDeclaredResources(
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
            state.AlbedoTexture = albedoTex;
            state.NormalTexture = normalTex;
            state.RmseTexture = rmseTex;
            state.TransformIdTexture = transformIdTex;
            state.DepthStencilTexture = depthStencilTex;
            state.DepthViewTexture = instance.GetTexture<XRTexture>(DepthViewTextureName);
            state.AoTexture = instance.GetTexture<XRTexture>(IntensityTextureName)
                ?? throw new InvalidOperationException($"Missing declared AO texture '{IntensityTextureName}'.");

            if (!instance.TryGetFBO(GenerationFBOName, out _) || !instance.TryGetFBO(BlurFBOName, out _))
                throw new InvalidOperationException("Disabled AO command requires its declared generation and blur framebuffers.");
        }

        private XRTexture ResolveAoTexture(
            XRRenderPipelineInstance instance,
            int width,
            int height)
        {
            XRTexture? registeredTexture = instance.GetTexture<XRTexture>(IntensityTextureName);
            if (registeredTexture is not null && TextureMatchesSize(registeredTexture, width, height))
            {
                ConfigureAoSampler(registeredTexture);
                return registeredTexture;
            }

            throw new InvalidOperationException(
                $"Declared AO texture '{IntensityTextureName}' is missing or does not match {width}x{height}.");
        }

        private static bool TextureMatchesSize(XRTexture texture, int width, int height)
        {
            Vector3 dims = texture.WidthHeightDepth;
            return (int)MathF.Round(dims.X) == Math.Max(width, 1) &&
                   (int)MathF.Round(dims.Y) == Math.Max(height, 1);
        }

        private void ConfigureAoSampler(XRTexture texture)
        {
            texture.SamplerName = IntensityTextureName;
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

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            GetInstanceState(instance).ResourcesDirty = true;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (_instanceStates.TryGetValue(instance, out var state))
            {
                state.ResourcesDirty = true;
                state.AoTexture = null;
                state.LastWidth = 0;
                state.LastHeight = 0;
            }
        }
    }
}
