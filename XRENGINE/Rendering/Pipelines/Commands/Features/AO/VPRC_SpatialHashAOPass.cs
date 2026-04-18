using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Extensions;
using XREngine;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Builds the spatial hash buffers and dispatches the compute shader for the spatially hashed ray traced AO.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_SpatialHashAOPass : ViewportRenderCommand
    {
        private static void Log(string message)
            => Debug.Out(EOutputVerbosity.Normal, false, "[AO][SpatialHash] {0}", message);

        private const uint HashMapScale = 2u;
        private const uint LocalGroupSize = 8u;
        private const uint ShaderMaxFrameAge = 20u;
        private const string ComputeShaderFile = "AO/SpatialHashAO.comp";
        private const string ComputeShaderFileStereo = "AO/SpatialHashAOStereo.comp";

        public const int DefaultSamples = 96;
        public const uint DefaultNoiseWidth = 4u;
        public const uint DefaultNoiseHeight = 4u;
        public const float DefaultMinSampleDist = 0.1f;
        public const float DefaultMaxSampleDist = 1.0f;

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
            public int SettingsHash;
            public uint HashCapacity;
            public uint FrameIndex;
            public bool HistoryReady;
            public bool HasCachedViewProjection;
            public bool UseHistoryTextureA = true;
            public Matrix4x4 LastUnjitteredViewProjection = Matrix4x4.Identity;
            public XRTexture? AoTexture;
            public XRTexture? HistoryAoTextureA;
            public XRTexture? HistoryAoTextureB;
            public XRTexture? HistoryDepthTextureA;
            public XRTexture? HistoryDepthTextureB;
            public XRDataBuffer? HashBuffer;
            public XRDataBuffer? HashTimeBuffer;
            public XRDataBuffer? SpatialBuffer;
        }

        private readonly struct SpatialHashRuntimeSettings
        {
            public float Radius { get; init; }
            public float Power { get; init; }
            public int Steps { get; init; }
            public float Bias { get; init; }
            public float CellSizeMin { get; init; }
            public float Thickness { get; init; }
            public float DistanceFade { get; init; }
            public float SamplesPerPixel { get; init; }
            public float JitterScale { get; init; }
            public uint MaxSamplesPerCell { get; init; }
            public bool TemporalReuseEnabled { get; init; }
            public float TemporalBlendFactor { get; init; }
            public float TemporalClamp { get; init; }
            public float TemporalDepthRejectThreshold { get; init; }
            public float TemporalMotionRejectionScale { get; init; }
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

            var camera = instance.RenderState.SceneCamera;
            var stage = camera?.GetPostProcessStageState<AmbientOcclusionSettings>();
            var settings = stage?.TryGetBacking(out AmbientOcclusionSettings? backing) == true ? backing : null;
            SpatialHashRuntimeSettings runtimeSettings = ResolveRuntimeSettings(settings);

            VPRC_TemporalAccumulationPass.TemporalUniformData temporalData = default;
            bool hasTemporalData = !Stereo && VPRC_TemporalAccumulationPass.TryGetTemporalUniformData(out temporalData);
            int settingsHash = ComputeSettingsHash(runtimeSettings, Stereo);

            bool forceRebuild = state.ResourcesDirty;
            state.ResourcesDirty = false;

            if (state.SettingsHash != settingsHash)
            {
                state.SettingsHash = settingsHash;
                forceRebuild = true;
            }

            Matrix4x4 currentUnjitteredViewProjection = GetCurrentUnjitteredViewProjection(camera, temporalData, hasTemporalData);
            bool cameraMovedSinceLastFrame = !Stereo && !forceRebuild && ShouldInvalidateForCameraMotion(state, currentUnjitteredViewProjection);

            if (!Stereo && runtimeSettings.TemporalReuseEnabled && state.HistoryReady && (!hasTemporalData || !temporalData.HistoryReady))
                forceRebuild = true;

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

            Debug.RenderingEvery(
                $"AO.SpatialHash.Execute.{RuntimeHelpers.GetHashCode(instance)}",
                TimeSpan.FromSeconds(1),
                "[AO][SpatialHash] Execute forceRebuild={0} size={1}x{2} stereo={3} normal={4} depth={5} output={6}",
                forceRebuild,
                width,
                height,
                Stereo,
                normalTex.Name ?? "null",
                depthViewTex.Name ?? "null",
                IntensityTextureName);

            if (!forceRebuild)
                forceRebuild = !instance.TryGetFBO(GenerationFBOName, out _);

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

            // Soft-clear spatial hash data when the camera moves: zero out the
            // SSBO hit/sample/key buffers so stale view-dependent occlusion data
            // is discarded, but keep temporal history textures intact so the
            // reprojection pass can smooth the reconvergence.
            if (cameraMovedSinceLastFrame && !forceRebuild)
                ClearSpatialHashData(state);

            EnsureComputeProgram();
            DispatchSpatialHashAO(state, normalTex, depthViewTex, transformIdTex, width, height, runtimeSettings, hasTemporalData ? temporalData : default, hasTemporalData);
        }

        private static void ClearSpatialHashData(InstanceState state)
        {
            // Advancing FrameIndex past the shader's MaxFrameAge window makes
            // every cell appear stale on next lookup. The shader will lazily
            // re-initialize each cell as it's accessed — no GPU clear needed.
            state.FrameIndex += ShaderMaxFrameAge + 1;
        }

        private static Matrix4x4 GetCurrentUnjitteredViewProjection(
            XRCamera? camera,
            VPRC_TemporalAccumulationPass.TemporalUniformData temporalData,
            bool hasTemporalData)
        {
            if (hasTemporalData)
                return temporalData.CurrViewProjectionUnjittered;

            if (camera is null)
                return Matrix4x4.Identity;

            Matrix4x4 viewMatrix = camera.Transform.InverseRenderMatrix;
            return viewMatrix * camera.ProjectionMatrixUnjittered;
        }

        private static bool ShouldInvalidateForCameraMotion(InstanceState state, in Matrix4x4 currentUnjitteredViewProjection)
        {
            if (!state.HasCachedViewProjection)
            {
                state.HasCachedViewProjection = true;
                state.LastUnjitteredViewProjection = currentUnjitteredViewProjection;
                return false;
            }

            if (IsMatrixApproximatelyEqual(state.LastUnjitteredViewProjection, currentUnjitteredViewProjection, 1e-4f))
                return false;

            state.LastUnjitteredViewProjection = currentUnjitteredViewProjection;
            return true;
        }

        private static bool IsMatrixApproximatelyEqual(in Matrix4x4 a, in Matrix4x4 b, float epsilon)
        {
            return MathF.Abs(a.M11 - b.M11) < epsilon && MathF.Abs(a.M12 - b.M12) < epsilon && MathF.Abs(a.M13 - b.M13) < epsilon && MathF.Abs(a.M14 - b.M14) < epsilon &&
                   MathF.Abs(a.M21 - b.M21) < epsilon && MathF.Abs(a.M22 - b.M22) < epsilon && MathF.Abs(a.M23 - b.M23) < epsilon && MathF.Abs(a.M24 - b.M24) < epsilon &&
                   MathF.Abs(a.M31 - b.M31) < epsilon && MathF.Abs(a.M32 - b.M32) < epsilon && MathF.Abs(a.M33 - b.M33) < epsilon && MathF.Abs(a.M34 - b.M34) < epsilon &&
                   MathF.Abs(a.M41 - b.M41) < epsilon && MathF.Abs(a.M42 - b.M42) < epsilon && MathF.Abs(a.M43 - b.M43) < epsilon && MathF.Abs(a.M44 - b.M44) < epsilon;
        }

        private static SpatialHashRuntimeSettings ResolveRuntimeSettings(AmbientOcclusionSettings? settings)
        {
            float spatialMaxDistance = settings?.SpatialHashMaxDistance > 0.0f ? settings.SpatialHashMaxDistance : 0.0f;
            float fallbackRadius = settings?.Radius > 0.0f ? settings.Radius : 2.0f;

            return new SpatialHashRuntimeSettings
            {
                Radius = spatialMaxDistance > 0.0f ? spatialMaxDistance : fallbackRadius,
                Power = settings?.Power > 0.0f ? settings.Power : 1.0f,
                Steps = settings?.SpatialHashSteps > 0 ? settings.SpatialHashSteps : 8,
                Bias = settings?.Bias > 0.0f ? settings.Bias : 0.01f,
                CellSizeMin = settings?.SpatialHashCellSize > 0.0f ? settings.SpatialHashCellSize : 0.07f,
                Thickness = settings?.Thickness > 0.0f ? settings.Thickness : 0.5f,
                DistanceFade = Math.Max(settings?.DistanceIntensity ?? 1.0f, 0.0f),
                SamplesPerPixel = Math.Max(settings?.SamplesPerPixel ?? 3.0f, 1.0f),
                JitterScale = settings?.SpatialHashJitterScale >= 0.0f ? settings.SpatialHashJitterScale : 0.35f,
                MaxSamplesPerCell = (uint)Math.Clamp(settings?.Samples > 0 ? settings.Samples : DefaultSamples, 1, 4096),
                TemporalReuseEnabled = settings?.SpatialHashTemporalReuseEnabled ?? true,
                TemporalBlendFactor = Math.Clamp(settings?.SpatialHashTemporalBlendFactor ?? 0.9f, 0.0f, 0.99f),
                TemporalClamp = Math.Max(settings?.SpatialHashTemporalClamp ?? 0.2f, 0.001f),
                TemporalDepthRejectThreshold = Math.Max(settings?.SpatialHashTemporalDepthRejectThreshold ?? 0.01f, 0.0001f),
                TemporalMotionRejectionScale = Math.Max(settings?.SpatialHashTemporalMotionRejectionScale ?? 0.2f, 0.0001f)
            };
        }

        private static int ComputeSettingsHash(in SpatialHashRuntimeSettings settings, bool stereo)
        {
            HashCode hash = new();
            hash.Add(settings.Radius);
            hash.Add(settings.Power);
            hash.Add(settings.Steps);
            hash.Add(settings.Bias);
            hash.Add(settings.CellSizeMin);
            hash.Add(settings.Thickness);
            hash.Add(settings.DistanceFade);
            hash.Add(settings.SamplesPerPixel);
            hash.Add(settings.JitterScale);
            hash.Add(settings.MaxSamplesPerCell);
            hash.Add(settings.TemporalReuseEnabled);
            hash.Add(settings.TemporalBlendFactor);
            hash.Add(settings.TemporalClamp);
            hash.Add(settings.TemporalDepthRejectThreshold);
            hash.Add(settings.TemporalMotionRejectionScale);
            hash.Add(stereo);
            return hash.ToHashCode();
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
            state.FrameIndex = 0;
            state.HistoryReady = false;
            state.HasCachedViewProjection = false;
            state.UseHistoryTextureA = true;

            //Log($"Regenerating resources: size={width}x{height}, stereo={Stereo}");

            state.AoTexture?.Destroy();
            state.AoTexture = CreateAOTexture(width, height);
            PrimeTextureStorage(state.AoTexture);
            DestroyHistoryTextures(state);
            CreateHistoryTextures(state, width, height);
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
            tex.RequiresStorageUsage = true;
            return tex;
        }

        private void CreateHistoryTextures(InstanceState state, int width, int height)
        {
            state.HistoryAoTextureA = CreateHistoryAoTexture(width, height);
            state.HistoryAoTextureB = CreateHistoryAoTexture(width, height);
            state.HistoryDepthTextureA = CreateHistoryDepthTexture(width, height);
            state.HistoryDepthTextureB = CreateHistoryDepthTexture(width, height);

            PrimeTextureStorage(state.HistoryAoTextureA);
            PrimeTextureStorage(state.HistoryAoTextureB);
            PrimeTextureStorage(state.HistoryDepthTextureA);
            PrimeTextureStorage(state.HistoryDepthTextureB);
        }

        private XRTexture CreateHistoryAoTexture(int width, int height)
        {
            if (Stereo)
            {
                var arrayTexture = XRTexture2DArray.CreateFrameBufferTexture(
                    2,
                    (uint)width,
                    (uint)height,
                    EPixelInternalFormat.R16f,
                    EPixelFormat.Red,
                    EPixelType.HalfFloat);
                arrayTexture.Resizable = false;
                arrayTexture.SizedInternalFormat = ESizedInternalFormat.R16f;
                arrayTexture.OVRMultiViewParameters = new(0, 2u);
                arrayTexture.MinFilter = ETexMinFilter.Nearest;
                arrayTexture.MagFilter = ETexMagFilter.Nearest;
                arrayTexture.UWrap = ETexWrapMode.ClampToEdge;
                arrayTexture.VWrap = ETexWrapMode.ClampToEdge;
                arrayTexture.RequiresStorageUsage = true;
                return arrayTexture;
            }

            XRTexture2D historyTexture = XRTexture2D.CreateFrameBufferTexture(
                (uint)width,
                (uint)height,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat);
            historyTexture.Resizable = false;
            historyTexture.SizedInternalFormat = ESizedInternalFormat.R16f;
            historyTexture.MinFilter = ETexMinFilter.Nearest;
            historyTexture.MagFilter = ETexMagFilter.Nearest;
            historyTexture.UWrap = ETexWrapMode.ClampToEdge;
            historyTexture.VWrap = ETexWrapMode.ClampToEdge;
            historyTexture.RequiresStorageUsage = true;
            return historyTexture;
        }

        private XRTexture CreateHistoryDepthTexture(int width, int height)
        {
            if (Stereo)
            {
                XRTexture2DArray arrayTexture = new((uint)width, (uint)height, 2, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float)
                {
                    Resizable = false,
                    SizedInternalFormat = ESizedInternalFormat.R32f,
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    RequiresStorageUsage = true,
                    OVRMultiViewParameters = new(0, 2u)
                };
                return arrayTexture;
            }

            XRTexture2D historyTexture = XRTexture2D.CreateFrameBufferTexture(
                (uint)width,
                (uint)height,
                EPixelInternalFormat.R32f,
                EPixelFormat.Red,
                EPixelType.Float);
            historyTexture.Resizable = false;
            historyTexture.SizedInternalFormat = ESizedInternalFormat.R32f;
            historyTexture.MinFilter = ETexMinFilter.Nearest;
            historyTexture.MagFilter = ETexMagFilter.Nearest;
            historyTexture.UWrap = ETexWrapMode.ClampToEdge;
            historyTexture.VWrap = ETexWrapMode.ClampToEdge;
            historyTexture.RequiresStorageUsage = true;
            return historyTexture;
        }

        private static void DestroyHistoryTextures(InstanceState state)
        {
            state.HistoryAoTextureA?.Destroy();
            state.HistoryAoTextureA = null;
            state.HistoryAoTextureB?.Destroy();
            state.HistoryAoTextureB = null;
            state.HistoryDepthTextureA?.Destroy();
            state.HistoryDepthTextureA = null;
            state.HistoryDepthTextureB?.Destroy();
            state.HistoryDepthTextureB = null;
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

        private void DispatchSpatialHashAO(
            InstanceState state,
            XRTexture normalTex,
            XRTexture depthViewTex,
            XRTexture transformIdTex,
            int width,
            int height,
            SpatialHashRuntimeSettings runtimeSettings,
            VPRC_TemporalAccumulationPass.TemporalUniformData temporalData,
            bool hasTemporalData)
        {
            if (state.AoTexture is null || state.HashBuffer is null || state.HashTimeBuffer is null || state.SpatialBuffer is null)
                return;

            XRTexture? aoTexture = state.AoTexture;
            bool layered = state.AoTexture is XRTexture2DArray;
            if (!layered && state.AoTexture is not XRTexture2D)
                return;

            var camera = ActivePipelineInstance.RenderState.SceneCamera;

            float fovY = camera?.Parameters is XRPerspectiveCameraParameters persp
                ? XRMath.DegToRad(persp.VerticalFieldOfView)
                : XRMath.DegToRad(60.0f);

            if (Stereo && layered)
            {
                DispatchStereo(state, normalTex, depthViewTex, transformIdTex, aoTexture, width, height,
                    runtimeSettings, fovY);
            }
            else
            {
                DispatchMono(state, normalTex, depthViewTex, transformIdTex, aoTexture, width, height,
                    runtimeSettings, fovY, camera, temporalData, hasTemporalData);
            }
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
                //Log($"Invalidated dependent FBO '{name}'");
            }
        }

        private void DispatchMono(
            InstanceState state,
            XRTexture normalTex, XRTexture depthViewTex, XRTexture transformIdTex, XRTexture aoTexture,
            int width, int height,
            SpatialHashRuntimeSettings runtimeSettings,
            float fovY,
            XRCamera? camera,
            VPRC_TemporalAccumulationPass.TemporalUniformData temporalData,
            bool hasTemporalData)
        {
            if (_computeProgram is null)
                return;

            XRTexture? previousHistoryAo = state.UseHistoryTextureA ? state.HistoryAoTextureB : state.HistoryAoTextureA;
            XRTexture? previousHistoryDepth = state.UseHistoryTextureA ? state.HistoryDepthTextureB : state.HistoryDepthTextureA;
            XRTexture? writeHistoryAo = state.UseHistoryTextureA ? state.HistoryAoTextureA : state.HistoryAoTextureB;
            XRTexture? writeHistoryDepth = state.UseHistoryTextureA ? state.HistoryDepthTextureA : state.HistoryDepthTextureB;
            if (writeHistoryAo is null || writeHistoryDepth is null)
                return;

            Matrix4x4 inverseView = camera?.Transform.RenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 viewMatrix = inverseView.Inverted();
            Matrix4x4 projMatrix = camera?.ProjectionMatrix ?? Matrix4x4.Identity;
            Matrix4x4 inverseProj = projMatrix.Inverted();
            Matrix4x4 currViewProjectionUnjittered = hasTemporalData
                ? temporalData.CurrViewProjectionUnjittered
                : viewMatrix * (camera?.ProjectionMatrixUnjittered ?? Matrix4x4.Identity);
            Matrix4x4 prevViewProjectionUnjittered = hasTemporalData && temporalData.HistoryReady
                ? temporalData.PrevViewProjectionUnjittered
                : currViewProjectionUnjittered;
            Vector2 currentJitterUv = hasTemporalData
                ? new Vector2(temporalData.CurrentJitter.X / Math.Max(1, width), temporalData.CurrentJitter.Y / Math.Max(1, height))
                : Vector2.Zero;
            Vector2 previousJitterUv = hasTemporalData
                ? new Vector2(temporalData.PreviousJitter.X / Math.Max(1, width), temporalData.PreviousJitter.Y / Math.Max(1, height))
                : Vector2.Zero;
            bool historyReady = runtimeSettings.TemporalReuseEnabled
                && state.HistoryReady
                && hasTemporalData
                && temporalData.HistoryReady;

            state.HashBuffer!.BindTo(_computeProgram, 1u);
            state.HashTimeBuffer!.BindTo(_computeProgram, 2u);
            state.SpatialBuffer!.BindTo(_computeProgram, 3u);

            _computeProgram.Sampler("NormalTex", normalTex, 0);
            _computeProgram.Sampler("DepthTex", depthViewTex, 1);
            _computeProgram.Sampler("HistoryAOTex", previousHistoryAo ?? writeHistoryAo, 2);
            _computeProgram.Sampler("HistoryDepthTex", previousHistoryDepth ?? writeHistoryDepth, 3);
            _computeProgram.Sampler("TransformIdTex", transformIdTex, 4);
            _computeProgram.BindImageTexture(0u, aoTexture, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R16F);
            _computeProgram.BindImageTexture(1u, writeHistoryAo, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R16F);
            _computeProgram.BindImageTexture(2u, writeHistoryDepth, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R32F);

            _computeProgram.Uniform("FrameIndex", state.FrameIndex++);
            _computeProgram.Uniform("HashMapSize", state.HashCapacity);
            _computeProgram.Uniform("CellSizeMin", runtimeSettings.CellSizeMin);
            _computeProgram.Uniform("Bias", runtimeSettings.Bias);
            _computeProgram.Uniform("Thickness", runtimeSettings.Thickness);
            _computeProgram.Uniform("DistanceFade", runtimeSettings.DistanceFade);
            _computeProgram.Uniform("Power", runtimeSettings.Power);
            _computeProgram.Uniform("SamplesPerPixel", runtimeSettings.SamplesPerPixel);
            _computeProgram.Uniform("JitterScale", runtimeSettings.JitterScale);
            _computeProgram.Uniform("MaxSamplesPerCell", runtimeSettings.MaxSamplesPerCell);
            _computeProgram.Uniform("RayStepCount", runtimeSettings.Steps);
            _computeProgram.Uniform("Radius", runtimeSettings.Radius);
            _computeProgram.Uniform("FieldOfViewY", fovY);
            _computeProgram.Uniform("InvResolution", new Vector2(1.0f / width, 1.0f / height));
            _computeProgram.Uniform(EEngineUniform.DepthMode.ToStringFast(), (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal));
            _computeProgram.Uniform("HistoryReady", historyReady);
            _computeProgram.Uniform("CurrentJitterUv", currentJitterUv);
            _computeProgram.Uniform("PreviousJitterUv", previousJitterUv);
            _computeProgram.Uniform("TemporalBlendFactor", runtimeSettings.TemporalBlendFactor);
            _computeProgram.Uniform("TemporalClamp", runtimeSettings.TemporalClamp);
            _computeProgram.Uniform("TemporalDepthRejectThreshold", runtimeSettings.TemporalDepthRejectThreshold);
            _computeProgram.Uniform("TemporalMotionRejectionScale", runtimeSettings.TemporalMotionRejectionScale);
            _computeProgram.Uniform("InverseProjMatrix", inverseProj);
            _computeProgram.Uniform("ProjMatrix", projMatrix);
            _computeProgram.Uniform("CurrViewProjectionUnjittered", currViewProjectionUnjittered);
            _computeProgram.Uniform("PrevViewProjectionUnjittered", prevViewProjectionUnjittered);
            _computeProgram.Uniform("InverseViewMatrix", inverseView);
            _computeProgram.Uniform("ViewMatrix", viewMatrix);

            Debug.RenderingEvery(
                $"AO.SpatialHash.DispatchMono.{RuntimeHelpers.GetHashCode(ActivePipelineInstance)}",
                TimeSpan.FromSeconds(1),
                "[AO][SpatialHash] DispatchMono depthMode={0} frameIndex={1} size={2}x{3} radius={4} spp={5}",
                camera?.DepthMode.ToString() ?? "null",
                state.FrameIndex,
                width,
                height,
                runtimeSettings.Radius,
                runtimeSettings.SamplesPerPixel);

            uint groupX = (uint)(width + LocalGroupSize - 1) / LocalGroupSize;
            uint groupY = (uint)(height + LocalGroupSize - 1) / LocalGroupSize;
            _computeProgram.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.TextureFetch | EMemoryBarrierMask.ShaderStorage);
            Log($"Dispatch mono: frameIndex={state.FrameIndex}, groups={groupX}x{groupY}, hashCapacity={state.HashCapacity}");
            state.HistoryReady = true;
            state.UseHistoryTextureA = !state.UseHistoryTextureA;
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
            DestroyHistoryTextures(state);
            state.HashBuffer?.Destroy();
            state.HashBuffer = null;
            state.HashTimeBuffer?.Destroy();
            state.HashTimeBuffer = null;
            state.SpatialBuffer?.Destroy();
            state.SpatialBuffer = null;
            state.LastWidth = 0;
            state.LastHeight = 0;
            state.HashCapacity = 0;
            state.FrameIndex = 0;
            state.HistoryReady = false;
            state.HasCachedViewProjection = false;
            state.LastUnjitteredViewProjection = Matrix4x4.Identity;
            state.UseHistoryTextureA = true;
        }

        private void DispatchStereo(
            InstanceState state,
            XRTexture normalTex, XRTexture depthViewTex, XRTexture transformIdTex, XRTexture aoTexture,
            int width, int height,
            SpatialHashRuntimeSettings runtimeSettings,
            float fovY)
        {
            if (_computeProgramStereo is null)
                return;

            XRTexture? previousHistoryAo = state.UseHistoryTextureA ? state.HistoryAoTextureB : state.HistoryAoTextureA;
            XRTexture? previousHistoryDepth = state.UseHistoryTextureA ? state.HistoryDepthTextureB : state.HistoryDepthTextureA;
            XRTexture? writeHistoryAo = state.UseHistoryTextureA ? state.HistoryAoTextureA : state.HistoryAoTextureB;
            XRTexture? writeHistoryDepth = state.UseHistoryTextureA ? state.HistoryDepthTextureA : state.HistoryDepthTextureB;
            if (writeHistoryAo is null || writeHistoryDepth is null)
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
            _computeProgramStereo.Sampler("HistoryAOTex", previousHistoryAo ?? writeHistoryAo, 2);
            _computeProgramStereo.Sampler("HistoryDepthTex", previousHistoryDepth ?? writeHistoryDepth, 3);
            _computeProgramStereo.Sampler("TransformIdTex", transformIdTex, 4);
            _computeProgramStereo.BindImageTexture(0u, aoTexture, 0, true, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R16F);
            _computeProgramStereo.BindImageTexture(1u, writeHistoryAo, 0, true, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R16F);
            _computeProgramStereo.BindImageTexture(2u, writeHistoryDepth, 0, true, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R32F);

            _computeProgramStereo.Uniform("FrameIndex", state.FrameIndex++);
            _computeProgramStereo.Uniform("HashMapSize", state.HashCapacity);
            _computeProgramStereo.Uniform("CellSizeMin", runtimeSettings.CellSizeMin);
            _computeProgramStereo.Uniform("Bias", runtimeSettings.Bias);
            _computeProgramStereo.Uniform("Thickness", runtimeSettings.Thickness);
            _computeProgramStereo.Uniform("DistanceFade", runtimeSettings.DistanceFade);
            _computeProgramStereo.Uniform("Power", runtimeSettings.Power);
            _computeProgramStereo.Uniform("SamplesPerPixel", runtimeSettings.SamplesPerPixel);
            _computeProgramStereo.Uniform("JitterScale", runtimeSettings.JitterScale);
            _computeProgramStereo.Uniform("MaxSamplesPerCell", runtimeSettings.MaxSamplesPerCell);
            _computeProgramStereo.Uniform("RayStepCount", runtimeSettings.Steps);
            _computeProgramStereo.Uniform("Radius", runtimeSettings.Radius);
            _computeProgramStereo.Uniform("FieldOfViewY", fovY);
            _computeProgramStereo.Uniform("InvResolution", new Vector2(1.0f / width, 1.0f / height));
            _computeProgramStereo.Uniform(EEngineUniform.DepthMode.ToStringFast(), (int)leftCamera.DepthMode);
            _computeProgramStereo.Uniform("HistoryReady", false);
            _computeProgramStereo.Uniform("CurrentJitterUv", Vector2.Zero);
            _computeProgramStereo.Uniform("PreviousJitterUv", Vector2.Zero);
            _computeProgramStereo.Uniform("TemporalBlendFactor", runtimeSettings.TemporalBlendFactor);
            _computeProgramStereo.Uniform("TemporalClamp", runtimeSettings.TemporalClamp);
            _computeProgramStereo.Uniform("TemporalDepthRejectThreshold", runtimeSettings.TemporalDepthRejectThreshold);
            _computeProgramStereo.Uniform("TemporalMotionRejectionScale", runtimeSettings.TemporalMotionRejectionScale);

            // Left eye matrices
            _computeProgramStereo.Uniform("LeftEyeInverseProjMatrix", leftInverseProj);
            _computeProgramStereo.Uniform("LeftEyeProjMatrix", leftProjMatrix);
            _computeProgramStereo.Uniform("LeftEyeInverseViewMatrix", leftInverseView);
            _computeProgramStereo.Uniform("LeftEyeViewMatrix", leftViewMatrix);
            _computeProgramStereo.Uniform("LeftEyeCurrViewProjectionUnjittered", leftViewMatrix * leftCamera.ProjectionMatrixUnjittered);
            _computeProgramStereo.Uniform("LeftEyePrevViewProjectionUnjittered", leftViewMatrix * leftCamera.ProjectionMatrixUnjittered);

            // Right eye matrices
            _computeProgramStereo.Uniform("RightEyeInverseProjMatrix", rightInverseProj);
            _computeProgramStereo.Uniform("RightEyeProjMatrix", rightProjMatrix);
            _computeProgramStereo.Uniform("RightEyeInverseViewMatrix", rightInverseView);
            _computeProgramStereo.Uniform("RightEyeViewMatrix", rightViewMatrix);
            _computeProgramStereo.Uniform("RightEyeCurrViewProjectionUnjittered", rightViewMatrix * (rightCamera?.ProjectionMatrixUnjittered ?? leftCamera.ProjectionMatrixUnjittered));
            _computeProgramStereo.Uniform("RightEyePrevViewProjectionUnjittered", rightViewMatrix * (rightCamera?.ProjectionMatrixUnjittered ?? leftCamera.ProjectionMatrixUnjittered));

            Debug.RenderingEvery(
                $"AO.SpatialHash.DispatchStereo.{RuntimeHelpers.GetHashCode(ActivePipelineInstance)}",
                TimeSpan.FromSeconds(1),
                "[AO][SpatialHash] DispatchStereo frameIndex={0} size={1}x{2} radius={3} spp={4}",
                state.FrameIndex,
                width,
                height,
                runtimeSettings.Radius,
                runtimeSettings.SamplesPerPixel);

            uint groupX = (uint)(width + LocalGroupSize - 1) / LocalGroupSize;
            uint groupY = (uint)(height + LocalGroupSize - 1) / LocalGroupSize;
            _computeProgramStereo.DispatchCompute(groupX, groupY, 1u, EMemoryBarrierMask.TextureFetch | EMemoryBarrierMask.ShaderStorage);
            Log($"Dispatch stereo: frameIndex={state.FrameIndex}, groups={groupX}x{groupY}, hashCapacity={state.HashCapacity}");
            state.HistoryReady = true;
            state.UseHistoryTextureA = !state.UseHistoryTextureA;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_SpatialHashAOPass), ERenderGraphPassStage.Compute);
            builder.SampleTexture(MakeTextureResource(NormalTextureName));
            builder.SampleTexture(MakeTextureResource(DepthViewTextureName));
            builder.SampleTexture(MakeTextureResource(TransformIdTextureName));
            builder.ReadWriteTexture(MakeTextureResource(IntensityTextureName));
            builder.ReadWriteBuffer("SpatialHashKeys");
            builder.ReadWriteBuffer("SpatialHashTime");
            builder.ReadWriteBuffer("SpatialHashData");
        }
    }
}
