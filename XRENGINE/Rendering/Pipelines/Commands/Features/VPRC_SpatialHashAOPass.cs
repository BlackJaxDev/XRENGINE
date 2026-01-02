using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using Extensions;
using XREngine;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Builds the spatial hash buffers and dispatches the compute shader for the spatially hashed ray traced AO.
    /// </summary>
    public class VPRC_SpatialHashAOPass : ViewportRenderCommand
    {
        private static void Log(string message)
            => Debug.Out(EOutputVerbosity.Normal, false, "[AO][SpatialHash] {0}", message);

        private const uint HashMapScale = 2u;
        private const uint LocalGroupSize = 8u;
        private const string ComputeShaderFile = "SpatialHashAO.comp";
        private const string ComputeShaderFileStereo = "SpatialHashAOStereo.comp";

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
        public string TransformIdTextureName { get; set; } = "TransformId";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";
        public IReadOnlyList<string> DependentFboNames { get; set; } = Array.Empty<string>();

        public int Samples { get; set; } = DefaultSamples;
        public uint NoiseWidth { get; set; } = DefaultNoiseWidth;
        public uint NoiseHeight { get; set; } = DefaultNoiseHeight;
        public float MinSampleDistance { get; set; } = DefaultMinSampleDist;
        public float MaxSampleDistance { get; set; } = DefaultMaxSampleDist;
        public bool Stereo { get; set; } = false;

        private XRRenderProgram? _computeProgram;
        private XRRenderProgram? _computeProgramStereo;

        // Per-instance state tracking
        private sealed class InstanceState
        {
            public bool ResourcesDirty = true;
            public int LastWidth;
            public int LastHeight;
            public uint HashCapacity;
            public uint FrameIndex;
            public XRTexture? AoTexture;
            public XRDataBuffer? HashBuffer;
            public XRDataBuffer? HashTimeBuffer;
            public XRDataBuffer? SpatialBuffer;
        }

        private static readonly ConditionalWeakTable<XRRenderPipelineInstance, InstanceState> _instanceStates = new();

        private InstanceState GetInstanceState(XRRenderPipelineInstance instance)
            => _instanceStates.GetValue(instance, _ => new InstanceState());

        public void SetOptions(int samples, uint noiseWidth, uint noiseHeight, float minSampleDist, float maxSampleDist, bool stereo)
        {
            Samples = samples;
            NoiseWidth = noiseWidth;
            NoiseHeight = noiseHeight;
            MinSampleDistance = minSampleDist;
            MaxSampleDistance = maxSampleDist;
            Stereo = stereo;
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
                Log("Skipping execute; required textures missing");
                return;
            }

            var area = Engine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
            if (width <= 0 || height <= 0)
                return;

            bool forceRebuild = state.ResourcesDirty;
            state.ResourcesDirty = false;

            if (!forceRebuild)
            {
                bool missingStateTexture = state.AoTexture is null;
                XRTexture? registeredAoTexture = instance.GetTexture<XRTexture>(IntensityTextureName);
                bool registryMissingTexture = state.AoTexture is not null && registeredAoTexture is null;
                bool registryReplacedTexture = state.AoTexture is not null && registeredAoTexture is not null && !ReferenceEquals(state.AoTexture, registeredAoTexture);

                if (missingStateTexture)
                {
                    Log("AO texture missing from state; forcing regenerate");
                    forceRebuild = true;
                }
                else if (registryMissingTexture)
                {
                    Log("AO texture not registered in pipeline; forcing regenerate");
                    forceRebuild = true;
                }
                else if (registryReplacedTexture)
                {
                    Log("AO texture replaced externally; forcing regenerate");
                    forceRebuild = true;
                }
            }

            Log($"Execute start: forceRebuild={forceRebuild}, last={state.LastWidth}x{state.LastHeight}, current={width}x{height}");

            if (forceRebuild || width != state.LastWidth || height != state.LastHeight)
            {
                RegenerateResources(
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

            EnsureComputeProgram();
            DispatchSpatialHashAO(state, normalTex, depthViewTex, width, height);
        }

        private void EnsureComputeProgram()
        {
            if (Stereo)
            {
                if (_computeProgramStereo != null)
                    return;

                XRShader compute = XRShader.EngineShader(Path.Combine("Compute", ComputeShaderFileStereo), EShaderType.Compute);
                _computeProgramStereo = new XRRenderProgram(true, false, compute);
            }
            else
            {
                if (_computeProgram != null)
                    return;

                XRShader compute = XRShader.EngineShader(Path.Combine("Compute", ComputeShaderFile), EShaderType.Compute);
                _computeProgram = new XRRenderProgram(true, false, compute);
            }
        }

        private void RegenerateResources(
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

            //Log($"Regenerating resources: size={width}x{height}, stereo={Stereo}");

            state.AoTexture?.Destroy();
            state.AoTexture = CreateAOTexture(width, height);
            PrimeTextureStorage(state.AoTexture);
            instance.SetTexture(state.AoTexture);
            InvalidateDependentFbos(instance);

            EnsureBuffers(state, (uint)width, (uint)height);
            CreateFbos(instance, state, albedoTex, normalTex, rmseTex, transformIdTex, depthStencilTex);
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
                t.SamplerName = IntensityTextureName;
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
            tex.SamplerName = IntensityTextureName;
            tex.MinFilter = ETexMinFilter.Nearest;
            tex.MagFilter = ETexMagFilter.Nearest;
            tex.UWrap = ETexWrapMode.ClampToEdge;
            tex.VWrap = ETexWrapMode.ClampToEdge;
            return tex;
        }

        private static void PrimeTextureStorage(XRTexture texture)
        {
            try
            {
                texture.PushData();
            }
            catch (Exception ex)
            {
                Log($"Failed to initialize AO texture storage: {ex.Message}");
            }
        }

        private void CreateFbos(
            XRRenderPipelineInstance instance,
            InstanceState state,
            XRTexture albedoTex,
            XRTexture normalTex,
            XRTexture rmseTex,
            XRTexture transformIdTex,
            XRTexture depthStencilTex)
        {
            if (state.AoTexture is not IFrameBufferAttachement aoAttach)
                throw new ArgumentException("Ambient occlusion texture must be an IFrameBufferAttachement");

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

            XRFrameBuffer genFbo = new(
                (albedoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (normalAttach, EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (rmseAttach, EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (transformIdAttach, EFrameBufferAttachment.ColorAttachment3, 0, -1),
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

            instance.SetFBO(genFbo);
            instance.SetFBO(blurFbo);
            instance.SetFBO(outputFbo);
            Log("Registered spatial hash AO FBOs");
        }

        private void EnsureBuffers(InstanceState state, uint width, uint height)
        {
            uint pixelCount = width * height;
            uint desiredCapacity = XRMath.NextPowerOfTwo(pixelCount * HashMapScale);
            if (desiredCapacity == 0)
                desiredCapacity = 1024;

            if (state.HashBuffer != null && desiredCapacity == state.HashCapacity)
                return;

            state.HashCapacity = desiredCapacity;

            state.HashBuffer?.Destroy();
            state.HashTimeBuffer?.Destroy();
            state.SpatialBuffer?.Destroy();

            state.HashBuffer = new XRDataBuffer("SpatialHashKeys", EBufferTarget.ShaderStorageBuffer, state.HashCapacity, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.DynamicDraw,
                BindingIndexOverride = 1u,
                PadEndingToVec4 = false,
                DisposeOnPush = false
            };
            state.HashBuffer.PushData();

            state.HashTimeBuffer = new XRDataBuffer("SpatialHashTime", EBufferTarget.ShaderStorageBuffer, state.HashCapacity, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.DynamicDraw,
                BindingIndexOverride = 2u,
                PadEndingToVec4 = false,
                DisposeOnPush = false
            };
            state.HashTimeBuffer.PushData();

            state.SpatialBuffer = new XRDataBuffer("SpatialHashData", EBufferTarget.ShaderStorageBuffer, state.HashCapacity, EComponentType.UInt, 2, false, true)
            {
                Usage = EBufferUsage.DynamicDraw,
                BindingIndexOverride = 3u,
                PadEndingToVec4 = false,
                DisposeOnPush = false
            };
            state.SpatialBuffer.PushData();
            Log($"Recreated buffers capacity={state.HashCapacity}");
        }

        private void DispatchSpatialHashAO(InstanceState state, XRTexture normalTex, XRTexture depthViewTex, int width, int height)
        {
            if (state.AoTexture is null || state.HashBuffer is null || state.HashTimeBuffer is null || state.SpatialBuffer is null)
                return;

            XRTexture? aoTexture = state.AoTexture;
            bool layered = state.AoTexture is XRTexture2DArray;
            if (!layered && state.AoTexture is not XRTexture2D)
                return;

            var camera = ActivePipelineInstance.RenderState.SceneCamera;
            var stage = camera?.GetPostProcessStageState<AmbientOcclusionSettings>();
            var settings = stage?.TryGetBacking(out AmbientOcclusionSettings? backing) == true ? backing : null;

            float hashRadius = settings?.Radius > 0.0f ? settings.Radius : 2.0f;  // Max ray distance in world units
            float hashPower = settings?.Power > 0.0f ? settings.Power : 1.0f;
            int hashSteps = settings?.SpatialHashSteps > 0 ? settings.SpatialHashSteps : 8;
            float hashBias = settings?.Bias > 0.0f ? settings.Bias : 0.01f;
            float hashCellMin = settings?.SpatialHashCellSize > 0.0f ? settings.SpatialHashCellSize : 0.07f;  // smin - smallest cell
            float hashThickness = settings?.Thickness > 0.0f ? settings.Thickness : 0.5f;
            float jitterScale = settings?.SpatialHashJitterScale >= 0.0f ? settings.SpatialHashJitterScale : 0.35f;

            // SamplesPerPixel (sp) controls feature size in screen pixels for adaptive cell sizing
            float spp = settings?.SamplesPerPixel > 0.0f ? settings.SamplesPerPixel : 3.0f;

            if (spp < 1.0f)
                spp = 1.0f;

            float fovY = camera?.Parameters is XRPerspectiveCameraParameters persp
                ? XRMath.DegToRad(persp.VerticalFieldOfView)
                : XRMath.DegToRad(60.0f);

            if (Stereo && layered)
            {
                DispatchStereo(state, normalTex, depthViewTex, aoTexture, width, height,
                    hashRadius, hashPower, hashSteps, hashBias, hashCellMin,
                    hashThickness, spp, jitterScale, fovY);
            }
            else
            {
                DispatchMono(state, normalTex, depthViewTex, aoTexture, width, height,
                    hashRadius, hashPower, hashSteps, hashBias, hashCellMin,
                    hashThickness, spp, jitterScale, fovY, camera);
            }
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
                //Log($"Invalidated dependent FBO '{name}'");
            }
        }

        private void DispatchMono(
            InstanceState state,
            XRTexture normalTex, XRTexture depthViewTex, XRTexture aoTexture,
            int width, int height,
            float hashRadius, float hashPower, int hashSteps, float hashBias,
            float hashCellMin, float hashThickness,
            float spp, float jitterScale, float fovY, XRCamera? camera)
        {
            if (_computeProgram is null)
                return;

            Matrix4x4 inverseView = camera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 viewMatrix = inverseView.Inverted();
            Matrix4x4 projMatrix = camera?.Parameters.GetProjectionMatrix() ?? Matrix4x4.Identity;
            Matrix4x4 inverseProj = projMatrix.Inverted();

            state.HashBuffer!.BindTo(_computeProgram, 1u);
            state.HashTimeBuffer!.BindTo(_computeProgram, 2u);
            state.SpatialBuffer!.BindTo(_computeProgram, 3u);

            _computeProgram.Sampler("NormalTex", normalTex, 0);
            _computeProgram.Sampler("DepthTex", depthViewTex, 1);
            _computeProgram.BindImageTexture(0u, aoTexture, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R16F);

            _computeProgram.Uniform("FrameIndex", state.FrameIndex++);
            _computeProgram.Uniform("HashMapSize", state.HashCapacity);
            _computeProgram.Uniform("CellSizeMin", hashCellMin);
            _computeProgram.Uniform("Bias", hashBias);
            _computeProgram.Uniform("Thickness", hashThickness);
            _computeProgram.Uniform("Power", hashPower);
            _computeProgram.Uniform("SamplesPerPixel", spp);
            _computeProgram.Uniform("JitterScale", jitterScale);
            _computeProgram.Uniform("RayStepCount", hashSteps);
            _computeProgram.Uniform("Radius", hashRadius);  // Used as max ray distance
            _computeProgram.Uniform("FieldOfViewY", fovY);
            _computeProgram.Uniform("InvResolution", new Vector2(1.0f / width, 1.0f / height));
            _computeProgram.Uniform("InverseProjMatrix", inverseProj);
            _computeProgram.Uniform("ProjMatrix", projMatrix);
            _computeProgram.Uniform("InverseViewMatrix", inverseView);
            _computeProgram.Uniform("ViewMatrix", viewMatrix);

            uint groupX = (uint)(width + LocalGroupSize - 1) / LocalGroupSize;
            uint groupY = (uint)(height + LocalGroupSize - 1) / LocalGroupSize;
            _computeProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.TextureFetch | EMemoryBarrierMask.ShaderStorage);
            Log($"Dispatch mono: frameIndex={state.FrameIndex}, groups={groupX}x{groupY}, hashCapacity={state.HashCapacity}");
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
            state.AoTexture?.Destroy();
            state.AoTexture = null;
            state.HashBuffer?.Destroy();
            state.HashBuffer = null;
            state.HashTimeBuffer?.Destroy();
            state.HashTimeBuffer = null;
            state.SpatialBuffer?.Destroy();
            state.SpatialBuffer = null;
            state.LastWidth = 0;
            state.LastHeight = 0;
            state.HashCapacity = 0;
        }

        private void DispatchStereo(
            InstanceState state,
            XRTexture normalTex, XRTexture depthViewTex, XRTexture aoTexture,
            int width, int height,
            float hashRadius, float hashPower, int hashSteps, float hashBias,
            float hashCellMin, float hashThickness,
            float spp, float jitterScale, float fovY)
        {
            if (_computeProgramStereo is null)
                return;

            var renderState = ActivePipelineInstance.RenderState;
            var leftCamera = renderState.SceneCamera;
            var rightCamera = renderState.StereoRightEyeCamera;

            if (leftCamera is null)
                return;

            // Get left eye matrices
            Matrix4x4 leftInverseView = leftCamera.Transform.RenderMatrix;
            Matrix4x4 leftViewMatrix = leftInverseView.Inverted();
            Matrix4x4 leftProjMatrix = leftCamera.ProjectionMatrix;
            Matrix4x4 leftInverseProj = leftProjMatrix.Inverted();

            // Get right eye matrices (fallback to left if right not available)
            Matrix4x4 rightInverseView = rightCamera?.Transform.RenderMatrix ?? leftInverseView;
            Matrix4x4 rightViewMatrix = rightInverseView.Inverted();
            Matrix4x4 rightProjMatrix = rightCamera?.ProjectionMatrix ?? leftProjMatrix;
            Matrix4x4 rightInverseProj = rightProjMatrix.Inverted();

            state.HashBuffer!.BindTo(_computeProgramStereo, 1u);
            state.HashTimeBuffer!.BindTo(_computeProgramStereo, 2u);
            state.SpatialBuffer!.BindTo(_computeProgramStereo, 3u);

            _computeProgramStereo.Sampler("NormalTex", normalTex, 0);
            _computeProgramStereo.Sampler("DepthTex", depthViewTex, 1);
            _computeProgramStereo.BindImageTexture(0u, aoTexture, 0, true, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R16F);

            _computeProgramStereo.Uniform("FrameIndex", state.FrameIndex++);
            _computeProgramStereo.Uniform("HashMapSize", state.HashCapacity);
            _computeProgramStereo.Uniform("CellSizeMin", hashCellMin);
            _computeProgramStereo.Uniform("Bias", hashBias);
            _computeProgramStereo.Uniform("Thickness", hashThickness);
            _computeProgramStereo.Uniform("Power", hashPower);
            _computeProgramStereo.Uniform("SamplesPerPixel", spp);
            _computeProgramStereo.Uniform("JitterScale", jitterScale);
            _computeProgramStereo.Uniform("RayStepCount", hashSteps);
            _computeProgramStereo.Uniform("Radius", hashRadius);  // Used as max ray distance
            _computeProgramStereo.Uniform("FieldOfViewY", fovY);
            _computeProgramStereo.Uniform("InvResolution", new Vector2(1.0f / width, 1.0f / height));

            // Left eye matrices
            _computeProgramStereo.Uniform("LeftEyeInverseProjMatrix", leftInverseProj);
            _computeProgramStereo.Uniform("LeftEyeProjMatrix", leftProjMatrix);
            _computeProgramStereo.Uniform("LeftEyeInverseViewMatrix", leftInverseView);
            _computeProgramStereo.Uniform("LeftEyeViewMatrix", leftViewMatrix);

            // Right eye matrices
            _computeProgramStereo.Uniform("RightEyeInverseProjMatrix", rightInverseProj);
            _computeProgramStereo.Uniform("RightEyeProjMatrix", rightProjMatrix);
            _computeProgramStereo.Uniform("RightEyeInverseViewMatrix", rightInverseView);
            _computeProgramStereo.Uniform("RightEyeViewMatrix", rightViewMatrix);

            uint groupX = (uint)(width + LocalGroupSize - 1) / LocalGroupSize;
            uint groupY = (uint)(height + LocalGroupSize - 1) / LocalGroupSize;
            _computeProgramStereo.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.TextureFetch | EMemoryBarrierMask.ShaderStorage);
            Log($"Dispatch stereo: frameIndex={state.FrameIndex}, groups={groupX}x{groupY}, hashCapacity={state.HashCapacity}");
        }
    }
}
