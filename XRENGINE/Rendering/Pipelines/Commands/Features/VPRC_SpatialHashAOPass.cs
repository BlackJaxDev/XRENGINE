using Extensions;
using System;
using System.IO;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Camera;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Builds the spatial hash buffers and dispatches the compute shader for the spatially hashed ray traced AO.
    /// </summary>
    public class VPRC_SpatialHashAOPass : ViewportRenderCommand
    {
        private const uint HashMapScale = 2u;
        private const uint LocalGroupSize = 8u;
        private const string ComputeShaderFile = "SpatialHashAO.comp";

        public const int DefaultSamples = 96;
        public const uint DefaultNoiseWidth = 4u;
        public const uint DefaultNoiseHeight = 4u;
        public const float DefaultMinSampleDist = 0.1f;
        public const float DefaultMaxSampleDist = 1.0f;

        public string IntensityTextureName { get; set; } = "SSAOIntensityTexture";
        public string GenerationFBOName { get; set; } = "SSAOFBO";
        public string BlurFBOName { get; set; } = "SSAOBlurFBO";
        public string OutputFBOName { get; set; } = "GBufferFBO";

        public string NormalTextureName { get; set; } = "Normal";
        public string DepthViewTextureName { get; set; } = "DepthView";
        public string AlbedoTextureName { get; set; } = "AlbedoOpacity";
        public string RMSETextureName { get; set; } = "RMSE";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";

        public int Samples { get; set; } = DefaultSamples;
        public uint NoiseWidth { get; set; } = DefaultNoiseWidth;
        public uint NoiseHeight { get; set; } = DefaultNoiseHeight;
        public float MinSampleDistance { get; set; } = DefaultMinSampleDist;
        public float MaxSampleDistance { get; set; } = DefaultMaxSampleDist;
        public bool Stereo { get; set; } = false;

        private XRTexture? _aoTexture;
        private XRRenderProgram? _computeProgram;
        private XRDataBuffer? _hashBuffer;
        private XRDataBuffer? _hashTimeBuffer;
        private XRDataBuffer? _spatialBuffer;

        private int _lastWidth;
        private int _lastHeight;
        private uint _hashCapacity;
        private uint _frameIndex;

        public void SetOptions(int samples, uint noiseWidth, uint noiseHeight, float minSampleDist, float maxSampleDist, bool stereo)
        {
            Samples = samples;
            NoiseWidth = noiseWidth;
            NoiseHeight = noiseHeight;
            MinSampleDistance = minSampleDist;
            MaxSampleDistance = maxSampleDist;
            Stereo = stereo;
        }

        public void SetGBufferInputTextureNames(string normal, string depthView, string albedo, string rmse, string depthStencil)
        {
            NormalTextureName = normal;
            DepthViewTextureName = depthView;
            AlbedoTextureName = albedo;
            RMSETextureName = rmse;
            DepthStencilTextureName = depthStencil;
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
            XRTexture? normalTex = ActivePipelineInstance.GetTexture<XRTexture>(NormalTextureName);
            XRTexture? depthViewTex = ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTextureName);
            XRTexture? albedoTex = ActivePipelineInstance.GetTexture<XRTexture>(AlbedoTextureName);
            XRTexture? rmseTex = ActivePipelineInstance.GetTexture<XRTexture>(RMSETextureName);
            XRTexture? depthStencilTex = ActivePipelineInstance.GetTexture<XRTexture>(DepthStencilTextureName);

            if (normalTex is null ||
                depthViewTex is null ||
                albedoTex is null ||
                rmseTex is null ||
                depthStencilTex is null)
                return;

            var area = Engine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
            if (width <= 0 || height <= 0)
                return;

            if (width != _lastWidth || height != _lastHeight)
            {
                RegenerateResources(
                    normalTex,
                    depthViewTex,
                    albedoTex,
                    rmseTex,
                    depthStencilTex,
                    width,
                    height);
            }

            EnsureComputeProgram();
            DispatchSpatialHashAO(normalTex, depthViewTex, width, height);
        }

        private void EnsureComputeProgram()
        {
            if (_computeProgram != null)
                return;

            XRShader compute = XRShader.EngineShader(Path.Combine("Compute", ComputeShaderFile), EShaderType.Compute);
            _computeProgram = new XRRenderProgram(true, false, compute);
        }

        private void RegenerateResources(
            XRTexture normalTex,
            XRTexture depthViewTex,
            XRTexture albedoTex,
            XRTexture rmseTex,
            XRTexture depthStencilTex,
            int width,
            int height)
        {
            _lastWidth = width;
            _lastHeight = height;

            _aoTexture?.Destroy();
            _aoTexture = CreateAOTexture(width, height);
            ActivePipelineInstance.SetTexture(_aoTexture);

            EnsureBuffers((uint)width, (uint)height);
            CreateFbos(albedoTex, normalTex, rmseTex, depthStencilTex);
        }

        private XRTexture CreateAOTexture(int width, int height)
        {
            if (Stereo)
            {
                var t = XRTexture2DArray.CreateFrameBufferTexture(
                    2,
                    (uint)width,
                    (uint)height,
                    EPixelInternalFormat.R16f,
                    EPixelFormat.Red,
                    EPixelType.HalfFloat,
                    EFrameBufferAttachment.ColorAttachment0);
                t.Resizable = false;
                t.SizedInternalFormat = ESizedInternalFormat.R16f;
                t.OVRMultiViewParameters = new(0, 2u);
                t.Name = IntensityTextureName;
                t.MinFilter = ETexMinFilter.Nearest;
                t.MagFilter = ETexMagFilter.Nearest;
                t.UWrap = ETexWrapMode.ClampToEdge;
                t.VWrap = ETexWrapMode.ClampToEdge;
                return t;
            }

            var tex = XRTexture2D.CreateFrameBufferTexture(
                (uint)width,
                (uint)height,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            tex.Name = IntensityTextureName;
            tex.MinFilter = ETexMinFilter.Nearest;
            tex.MagFilter = ETexMagFilter.Nearest;
            tex.UWrap = ETexWrapMode.ClampToEdge;
            tex.VWrap = ETexWrapMode.ClampToEdge;
            return tex;
        }

        private void CreateFbos(
            XRTexture albedoTex,
            XRTexture normalTex,
            XRTexture rmseTex,
            XRTexture depthStencilTex)
        {
            if (_aoTexture is not IFrameBufferAttachement aoAttach)
                throw new ArgumentException("Ambient occlusion texture must be an IFrameBufferAttachement");

            if (albedoTex is not IFrameBufferAttachement albedoAttach)
                throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");

            if (normalTex is not IFrameBufferAttachement normalAttach)
                throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");

            if (rmseTex is not IFrameBufferAttachement rmseAttach)
                throw new ArgumentException("RMSE texture must be an IFrameBufferAttachement");

            if (depthStencilTex is not IFrameBufferAttachement depthStencilAttach)
                throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");

            XRFrameBuffer genFbo = new(
                (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (depthStencilAttach, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = GenerationFBOName
            };

            XRFrameBuffer blurFbo = new((aoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = BlurFBOName
            };

            XRFrameBuffer outputFbo = new((aoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
            {
                Name = OutputFBOName,
            };

            ActivePipelineInstance.SetFBO(genFbo);
            ActivePipelineInstance.SetFBO(blurFbo);
            ActivePipelineInstance.SetFBO(outputFbo);
        }

        private void EnsureBuffers(uint width, uint height)
        {
            uint pixelCount = width * height;
            uint desiredCapacity = XRMath.NextPowerOfTwo(pixelCount * HashMapScale);
            if (desiredCapacity == 0)
                desiredCapacity = 1024;

            if (_hashBuffer != null && desiredCapacity == _hashCapacity)
                return;

            _hashCapacity = desiredCapacity;

            _hashBuffer?.Destroy();
            _hashTimeBuffer?.Destroy();
            _spatialBuffer?.Destroy();

            _hashBuffer = new XRDataBuffer("SpatialHashKeys", EBufferTarget.ShaderStorageBuffer, _hashCapacity, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.DynamicDraw,
                BindingIndexOverride = 1u,
                PadEndingToVec4 = false,
                DisposeOnPush = false
            };
            _hashBuffer.PushData();

            _hashTimeBuffer = new XRDataBuffer("SpatialHashTime", EBufferTarget.ShaderStorageBuffer, _hashCapacity, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.DynamicDraw,
                BindingIndexOverride = 2u,
                PadEndingToVec4 = false,
                DisposeOnPush = false
            };
            _hashTimeBuffer.PushData();

            _spatialBuffer = new XRDataBuffer("SpatialHashData", EBufferTarget.ShaderStorageBuffer, _hashCapacity, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.DynamicDraw,
                BindingIndexOverride = 3u,
                PadEndingToVec4 = false,
                DisposeOnPush = false
            };
            _spatialBuffer.PushData();
        }

        private void DispatchSpatialHashAO(XRTexture normalTex, XRTexture depthViewTex, int width, int height)
        {
            if (_computeProgram is null || _aoTexture is null || _hashBuffer is null || _hashTimeBuffer is null || _spatialBuffer is null)
                return;

            XRTexture? aoTexture = _aoTexture;
            bool layered = false;
            if (_aoTexture is XRTexture2DArray)
                layered = true;
            else if (_aoTexture is not XRTexture2D)
                return;

            var camera = ActivePipelineInstance.RenderState.SceneCamera;
            var settings = camera?.PostProcessing?.AmbientOcclusion;

            Matrix4x4 inverseView = camera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 viewMatrix = inverseView.Inverted();
            Matrix4x4 projMatrix = camera?.Parameters.GetProjectionMatrix() ?? Matrix4x4.Identity;
            Matrix4x4 inverseProj = projMatrix.Inverted();

            float hashRadius = settings?.Radius > 0.0f ? settings.Radius : 0.9f;
            float hashPower = settings?.Power > 0.0f ? settings.Power : 1.2f;
            int hashSteps = settings?.SpatialHashSteps > 0 ? settings.SpatialHashSteps : 6;
            float hashBias = settings?.Bias > 0.0f ? settings.Bias : 0.03f;
            float hashCell = settings?.SpatialHashCellSize > 0.0f ? settings.SpatialHashCellSize : 0.75f;
            float hashMaxDistance = settings?.SpatialHashMaxDistance > 0.0f ? settings.SpatialHashMaxDistance : 1.5f;
            float hashThickness = settings?.Thickness > 0.0f ? settings.Thickness : 0.1f;
            float hashFade = settings?.DistanceIntensity > 0.0f ? settings.DistanceIntensity : 1.0f;
            float spp = settings?.SamplesPerPixel > 0.0f ? settings.SamplesPerPixel : 1.0f;
            float jitterScale = 0.35f;
            float fovY = camera?.Parameters is XRPerspectiveCameraParameters persp
                ? XRMath.DegToRad(persp.VerticalFieldOfView)
                : XRMath.DegToRad(60.0f);

            _hashBuffer.BindTo(_computeProgram, 1u);
            _hashTimeBuffer.BindTo(_computeProgram, 2u);
            _spatialBuffer.BindTo(_computeProgram, 3u);

            _computeProgram.Sampler("NormalTex", normalTex, 0);
            _computeProgram.Sampler("DepthTex", depthViewTex, 1);
            _computeProgram.BindImageTexture(0u, aoTexture, 0, layered, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R16f);

            _computeProgram.Uniform("FrameIndex", _frameIndex++);
            _computeProgram.Uniform("HashMapSize", _hashCapacity);
            _computeProgram.Uniform("CellSizeMin", hashCell);
            _computeProgram.Uniform("Bias", hashBias);
            _computeProgram.Uniform("Thickness", hashThickness);
            _computeProgram.Uniform("MaxRayDistance", hashMaxDistance);
            _computeProgram.Uniform("DistanceFade", hashFade);
            _computeProgram.Uniform("Power", hashPower);
            _computeProgram.Uniform("SamplesPerPixel", spp);
            _computeProgram.Uniform("JitterScale", jitterScale);
            _computeProgram.Uniform("RayStepCount", hashSteps);
            _computeProgram.Uniform("Radius", hashRadius);
            _computeProgram.Uniform("FieldOfViewY", fovY);
            _computeProgram.Uniform("InvResolution", new Vector2(1.0f / width, 1.0f / height));
            _computeProgram.Uniform("InverseProjMatrix", inverseProj);
            _computeProgram.Uniform("ProjMatrix", projMatrix);
            _computeProgram.Uniform("InverseViewMatrix", inverseView);
            _computeProgram.Uniform("ViewMatrix", viewMatrix);

            uint groupX = (uint)(width + LocalGroupSize - 1) / LocalGroupSize;
            uint groupY = (uint)(height + LocalGroupSize - 1) / LocalGroupSize;
            _computeProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.TextureFetch | EMemoryBarrierMask.ShaderStorage);
        }
    }
}
