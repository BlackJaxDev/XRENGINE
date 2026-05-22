using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Shadows;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        #region Forward Lighting

        private const int MaxForwardDirectionalLights = 2;
        private const uint MinForwardLocalLightBufferCapacity = 1024;
        private const int ForwardMaxCascades = 8;
        private const int ForwardDirectionalShadowRecordCount = MaxForwardDirectionalLights * ForwardMaxCascades;
        private const uint ForwardDirectionalLightsBinding = 22;
        private const uint ForwardPointLightsBinding = 23;
        private const uint ForwardSpotLightsBinding = 26;
        private const uint ForwardPointShadowMetadataBinding = 27;
        private const uint ForwardSpotShadowMetadataBinding = 28;

        private XRDataBuffer? _forwardDirectionalLightsBuffer;
        private XRDataBuffer? _forwardPointLightsBuffer;
        private XRDataBuffer? _forwardSpotLightsBuffer;
        private XRDataBuffer? _forwardPointShadowMetadataBuffer;
        private XRDataBuffer? _forwardSpotShadowMetadataBuffer;

        // Frame tokens used to gate redundant per-material-program re-uploads.
        // SetForwardLightingUniforms runs once per material program per pass, but
        // the underlying buffer contents only need to be uploaded once per frame.
        // We still execute sampler/uniform binding per program; only the host->GPU
        // Set() writes and PushSubData() calls are skipped on repeat invocations.
        private long _lightBuffersUploadedFrameTicks = long.MinValue;
        private long _shadowMetadataUploadedFrameTicks = long.MinValue;

        private static readonly IVector4[] _directionalShadowAtlasPacked0 = new IVector4[ForwardDirectionalShadowRecordCount];
        private static readonly Vector4[] _directionalShadowAtlasParams0 = new Vector4[ForwardDirectionalShadowRecordCount];
        private static readonly Vector4[] _directionalShadowAtlasParams1 = new Vector4[ForwardDirectionalShadowRecordCount];
        private static readonly Vector4[] _directionalShadowBiasProjectionParams = new Vector4[MaxForwardDirectionalLights];
        private static readonly int[] _directionalShadowMapEnabled = new int[MaxForwardDirectionalLights];
        private static readonly int[] _directionalUseCascadedShadows = new int[MaxForwardDirectionalLights];
        private static readonly int[] _directionalShadowAtlasEnabled = new int[MaxForwardDirectionalLights];
        private static readonly int[] _directionalShadowMapEncoding = new int[MaxForwardDirectionalLights];
        private static readonly Vector4[] _directionalShadowMomentParams0 = new Vector4[MaxForwardDirectionalLights];
        private static readonly Vector4[] _directionalShadowMomentFilterParams = new Vector4[MaxForwardDirectionalLights];

        // Pre-computed sampler uniform names for indexed shadow resources.
        // Avoids per-draw string interpolation allocations on the render thread.
        private static readonly string[] _directionalShadowMapNames = CreateIndexedNames("DirectionalShadowMaps", MaxForwardDirectionalLights);
        private static readonly string[] _directionalShadowMapArrayNames = CreateIndexedNames("DirectionalShadowMapArrays", MaxForwardDirectionalLights);
        private static readonly string[] _pointShadowMapNames = CreateIndexedNames("PointLightShadowMaps", 4);
        private static readonly string[] _spotShadowMapNames = CreateIndexedNames("SpotLightShadowMaps", 4);
        private const string PointShadowAtlasName = "PointLightShadowAtlas";
        private const string SpotShadowAtlasName = "SpotLightShadowAtlas";
        private const string DirectionalShadowAtlasName = "DirectionalShadowAtlas";

        private static string[] CreateIndexedNames(string prefix, int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++)
                names[i] = $"{prefix}[{i}]";
            return names;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardBaseLightGpu
        {
            public Vector4 ColorDiffuse;
            public Vector4 AmbientPadding;
            public Matrix4x4 WorldToLightSpaceProjMatrix;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct ForwardDirectionalLightGpu
        {
            public ForwardBaseLightGpu Base;
            public Vector4 DirectionPadding;
            public Matrix4x4 WorldToLightInvViewMatrix;
            public Matrix4x4 WorldToLightProjMatrix;
            public Matrix4x4 WorldToLightSpaceMatrix;
            public fixed float CascadeSplits[ForwardMaxCascades];
            public fixed float CascadeBlendWidths[ForwardMaxCascades];
            public fixed float CascadeBiasMin[ForwardMaxCascades];
            public fixed float CascadeBiasMax[ForwardMaxCascades];
            public fixed float CascadeReceiverOffsets[ForwardMaxCascades];
            public fixed float CascadeMatrices[ForwardMaxCascades * 16];
            public int CascadeCount;
            private int _padding0;
            private int _padding1;
            private int _padding2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardPointLightGpu
        {
            public ForwardBaseLightGpu Base;
            public Vector4 PositionRadius;
            public Vector4 BrightnessPadding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardSpotLightGpu
        {
            public ForwardPointLightGpu Base;
            public Vector4 DirectionInnerCutoff;
            public Vector4 OuterCutoffExponentPadding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardPointShadowGpu
        {
            public IVector4 Packed0;
            public IVector4 Packed1;
            public Vector4 Params0;
            public Vector4 Params1;
            public Vector4 Params2;
            public Vector4 Params3;
            public Vector4 Params4;
            public Vector4 Params5;
            public IVector4 Packed2;
            public Vector4 Params6;
            public IVector4 AtlasPacked0Face0;
            public IVector4 AtlasPacked0Face1;
            public IVector4 AtlasPacked0Face2;
            public IVector4 AtlasPacked0Face3;
            public IVector4 AtlasPacked0Face4;
            public IVector4 AtlasPacked0Face5;
            public Vector4 AtlasParams0Face0;
            public Vector4 AtlasParams0Face1;
            public Vector4 AtlasParams0Face2;
            public Vector4 AtlasParams0Face3;
            public Vector4 AtlasParams0Face4;
            public Vector4 AtlasParams0Face5;
            public Vector4 AtlasParams1Face0;
            public Vector4 AtlasParams1Face1;
            public Vector4 AtlasParams1Face2;
            public Vector4 AtlasParams1Face3;
            public Vector4 AtlasParams1Face4;
            public Vector4 AtlasParams1Face5;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardSpotShadowGpu
        {
            public IVector4 Packed0;
            public IVector4 Packed1;
            public Vector4 Params0;
            public Vector4 Params1;
            public Vector4 Params2;
            public Vector4 Params3;
            public IVector4 Packed2;
            public Vector4 Params4;
            public Vector4 Params5;
            public IVector4 AtlasPacked0;
            public Vector4 AtlasParams0;
            public Vector4 AtlasParams1;
            public Vector4 Params6;
        }

        private static XRDataBuffer CreateForwardLightBuffer<T>(string name, uint elementCount) where T : unmanaged
        {
            var buffer = new XRDataBuffer(
                name,
                EBufferTarget.ShaderStorageBuffer,
                elementCount,
                EComponentType.Struct,
                (uint)Unsafe.SizeOf<T>(),
                normalize: false,
                integral: false)
            {
                Usage = EBufferUsage.StreamDraw,
                PadEndingToVec4 = true,
            };
            buffer.PushData();
            return buffer;
        }

        private static XRDataBuffer EnsureForwardBuffer<T>(XRDataBuffer? buffer, string name, uint elementCount) where T : unmanaged
        {
            if (buffer is not null && buffer.ElementCount >= elementCount)
                return buffer;

            buffer?.Dispose();
            return CreateForwardLightBuffer<T>(name, elementCount);
        }

        private static uint ResolveForwardLocalBufferCapacity(int requiredCount)
        {
            if (requiredCount <= 0)
                return 1u;

            uint required = (uint)requiredCount;
            uint capacity = MinForwardLocalLightBufferCapacity;
            while (capacity < required && capacity <= uint.MaxValue / 2u)
                capacity <<= 1;

            return Math.Max(capacity, required);
        }

        private static void PushForwardBufferSubData<T>(XRDataBuffer buffer, int writtenElementCount) where T : unmanaged
        {
            uint elementCount = (uint)Math.Max(writtenElementCount, 1);
            buffer.PushSubData(0, elementCount * (uint)Unsafe.SizeOf<T>());
        }

        private void EnsureForwardLightBuffers(int pointLightCount, int spotLightCount)
        {
            uint pointCapacity = ResolveForwardLocalBufferCapacity(pointLightCount);
            uint spotCapacity = ResolveForwardLocalBufferCapacity(spotLightCount);

            _forwardDirectionalLightsBuffer = EnsureForwardBuffer<ForwardDirectionalLightGpu>(
                _forwardDirectionalLightsBuffer,
                "ForwardDirectionalLights",
                MaxForwardDirectionalLights);
            _forwardPointLightsBuffer = EnsureForwardBuffer<ForwardPointLightGpu>(
                _forwardPointLightsBuffer,
                "ForwardPointLights",
                pointCapacity);
            _forwardSpotLightsBuffer = EnsureForwardBuffer<ForwardSpotLightGpu>(
                _forwardSpotLightsBuffer,
                "ForwardSpotLights",
                spotCapacity);
            _forwardPointShadowMetadataBuffer = EnsureForwardBuffer<ForwardPointShadowGpu>(
                _forwardPointShadowMetadataBuffer,
                "ForwardPointShadowMetadata",
                pointCapacity);
            _forwardSpotShadowMetadataBuffer = EnsureForwardBuffer<ForwardSpotShadowGpu>(
                _forwardSpotShadowMetadataBuffer,
                "ForwardSpotShadowMetadata",
                spotCapacity);
        }

        private void BindForwardLightBuffers(XRRenderProgram program)
        {
            program.BindBuffer(_forwardDirectionalLightsBuffer!, ForwardDirectionalLightsBinding);
            program.BindBuffer(_forwardPointLightsBuffer!, ForwardPointLightsBinding);
            program.BindBuffer(_forwardSpotLightsBuffer!, ForwardSpotLightsBinding);
            program.BindBuffer(_forwardPointShadowMetadataBuffer!, ForwardPointShadowMetadataBinding);
            program.BindBuffer(_forwardSpotShadowMetadataBuffer!, ForwardSpotShadowMetadataBinding);
        }

        private void UploadForwardLightBuffers(int directionalLightCount, int pointLightCount, int spotLightCount)
        {
            EnsureForwardLightBuffers(pointLightCount, spotLightCount);

            // Skip the per-program data refresh + GPU upload when this frame already populated the buffers.
            // Bind/sampler/uniform calls are still executed by the caller for every program.
            long frameTicks = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
            if (frameTicks == _lightBuffersUploadedFrameTicks)
                return;
            _lightBuffersUploadedFrameTicks = frameTicks;

            for (int i = 0; i < MaxForwardDirectionalLights; i++)
            {
                ForwardDirectionalLightGpu light = i < directionalLightCount
                    ? CreateForwardDirectionalLightGpu(DynamicDirectionalLights[i])
                    : default;
                _forwardDirectionalLightsBuffer!.Set((uint)i, light);
            }

            if (pointLightCount == 0)
                _forwardPointLightsBuffer!.Set(0, default(ForwardPointLightGpu));
            for (int i = 0; i < pointLightCount; i++)
            {
                _forwardPointLightsBuffer!.Set((uint)i, CreateForwardPointLightGpu(DynamicPointLights[i]));
            }

            if (spotLightCount == 0)
                _forwardSpotLightsBuffer!.Set(0, default(ForwardSpotLightGpu));
            for (int i = 0; i < spotLightCount; i++)
            {
                _forwardSpotLightsBuffer!.Set((uint)i, CreateForwardSpotLightGpu(DynamicSpotLights[i]));
            }

            _forwardDirectionalLightsBuffer!.PushSubData();
            PushForwardBufferSubData<ForwardPointLightGpu>(_forwardPointLightsBuffer!, pointLightCount);
            PushForwardBufferSubData<ForwardSpotLightGpu>(_forwardSpotLightsBuffer!, spotLightCount);
        }

        private static ForwardBaseLightGpu CreateForwardBaseLightGpu(LightComponent light, Matrix4x4 worldToLightSpaceProjMatrix, float ambientIntensity)
            => new()
            {
                ColorDiffuse = new Vector4(light.Color, light.DiffuseIntensity),
                AmbientPadding = new Vector4(ambientIntensity, 0.0f, 0.0f, 0.0f),
                WorldToLightSpaceProjMatrix = worldToLightSpaceProjMatrix,
            };

        private static unsafe ForwardDirectionalLightGpu CreateForwardDirectionalLightGpu(DirectionalLightComponent light)
        {
            Matrix4x4 lightView = light.ShadowCamera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 lightProj = light.ShadowCamera?.ProjectionMatrix ?? Matrix4x4.Identity;
            Matrix4x4 lightViewProj = lightView * lightProj;

            ForwardDirectionalLightGpu data = new()
            {
                Base = CreateForwardBaseLightGpu(light, lightViewProj, 0.05f),
                DirectionPadding = new Vector4(light.Transform.WorldForward, 0.0f),
                WorldToLightInvViewMatrix = light.ShadowCamera?.Transform.RenderMatrix ?? Matrix4x4.Identity,
                WorldToLightProjMatrix = lightProj,
                WorldToLightSpaceMatrix = lightViewProj,
            };

            Span<float> cascadeSplits = stackalloc float[ForwardMaxCascades];
            Span<float> cascadeBlendWidths = stackalloc float[ForwardMaxCascades];
            Span<float> cascadeBiasMins = stackalloc float[ForwardMaxCascades];
            Span<float> cascadeBiasMaxes = stackalloc float[ForwardMaxCascades];
            Span<float> cascadeReceiverOffsets = stackalloc float[ForwardMaxCascades];
            Span<Matrix4x4> cascadeMatrices = stackalloc Matrix4x4[ForwardMaxCascades];
            light.CopyPublishedCascadeUniformData(
                cascadeSplits,
                cascadeBlendWidths,
                cascadeBiasMins,
                cascadeBiasMaxes,
                cascadeReceiverOffsets,
                cascadeMatrices,
                out int cascadeCount);

            data.CascadeCount = Math.Clamp(cascadeCount, 0, ForwardMaxCascades);
            CopyFloatSpan(data.CascadeSplits, cascadeSplits);
            CopyFloatSpan(data.CascadeBlendWidths, cascadeBlendWidths);
            CopyFloatSpan(data.CascadeBiasMin, cascadeBiasMins);
            CopyFloatSpan(data.CascadeBiasMax, cascadeBiasMaxes);
            CopyFloatSpan(data.CascadeReceiverOffsets, cascadeReceiverOffsets);
            CopyMatrixSpan(data.CascadeMatrices, cascadeMatrices);

            return data;
        }

        private static ForwardPointLightGpu CreateForwardPointLightGpu(PointLightComponent light)
            => new()
            {
                Base = CreateForwardBaseLightGpu(light, Matrix4x4.Identity, 0.0f),
                PositionRadius = new Vector4(light.Transform.RenderTranslation, light.Radius),
                BrightnessPadding = new Vector4(light.Brightness, 0.0f, 0.0f, 0.0f),
            };

        private static void SetPointAtlasFaceMetadata(
            ref ForwardPointShadowGpu data,
            int faceIndex,
            IVector4 packed0,
            Vector4 params0,
            Vector4 params1)
        {
            switch (faceIndex)
            {
                case 0:
                    data.AtlasPacked0Face0 = packed0;
                    data.AtlasParams0Face0 = params0;
                    data.AtlasParams1Face0 = params1;
                    break;
                case 1:
                    data.AtlasPacked0Face1 = packed0;
                    data.AtlasParams0Face1 = params0;
                    data.AtlasParams1Face1 = params1;
                    break;
                case 2:
                    data.AtlasPacked0Face2 = packed0;
                    data.AtlasParams0Face2 = params0;
                    data.AtlasParams1Face2 = params1;
                    break;
                case 3:
                    data.AtlasPacked0Face3 = packed0;
                    data.AtlasParams0Face3 = params0;
                    data.AtlasParams1Face3 = params1;
                    break;
                case 4:
                    data.AtlasPacked0Face4 = packed0;
                    data.AtlasParams0Face4 = params0;
                    data.AtlasParams1Face4 = params1;
                    break;
                case 5:
                    data.AtlasPacked0Face5 = packed0;
                    data.AtlasParams0Face5 = params0;
                    data.AtlasParams1Face5 = params1;
                    break;
            }
        }

        private static bool IsShadowAtlasAllocationSampleable(in ShadowAtlasAllocation allocation, int layerCount)
            => allocation.IsResident &&
               allocation.LastRenderedFrame != 0u &&
               allocation.ActiveFallback is ShadowFallbackMode.None or ShadowFallbackMode.StaleTile &&
               allocation.PageIndex >= 0 &&
               allocation.PageIndex < layerCount;

        private static ForwardSpotLightGpu CreateForwardSpotLightGpu(SpotLightComponent light)
        {
            Matrix4x4 lightView = light.ShadowCamera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 lightProj = light.ShadowCamera?.ProjectionMatrix ?? Matrix4x4.Identity;
            Matrix4x4 lightViewProj = lightView * lightProj;

            return new ForwardSpotLightGpu
            {
                Base = new ForwardPointLightGpu
                {
                    Base = CreateForwardBaseLightGpu(light, lightViewProj, 0.05f),
                    PositionRadius = new Vector4(light.Transform.RenderTranslation, light.Distance),
                    BrightnessPadding = new Vector4(light.Brightness, 0.0f, 0.0f, 0.0f),
                },
                DirectionInnerCutoff = new Vector4(light.Transform.RenderForward, light.InnerCutoff),
                OuterCutoffExponentPadding = new Vector4(light.OuterCutoff, light.Exponent, 0.0f, 0.0f),
            };
        }

        private static unsafe void CopyFloatSpan(float* destination, ReadOnlySpan<float> source)
        {
            for (int i = 0; i < source.Length; i++)
                destination[i] = source[i];
        }

        private static unsafe void CopyMatrixSpan(float* destination, ReadOnlySpan<Matrix4x4> source)
        {
            for (int i = 0; i < source.Length; i++)
                Unsafe.WriteUnaligned(destination + (i * 16), source[i]);
        }

        private static bool IsTexture2D(XRTexture texture)
            => texture switch
            {
                XRTexture2D tex2D => !tex2D.MultiSample && !tex2D.Rectangle,
                XRTexture2DView { Array: false, Multisample: false } => true,
                XRTexture2DArrayView { NumLayers: 1, Multisample: false } => true,
                _ => false,
            };

        private static bool IsTexture2DArray(XRTexture texture)
            => texture switch
            {
                XRTexture2DArray tex2DArray => !tex2DArray.MultiSample,
                XRTexture2DView { Array: true, Multisample: false } => true,
                XRTexture2DArrayView { NumLayers: > 1, Multisample: false } => true,
                _ => false,
            };

        private static void SetForwardLightingCameraUniforms(XRRenderProgram program)
        {
            bool stereoPass = RuntimeEngine.Rendering.State.IsStereoPass;
            bool useUnjittered = RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
            XRCamera? leftCamera = RuntimeEngine.Rendering.State.RenderingCamera;
            program.Uniform(EEngineUniform.DepthMode.ToStringFast(), (int)(leftCamera?.DepthMode ?? XRCamera.EDepthMode.Normal));

            SetForwardLightingCameraUniforms(
                program,
                leftCamera,
                EEngineUniform.ViewMatrix,
                EEngineUniform.InverseViewMatrix,
                EEngineUniform.InverseProjMatrix,
                EEngineUniform.ProjMatrix,
                EEngineUniform.ViewProjectionMatrix,
                useUnjittered);

            if (!stereoPass)
                return;

            SetForwardLightingCameraUniforms(
                program,
                leftCamera,
                EEngineUniform.LeftEyeViewMatrix,
                EEngineUniform.LeftEyeInverseViewMatrix,
                EEngineUniform.LeftEyeInverseProjMatrix,
                EEngineUniform.LeftEyeProjMatrix,
                EEngineUniform.LeftEyeViewProjectionMatrix,
                useUnjittered);

            SetForwardLightingCameraUniforms(
                program,
                RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera,
                EEngineUniform.RightEyeViewMatrix,
                EEngineUniform.RightEyeInverseViewMatrix,
                EEngineUniform.RightEyeInverseProjMatrix,
                EEngineUniform.RightEyeProjMatrix,
                EEngineUniform.RightEyeViewProjectionMatrix,
                useUnjittered);
        }

        private static void SetForwardLightingCameraUniforms(
            XRRenderProgram program,
            XRCamera? camera,
            EEngineUniform view,
            EEngineUniform inverseView,
            EEngineUniform inverseProj,
            EEngineUniform proj,
            EEngineUniform viewProj,
            bool useUnjittered)
        {
            Matrix4x4 viewMatrix = Matrix4x4.Identity;
            Matrix4x4 inverseViewMatrix = Matrix4x4.Identity;
            Matrix4x4 inverseProjMatrix = Matrix4x4.Identity;
            Matrix4x4 projMatrix = Matrix4x4.Identity;
            Matrix4x4 viewProjectionMatrix = Matrix4x4.Identity;

            if (camera is not null)
            {
                viewMatrix = camera.Transform.InverseRenderMatrix;
                inverseViewMatrix = camera.Transform.RenderMatrix;
                inverseProjMatrix = useUnjittered ? camera.InverseProjectionMatrixUnjittered : camera.InverseProjectionMatrix;
                projMatrix = useUnjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
                viewProjectionMatrix = useUnjittered ? camera.ViewProjectionMatrixUnjittered : camera.ViewProjectionMatrix;
            }

            program.Uniform(view.ToStringFast(), viewMatrix);
            program.Uniform(inverseView.ToStringFast(), inverseViewMatrix);
            program.Uniform(inverseProj.ToStringFast(), inverseProjMatrix);
            program.Uniform(proj.ToStringFast(), projMatrix);
            program.Uniform(viewProj.ToStringFast(), viewProjectionMatrix);
        }

        internal void SetForwardLightingUniforms(XRRenderProgram program)
        {
            static void DisablePbrResources(XRRenderProgram program)
            {
                program.SuppressFallbackSamplerWarning("IrradianceArray");
                program.SuppressFallbackSamplerWarning("PrefilterArray");
                program.Uniform("ForwardPbrResourcesEnabled", false);
                program.Uniform("ProbeCount", 0);
                program.Uniform("TetraCount", 0);
                program.Uniform("UseProbeGrid", false);
            }

            const int maxForwardShadowedPointLights = 4;
            const int maxForwardShadowedSpotLights = 4;
            const int directionalShadowMapStartUnit = 15;
            const int directionalShadowMapArrayStartUnit = 17;
            const int pointShadowStartUnit = 19;
            const int spotShadowStartUnit = 23;
            const int forwardAmbientOcclusionArrayUnit = 27;
            const int forwardContactDepthUnit = 28;
            const int forwardContactNormalUnit = 29;
            const int forwardContactDepthArrayUnit = 30;
            const int forwardContactNormalArrayUnit = 31;
            const int directionalShadowAtlasStartUnit = 9;
            const int spotShadowAtlasStartUnit = 32;
            const int pointShadowAtlasStartUnit = 34;

            // Debug: log that we're being called
            if (!_loggedForwardLightingOnce)
            {
                _loggedForwardLightingOnce = true;
                Debug.Lighting($"[ForwardLighting] SetForwardLightingUniforms called. DirLights={DynamicDirectionalLights.Count}, PointLights={DynamicPointLights.Count}, SpotLights={DynamicSpotLights.Count}");
            }

            // Global ambient light - required by ForwardLighting snippet
            program.Uniform("GlobalAmbient", (Vector3)World.GetEffectiveAmbientColor());

            // Camera position for specular calculations
            program.Uniform("CameraPosition", RuntimeEngine.Rendering.State.RenderingCamera?.Transform.RenderTranslation ?? Vector3.Zero);
            // Forward contact shadows project world-space ray samples into the
            // prepass depth texture, so the lighting upload owns these matrices
            // instead of relying on every material to request Camera uniforms.
            SetForwardLightingCameraUniforms(program);

            var area = RuntimeEngine.Rendering.State.RenderArea;
            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)area.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)area.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), new Vector2(area.X, area.Y));

            int directionalLightCount = Math.Min(DynamicDirectionalLights.Count, MaxForwardDirectionalLights);
            int pointLightCount = DynamicPointLights.Count;
            int spotLightCount = DynamicSpotLights.Count;

            program.Uniform("DirLightCount", directionalLightCount);
            program.Uniform("PointLightCount", pointLightCount);
            program.Uniform("SpotLightCount", spotLightCount);
            UploadForwardLightBuffers(directionalLightCount, pointLightCount, spotLightCount);
            BindForwardLightBuffers(program);

            // Forward+ bindings (optional). Shaders may ignore these if they don't declare Forward+ support.
            program.Uniform("ForwardPlusEnabled", RuntimeEngine.Rendering.State.ForwardPlusEnabled);
            if (RuntimeEngine.Rendering.State.ForwardPlusEnabled)
            {
                program.Uniform("ForwardPlusScreenSize", RuntimeEngine.Rendering.State.ForwardPlusScreenSize);
                program.Uniform("ForwardPlusTileSize", RuntimeEngine.Rendering.State.ForwardPlusTileSize);
                program.Uniform("ForwardPlusTileCountX", RuntimeEngine.Rendering.State.ForwardPlusTileCountX);
                program.Uniform("ForwardPlusTileCountY", RuntimeEngine.Rendering.State.ForwardPlusTileCountY);
                program.Uniform("ForwardPlusMaxLightsPerTile", RuntimeEngine.Rendering.State.ForwardPlusMaxLightsPerTile);

                // Keep bindings in sync with the compute shader: 20 (local lights), 21 (visible indices).
                program.BindBuffer(RuntimeEngine.Rendering.State.ForwardPlusLocalLightsBuffer!, 20u);
                program.BindBuffer(RuntimeEngine.Rendering.State.ForwardPlusVisibleIndicesBuffer!, 21u);
            }
            program.Uniform("ForwardPlusEyeCount", RuntimeEngine.Rendering.State.IsStereoPass ? 2 : 1);

            const int forwardAmbientOcclusionUnit = 14;
            XRTexture? ambientOcclusionTexture = null;
            var currentPipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            var ambientOcclusionCamera = RuntimeEngine.Rendering.State.RenderingCamera
                ?? currentPipeline?.RenderState.SceneCamera
                ?? currentPipeline?.LastSceneCamera
                ?? currentPipeline?.LastRenderingCamera;
            var aoStage = ambientOcclusionCamera?.GetPostProcessStageState<AmbientOcclusionSettings>();
            AmbientOcclusionSettings? aoSettings = aoStage?.TryGetBacking(out AmbientOcclusionSettings? backing) == true
                ? backing
                : null;
            bool ambientOcclusionEnabled = currentPipeline is not null &&
                (aoSettings?.Enabled ?? true) &&
                currentPipeline.TryGetTexture(
                    DefaultRenderPipeline.AmbientOcclusionIntensityTextureName,
                    out ambientOcclusionTexture) && ambientOcclusionTexture is not null;
            bool ambientOcclusionArrayEnabled = ambientOcclusionTexture is XRTexture2DArray;
            program.Uniform("AmbientOcclusionEnabled", ambientOcclusionEnabled);
            program.Uniform("AmbientOcclusionArrayEnabled", ambientOcclusionArrayEnabled);
            program.Uniform("AmbientOcclusionPower", aoSettings?.Power ?? 1.0f);
            program.Uniform(
                "AmbientOcclusionMultiBounce",
                AmbientOcclusionSettings.NormalizeType(aoSettings?.Type ?? AmbientOcclusionSettings.EType.ScreenSpace) == AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion
                && aoSettings?.GroundTruth.MultiBounceEnabled == true);
            program.Uniform(
                "SpecularOcclusionEnabled",
                AmbientOcclusionSettings.NormalizeType(aoSettings?.Type ?? AmbientOcclusionSettings.EType.ScreenSpace) == AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion
                && aoSettings?.GroundTruth.SpecularOcclusionEnabled == true);
            // Debug power exponent: set > 0 (e.g. 8) to dramatically exaggerate AO on forward objects.
            // This lets us visually confirm the shader is sampling and applying the AO texture.
            // 0 = normal behaviour.  Try 8.0 to make subtle AO extremely visible.
            program.Uniform("DebugForwardAOPower", 0.0f);
            program.Uniform(DefaultRenderPipeline.AmbientOcclusionIntensityTextureName, forwardAmbientOcclusionUnit);
            program.Uniform("AmbientOcclusionTextureArray", forwardAmbientOcclusionArrayUnit);
            if (ambientOcclusionEnabled && ambientOcclusionTexture is not XRTexture2DArray)
                program.Sampler(DefaultRenderPipeline.AmbientOcclusionIntensityTextureName, ambientOcclusionTexture!, forwardAmbientOcclusionUnit);
            else
                program.Sampler(DefaultRenderPipeline.AmbientOcclusionIntensityTextureName, DummyShadowMap, forwardAmbientOcclusionUnit);
            program.Sampler("AmbientOcclusionTextureArray", ambientOcclusionTexture as XRTexture2DArray ?? DummyAmbientOcclusionArray, forwardAmbientOcclusionArrayUnit);

            XRTexture? forwardContactDepthTexture = null;
            XRTexture? forwardContactNormalTexture = null;
            bool forwardContactPrePass2DAvailable = false;
            bool forwardContactPrePassArrayAvailable = false;
            bool forwardContactPrePassAvailable = false;
            if (RuntimeEngine.EditorPreferences.Debug.ForwardDepthPrePassEnabled && currentPipeline is not null)
            {
                currentPipeline.TryGetTexture(DefaultRenderPipeline.ForwardContactDepthViewTextureName, out forwardContactDepthTexture);
                currentPipeline.TryGetTexture(DefaultRenderPipeline.ForwardContactNormalTextureName, out forwardContactNormalTexture);
                if (forwardContactDepthTexture is not null && forwardContactNormalTexture is not null)
                {
                    forwardContactPrePass2DAvailable =
                        IsTexture2D(forwardContactDepthTexture) &&
                        IsTexture2D(forwardContactNormalTexture);
                    forwardContactPrePassArrayAvailable =
                        IsTexture2DArray(forwardContactDepthTexture) &&
                        IsTexture2DArray(forwardContactNormalTexture);
                    forwardContactPrePassAvailable = forwardContactPrePass2DAvailable || forwardContactPrePassArrayAvailable;
                }
            }

            program.Uniform("ForwardContactShadowsEnabled", forwardContactPrePassAvailable);
            program.Uniform("ForwardContactShadowsArrayEnabled", forwardContactPrePassArrayAvailable);
            program.Sampler("ForwardContactDepthView", forwardContactPrePass2DAvailable ? forwardContactDepthTexture! : DummyShadowMap, forwardContactDepthUnit);
            program.Sampler("ForwardContactNormalView", forwardContactPrePass2DAvailable ? forwardContactNormalTexture! : DummyShadowMap, forwardContactNormalUnit);
            program.Sampler("ForwardContactDepthViewArray", forwardContactPrePassArrayAvailable ? forwardContactDepthTexture! : DummyShadowMapArray, forwardContactDepthArrayUnit);
            program.Sampler("ForwardContactNormalViewArray", forwardContactPrePassArrayAvailable ? forwardContactNormalTexture! : DummyShadowMapArray, forwardContactNormalArrayUnit);

            switch (currentPipeline?.Pipeline)
            {
                case DefaultRenderPipeline defaultPipeline:
                    defaultPipeline.BindPbrLightingResources(program);
                    break;
                case DefaultRenderPipeline2 defaultPipeline2:
                    defaultPipeline2.BindPbrLightingResources(program);
                    break;
                default:
                    DisablePbrResources(program);
                    break;
            }

            /*
            Debug.LightingEvery(
                "ForwardAO.Binding",
                TimeSpan.FromSeconds(1),
                "[ForwardAO] enabled={0} pipeline={1} texture={2} textureType={3} unit={4} screen={5}x{6} origin={7}",
                ambientOcclusionEnabled,
                currentPipeline?.GetType().Name ?? "null",
                ambientOcclusionTexture?.Name ?? "null",
                ambientOcclusionTexture?.GetType().Name ?? "null",
                forwardAmbientOcclusionUnit,
                area.Width,
                area.Height,
                new Vector2(area.X, area.Y));
            
            if (ambientOcclusionEnabled)
            {
                var renderer = AbstractRenderer.Current;
                if (renderer is not null && ambientOcclusionTexture is XRTexture2D aoTexture2D)
                {
                    // Read center pixel at mip 0 directly instead of CalcDotLuminance.
                    // CalcDotLuminance uses glGenerateMipmap + reads the smallest mip level,
                    // but framebuffer textures have maxLevel=0 (no mip chain), so the mip
                    // readback always returns 0.
                    float centerAo = renderer.ReadTextureCenterRedMip0(aoTexture2D);
                    Debug.LightingEvery(
                        "ForwardAO.Content.2D",
                        TimeSpan.FromSeconds(1),
                        "[ForwardAO] centerAo={0:F4} size={1}x{2}",
                        centerAo,
                        aoTexture2D.Width,
                        aoTexture2D.Height);
                }
                else if (ambientOcclusionTexture is XRTexture2DArray aoTexture2DArray)
                {
                    Debug.LightingEvery(
                        "ForwardAO.Content.2DArray",
                        TimeSpan.FromSeconds(1),
                        "[ForwardAO] texture2DArray size={0}x{1} layers={2}",
                        aoTexture2DArray.Width,
                        aoTexture2DArray.Height,
                        aoTexture2DArray.Depth);
                }
            }
            if (!_loggedForwardAoBindingOnce)
            {
                _loggedForwardAoBindingOnce = true;
                Debug.Lighting($"[ForwardAO] Initial binding enabled={ambientOcclusionEnabled}, texture={ambientOcclusionTexture?.Name ?? "null"}, textureType={ambientOcclusionTexture?.GetType().Name ?? "null"}, screen={area.Width}x{area.Height}, origin=<{area.X}, {area.Y}>");
            }
            */

            // Forward materials bind their own textures at units [0..N) where N is the texture index.
            // Using a low fixed unit (like 4) for the shadow map collides with multi-texture materials
            // (e.g., Sponza) and manifests as "shadow" sampling a regular color texture.
            // Pick a dedicated high unit for forward shadow sampling.
            const int forwardShadowMapUnit = directionalShadowMapStartUnit;
            const int forwardShadowMapArrayUnit = directionalShadowMapArrayStartUnit;
            XRTexture? forwardShadowTex = null;
            XRTexture2DArray? forwardCascadeShadowTex = null;
            bool useCascadedDirectionalShadows = false;
            bool useDirectionalShadowAtlas = false;
            bool directionalAtlasSampleable = false;
            bool anyDirectionalShadowEnabled = false;
            var directionalShadowCameraComponent = currentPipeline?.RenderState.WindowViewport?.CameraComponent;

            Array.Clear(_directionalShadowMapEnabled);
            Array.Clear(_directionalUseCascadedShadows);
            Array.Clear(_directionalShadowAtlasEnabled);
            Array.Clear(_directionalShadowBiasProjectionParams);
            Array.Clear(_directionalShadowMapEncoding);
            Array.Clear(_directionalShadowMomentParams0);
            Array.Clear(_directionalShadowMomentFilterParams);
            if (directionalLightCount > 0)
            {
                var firstDirLight = DynamicDirectionalLights[0];
                ShadowMapFormatSelection firstShadowFormat = firstDirLight.ResolveShadowMapFormat(preferredStorageFormat: null);
                program.Uniform("ShadowPackedI0", new IVector4(
                    firstDirLight.BlockerSamples,
                    firstDirLight.FilterSamples,
                    firstDirLight.VogelTapCount,
                    (int)firstDirLight.SoftShadowMode));
                program.Uniform("ShadowPackedI1", new IVector4(
                    firstDirLight.EnableCascadedShadows ? 1 : 0,
                    firstDirLight.EnableContactShadows ? 1 : 0,
                    firstDirLight.ContactShadowSamples,
                    0));
                program.Uniform("ShadowParams0", new Vector4(
                    firstDirLight.ShadowExponentBase,
                    firstDirLight.ShadowExponent,
                    firstDirLight.ShadowMinBias,
                    firstDirLight.ShadowMaxBias));
                program.Uniform("ShadowParams1", new Vector4(
                    firstDirLight.FilterRadius,
                    firstDirLight.BlockerSearchRadius,
                    firstDirLight.MinPenumbra,
                    firstDirLight.MaxPenumbra));
                program.Uniform("ShadowParams2", new Vector4(
                    firstDirLight.LightSourceRadius,
                    firstDirLight.ContactShadowDistance,
                    firstDirLight.ContactShadowThickness,
                    firstDirLight.ContactShadowFadeStart));
                program.Uniform("ShadowParams3", new Vector4(
                    firstDirLight.ContactShadowFadeEnd,
                    firstDirLight.ContactShadowNormalOffset,
                    firstDirLight.ContactShadowJitterStrength,
                    0.0f));
                program.Uniform("ShadowBiasParams", firstDirLight.ShadowBiasParameters);
                program.Uniform("ShadowBiasProjectionParams", firstDirLight.GetPrimaryShadowBiasProjectionParameters());
                program.Uniform("ShadowMapEncoding", (int)firstShadowFormat.Encoding);
                program.Uniform("ShadowMomentParams0", new Vector4(
                    firstDirLight.ShadowMomentMinVariance,
                    firstDirLight.ShadowMomentLightBleedReduction,
                    firstShadowFormat.PositiveExponent,
                    firstShadowFormat.NegativeExponent));
                program.Uniform("ShadowMomentFilterParams", new Vector4(
                    firstDirLight.ShadowMomentBlurRadiusTexels,
                    firstDirLight.ShadowMomentBlurPasses,
                    firstDirLight.ShadowMomentUseMipmaps ? 1.0f : 0.0f,
                    firstDirLight.ShadowMomentMipBias));

                if (firstDirLight.CastsShadows)
                {
                    // 2D shadow map (non-cascaded / fallback path).
                    forwardShadowTex = FindShadowMapTexture(firstDirLight);

                    // Cascaded shadow array — evaluated independently of the 2D map because
                    // in cascaded mode the 2D ShadowMap may never be populated but the
                    // cascade array IS available. Previously this branch was nested inside
                    // the 2D-map-populated check, so cascaded-only configs silently dropped
                    // all directional shadows (including the volumetric fog scatter pass).
                    var cameraComponent = currentPipeline?.RenderState.WindowViewport?.CameraComponent;
                    useCascadedDirectionalShadows =
                        cameraComponent?.DirectionalShadowRenderingMode == EDirectionalShadowRenderingMode.Cascaded &&
                        firstDirLight.EnableCascadedShadows &&
                        (firstDirLight.UsesDirectionalShadowAtlasForCurrentEncoding || firstDirLight.CascadedShadowMapTexture is not null) &&
                        firstDirLight.ActiveCascadeCount > 0;

                    if (useCascadedDirectionalShadows)
                        forwardCascadeShadowTex = firstDirLight.CascadedShadowMapTexture;

                    useDirectionalShadowAtlas =
                        firstDirLight.UsesDirectionalShadowAtlasForCurrentEncoding &&
                        firstDirLight.CastsShadows;

                    if (useDirectionalShadowAtlas)
                    {
                        forwardShadowTex = null;
                        forwardCascadeShadowTex = null;
                    }

                    if (forwardShadowTex is null && forwardCascadeShadowTex is null)
                    {
                        // Log once per distinct reason — useful when the light casts shadows
                        // but neither representation has been populated yet.
                        string reason = firstDirLight.ShadowMap is null ? "ShadowMap=null,CascadeTex=null" :
                                        firstDirLight.ShadowMap.Material is null ? "ShadowMap.Material=null,CascadeTex=null" :
                                        $"Textures.Count={firstDirLight.ShadowMap.Material.Textures.Count},CascadeTex=null";
                        if (reason != _lastForwardShadowNoTexReason)
                        {
                            _lastForwardShadowNoTexReason = reason;
                            Debug.Lighting($"[ForwardShadow] No shadow tex: {reason}");
                        }
                    }
                }
                else
                {
                    string reason = "CastsShadows=false";
                    if (reason != _lastForwardShadowNoTexReason)
                    {
                        _lastForwardShadowNoTexReason = reason;
                        Debug.Lighting($"[ForwardShadow] No shadow tex: {reason}");
                    }
                }
            }
            // ShadowMapEnabled is a "shader may sample a directional shadow" flag.
            // Before: it was forwardShadowTex != null, which only considered the 2D
            // ShadowMap. When the pipeline runs in cascaded mode the 2D map is often
            // unpopulated and only forwardCascadeShadowTex is available, so this
            // flag flipped to false and every consumer (uber shader, volumetric fog
            // scatter, etc.) short-circuited to "fully lit" — no shafts, no shadows.
            // Treat real legacy/cascade textures as available immediately. Atlas shadows
            // only count once a published slot is resident; otherwise the shader should
            // use the legacy fallback or stay lit instead of sampling dummy atlas pages.
            program.Uniform("UseCascadedDirectionalShadows", useCascadedDirectionalShadows);

            // Diagnostic: log every transition of shadowEnabled / primary-dir-light state so we can
            // diff "CastsShadows=true" vs "CastsShadows=false" uniforms upstream of the uber shader.
            // Covers the reported bump-mapped-goes-fully-bright symptom when shadows are toggled off.
            /*
            if (DynamicDirectionalLights.Count > 0)
            {
                var diagLight = DynamicDirectionalLights[0];
                bool diagCasts = diagLight.CastsShadows;
                var diagDir = diagLight.Transform.WorldForward;
                var diagColor = diagLight.Color;
                float diagIntensity = diagLight.DiffuseIntensity;
                ulong diagKey = (ulong)((shadowEnabled ? 1 : 0) | (diagCasts ? 2 : 0) | (useCascadedDirectionalShadows ? 4 : 0));
                diagKey = (diagKey << 32) | (uint)DynamicDirectionalLights.Count;
                if (diagKey != _lastForwardShadowDiagKey)
                {
                    _lastForwardShadowDiagKey = diagKey;
                    Debug.Lighting(
                        $"[ForwardShadowDiag] transition: shadowEnabled={shadowEnabled} " +
                        $"CastsShadows={diagCasts} useCascadedDirShadows={useCascadedDirectionalShadows} " +
                        $"DirLightCount={DynamicDirectionalLights.Count} " +
                        $"forwardShadowTex={forwardShadowTex?.GetType().Name ?? "null"} " +
                        $"cascadeTex={forwardCascadeShadowTex?.GetType().Name ?? "null"} " +
                        $"dir=({diagDir.X:F3},{diagDir.Y:F3},{diagDir.Z:F3}) " +
                        $"color=({diagColor.R:F3},{diagColor.G:F3},{diagColor.B:F3}) " +
                        $"diffuseIntensity={diagIntensity:F3} " +
                        $"shadowMap={(diagLight.ShadowMap?.Material?.Textures.Count ?? -1)} textures");
                }
            }
            */

            // ALWAYS set the ShadowMap sampler to point to unit 15, even when shadows are disabled.
            // This prevents stale state from deferred passes (which use unit 4) from leaking through.
            // The shader's layout(binding=15) should handle this, but we force it to be safe against
            // cached shader binaries that might not have the layout qualifier.
            program.Uniform("ShadowMap", forwardShadowMapUnit);
            program.Uniform("ShadowMapArray", forwardShadowMapArrayUnit);

            for (int i = 0; i < MaxForwardDirectionalLights; i++)
            {
                XRTexture? perLightShadowTex = null;
                XRTexture2DArray? perLightCascadeTex = null;
                bool perLightUseCascades = false;

                if (i < directionalLightCount)
                {
                    DirectionalLightComponent dirLight = DynamicDirectionalLights[i];
                    ShadowMapFormatSelection dirShadowFormat = dirLight.ResolveShadowMapFormat(preferredStorageFormat: null);
                    bool perLightUseAtlas = dirLight.UsesDirectionalShadowAtlasForCurrentEncoding;
                    useDirectionalShadowAtlas |= perLightUseAtlas;
                    _directionalShadowBiasProjectionParams[i] = dirLight.GetPrimaryShadowBiasProjectionParameters();
                    _directionalShadowMapEncoding[i] = (int)dirShadowFormat.Encoding;
                    _directionalShadowMomentParams0[i] = new Vector4(
                        dirLight.ShadowMomentMinVariance,
                        dirLight.ShadowMomentLightBleedReduction,
                        dirShadowFormat.PositiveExponent,
                        dirShadowFormat.NegativeExponent);
                    _directionalShadowMomentFilterParams[i] = new Vector4(
                        dirLight.ShadowMomentBlurRadiusTexels,
                        dirLight.ShadowMomentBlurPasses,
                        dirLight.ShadowMomentUseMipmaps ? 1.0f : 0.0f,
                        dirLight.ShadowMomentMipBias);

                    if (dirLight.CastsShadows)
                    {
                        if (!perLightUseAtlas)
                            perLightShadowTex = FindShadowMapTexture(dirLight);
                        perLightUseCascades =
                            directionalShadowCameraComponent?.DirectionalShadowRenderingMode == EDirectionalShadowRenderingMode.Cascaded &&
                            dirLight.EnableCascadedShadows &&
                            (perLightUseAtlas || dirLight.CascadedShadowMapTexture is not null) &&
                            dirLight.ActiveCascadeCount > 0;

                        if (perLightUseCascades && !perLightUseAtlas)
                            perLightCascadeTex = dirLight.CascadedShadowMapTexture;
                    }

                    bool perLightShadowEnabled = perLightUseAtlas || perLightShadowTex is not null || perLightCascadeTex is not null;
                    _directionalShadowMapEnabled[i] = perLightShadowEnabled ? 1 : 0;
                    _directionalUseCascadedShadows[i] = perLightUseCascades ? 1 : 0;
                    anyDirectionalShadowEnabled |= perLightShadowEnabled;
                }

                program.Sampler(_directionalShadowMapNames[i], perLightShadowTex ?? DummyShadowMap, directionalShadowMapStartUnit + i);
                program.Sampler(_directionalShadowMapArrayNames[i], perLightCascadeTex ?? DummyShadowMapArray, directionalShadowMapArrayStartUnit + i);
            }

            int pointShadowSlot = 0;
            bool usePointAtlas = RuntimeEngine.Rendering.Settings.UsePointShadowAtlas;
            EShadowMapEncoding pointAtlasEncoding = EShadowMapEncoding.Depth;
            for (int i = 0; i < pointLightCount; i++)
            {
                PointLightComponent light = DynamicPointLights[i];
                if (!light.UsesPointShadowAtlasForCurrentEncoding)
                    continue;

                pointAtlasEncoding = light.ResolveShadowMapFormat(preferredStorageFormat: light.ShadowMapStorageFormat).Encoding;
                break;
            }
            XRTexture2DArray? pointAtlas = null;
            bool pointAtlasTextureAvailable = usePointAtlas &&
                ShadowAtlas.TryGetPageTexture(EShadowAtlasKind.Point, pointAtlasEncoding, 0, out pointAtlas);
            XRTexture2DArray pointAtlasTexture = pointAtlasTextureAvailable && pointAtlas is not null ? pointAtlas : DummyShadowMapArray;
            int pointAtlasLayerCount = checked((int)Math.Max(1u, pointAtlasTexture.Depth));
            program.Sampler(PointShadowAtlasName, pointAtlasTexture, pointShadowAtlasStartUnit);

            if (pointLightCount == 0)
                _forwardPointShadowMetadataBuffer!.Set(0, default(ForwardPointShadowGpu));
            for (int i = 0; i < pointLightCount; ++i)
            {
                PointLightComponent light = DynamicPointLights[i];
                int shadowSlot = -1;
                bool useLightPointAtlas = light.UsesPointShadowAtlasForCurrentEncoding;
                ShadowMapFormatSelection pointShadowFormat = light.ResolveShadowMapFormat(preferredStorageFormat: light.ShadowMapStorageFormat);

                XRTexture? shadowTexture = FindShadowMapTexture(light);
                if (!useLightPointAtlas &&
                    shadowTexture is XRTextureCube shadowCube &&
                    pointShadowSlot < maxForwardShadowedPointLights)
                {
                    shadowSlot = pointShadowSlot;
                    program.Sampler(_pointShadowMapNames[pointShadowSlot], shadowCube, pointShadowStartUnit + pointShadowSlot);
                    pointShadowSlot++;
                }

                ForwardPointShadowGpu pointShadowData = new()
                {
                    Packed0 = new IVector4(shadowSlot, light.FilterSamples, light.BlockerSamples, light.VogelTapCount),
                    Packed1 = new IVector4((int)light.SoftShadowMode, light.ShadowDebugMode, light.EnableContactShadows ? 1 : 0, light.ContactShadowSamples),
                    Params0 = new Vector4(light.ShadowNearPlaneDistance, light.Radius, light.ShadowExponentBase, light.ShadowExponent),
                    Params1 = new Vector4(light.ShadowMinBias, light.ShadowMaxBias, light.FilterRadius, light.BlockerSearchRadius),
                    Params2 = new Vector4(light.MinPenumbra, light.MaxPenumbra, light.EffectiveLightSourceRadius, light.ContactShadowDistance),
                    Params3 = new Vector4(light.ContactShadowThickness, light.ContactShadowFadeStart, light.ContactShadowFadeEnd, light.ContactShadowNormalOffset),
                    Params4 = new Vector4(light.ContactShadowJitterStrength, 0.0f, 0.0f, 0.0f),
                    Params5 = light.ShadowBiasParameters,
                    Packed2 = new IVector4((int)pointShadowFormat.Encoding, light.ShadowMomentUseMipmaps ? 1 : 0, light.ShadowMomentBlurRadiusTexels, light.ShadowMomentBlurPasses),
                    Params6 = new Vector4(light.ShadowMomentMinVariance, light.ShadowMomentLightBleedReduction, pointShadowFormat.PositiveExponent, pointShadowFormat.NegativeExponent),
                };

                for (int faceIndex = 0; faceIndex < PointLightComponent.ShadowFaceCount; faceIndex++)
                {
                    IVector4 atlasPacked0 = new(0, -1, (int)(useLightPointAtlas ? ShadowFallbackMode.ContactOnly : ShadowFallbackMode.Legacy), -1);
                    Vector4 atlasParams0 = Vector4.Zero;
                    Vector4 atlasParams1 = new(light.ShadowNearPlaneDistance, MathF.Max(light.Radius, light.ShadowNearPlaneDistance + 0.001f), 0.0f, 1.0f);

                    if (useLightPointAtlas &&
                        TryGetPointShadowAtlasFaceAllocation(light, faceIndex, out ShadowAtlasAllocation allocation, out int recordIndex))
                    {
                        ShadowFallbackMode fallback = allocation.ActiveFallback != ShadowFallbackMode.None
                            ? allocation.ActiveFallback
                            : ShadowFallbackMode.Lit;
                        bool atlasResident = pointAtlasTextureAvailable &&
                            allocation.Key.Encoding == pointAtlasEncoding &&
                            IsShadowAtlasAllocationSampleable(allocation, pointAtlasLayerCount);
                        uint sampleResolution = LightComponent.GetShadowAtlasSampleResolution(allocation);
                        float texelSize = sampleResolution > 0u ? 1.0f / sampleResolution : 0.0f;
                        float resolutionScale = light.GetShadowAtlasResolutionScale(sampleResolution);

                        atlasPacked0 = new IVector4(atlasResident ? 1 : 0, allocation.PageIndex, (int)fallback, recordIndex);
                        atlasParams0 = allocation.UvScaleBias;
                        atlasParams1 = new Vector4(light.ShadowNearPlaneDistance, MathF.Max(light.Radius, light.ShadowNearPlaneDistance + 0.001f), texelSize, resolutionScale);
                    }

                    SetPointAtlasFaceMetadata(ref pointShadowData, faceIndex, atlasPacked0, atlasParams0, atlasParams1);
                }

                _forwardPointShadowMetadataBuffer!.Set((uint)i, pointShadowData);
            }
            for (; pointShadowSlot < maxForwardShadowedPointLights; ++pointShadowSlot)
                program.Sampler(_pointShadowMapNames[pointShadowSlot], DummyPointShadowMap, pointShadowStartUnit + pointShadowSlot);

            int spotShadowSlot = 0;
            bool useSpotAtlas = RuntimeEngine.Rendering.Settings.UseSpotShadowAtlas;
            EShadowMapEncoding spotAtlasEncoding = EShadowMapEncoding.Depth;
            for (int i = 0; i < spotLightCount; i++)
            {
                SpotLightComponent light = DynamicSpotLights[i];
                if (!light.UsesSpotShadowAtlasForCurrentEncoding)
                    continue;

                spotAtlasEncoding = light.ResolveShadowMapFormat(preferredStorageFormat: light.ShadowMapStorageFormat).Encoding;
                break;
            }
            XRTexture2DArray? spotAtlas = null;
            bool spotAtlasTextureAvailable = useSpotAtlas &&
                ShadowAtlas.TryGetPageTexture(EShadowAtlasKind.Spot, spotAtlasEncoding, 0, out spotAtlas);
            XRTexture2DArray spotAtlasTexture = spotAtlasTextureAvailable && spotAtlas is not null ? spotAtlas : DummyShadowMapArray;
            int spotAtlasLayerCount = checked((int)Math.Max(1u, spotAtlasTexture.Depth));
            program.Sampler(SpotShadowAtlasName, spotAtlasTexture, spotShadowAtlasStartUnit);

            EShadowMapEncoding directionalAtlasEncoding = EShadowMapEncoding.Depth;
            for (int i = 0; i < directionalLightCount; i++)
            {
                DirectionalLightComponent light = DynamicDirectionalLights[i];
                if (!light.UsesDirectionalShadowAtlasForCurrentEncoding)
                    continue;

                directionalAtlasEncoding = light.ResolveShadowMapFormat(preferredStorageFormat: null).Encoding;
                break;
            }
            XRTexture2DArray? directionalAtlas = null;
            bool directionalAtlasTextureAvailable = useDirectionalShadowAtlas &&
                ShadowAtlas.TryGetPageTexture(EShadowAtlasKind.Directional, directionalAtlasEncoding, 0, out directionalAtlas);
            XRTexture2DArray directionalAtlasTexture = directionalAtlasTextureAvailable && directionalAtlas is not null ? directionalAtlas : DummyShadowMapArray;
            int directionalAtlasLayerCount = checked((int)Math.Max(1u, directionalAtlasTexture.Depth));
            program.Sampler(DirectionalShadowAtlasName, directionalAtlasTexture, directionalShadowAtlasStartUnit);

            if (spotLightCount == 0)
                _forwardSpotShadowMetadataBuffer!.Set(0, default(ForwardSpotShadowGpu));
            for (int i = 0; i < spotLightCount; ++i)
            {
                SpotLightComponent light = DynamicSpotLights[i];
                int shadowSlot = -1;
                bool useLightSpotAtlas = light.UsesSpotShadowAtlasForCurrentEncoding;

                IVector4 atlasPacked0 = new(0, -1, (int)ShadowFallbackMode.Lit, -1);
                Vector4 atlasParams0 = Vector4.Zero;
                Vector4 atlasParams1 = Vector4.Zero;
                bool atlasResident = false;
                if (useLightSpotAtlas &&
                    TryGetSpotShadowAtlasAllocation(light, out ShadowAtlasAllocation allocation, out int recordIndex))
                {
                    ShadowFallbackMode fallback = allocation.ActiveFallback != ShadowFallbackMode.None
                        ? allocation.ActiveFallback
                        : ShadowFallbackMode.Lit;
                    atlasResident = spotAtlasTextureAvailable &&
                        allocation.Key.Encoding == spotAtlasEncoding &&
                        IsShadowAtlasAllocationSampleable(allocation, spotAtlasLayerCount);
                    float atlasNearPlane = light.ShadowCamera?.NearZ ?? 0.1f;
                    float atlasFarPlane = light.ShadowCamera?.FarZ ?? MathF.Max(atlasNearPlane + 0.001f, light.Distance);
                    uint sampleResolution = LightComponent.GetShadowAtlasSampleResolution(allocation);
                    float texelSize = sampleResolution > 0u ? 1.0f / sampleResolution : 0.0f;
                    float resolutionScale = light.GetShadowAtlasResolutionScale(sampleResolution);

                    atlasPacked0 = new IVector4(atlasResident ? 1 : 0, allocation.PageIndex, (int)fallback, recordIndex);
                    atlasParams0 = allocation.UvScaleBias;
                    atlasParams1 = new Vector4(atlasNearPlane, atlasFarPlane, texelSize, resolutionScale);
                }

                if (!useLightSpotAtlas && spotShadowSlot < maxForwardShadowedSpotLights)
                {
                    XRTexture? shadowTexture = FindShadowMapTexture(light);
                    if (shadowTexture is XRTexture2D shadowMap)
                    {
                        shadowSlot = spotShadowSlot;
                        program.Sampler(_spotShadowMapNames[spotShadowSlot], shadowMap, spotShadowStartUnit + spotShadowSlot);
                        spotShadowSlot++;
                    }
                }

                ShadowMapFormatSelection shadowFormat = light.ResolveShadowMapFormat(preferredStorageFormat: light.ShadowMapStorageFormat);
                float momentNearPlane = light.ShadowCamera?.NearZ ?? light.ShadowNearPlaneDistance;
                float momentFarPlane = light.ShadowCamera?.FarZ ?? MathF.Max(momentNearPlane + 0.001f, light.Distance);
                _forwardSpotShadowMetadataBuffer!.Set((uint)i, new ForwardSpotShadowGpu
                {
                    Packed0 = new IVector4(shadowSlot, light.FilterSamples, light.BlockerSamples, light.VogelTapCount),
                    Packed1 = new IVector4((int)light.SoftShadowMode, light.ShadowDebugMode, light.EnableContactShadows ? 1 : 0, light.ContactShadowSamples),
                    Params0 = new Vector4(light.ShadowExponentBase, light.ShadowExponent, light.ShadowMinBias, light.ShadowMaxBias),
                    Params1 = new Vector4(light.FilterRadius, light.BlockerSearchRadius, light.MinPenumbra, light.MaxPenumbra),
                    Params2 = new Vector4(light.EffectiveLightSourceRadius, light.ContactShadowDistance, light.ContactShadowThickness, light.ContactShadowFadeStart),
                    Params3 = new Vector4(light.ContactShadowFadeEnd, light.ContactShadowNormalOffset, light.ContactShadowJitterStrength, 0.0f),
                    Packed2 = new IVector4((int)shadowFormat.Encoding, light.ShadowMomentUseMipmaps ? 1 : 0, light.ShadowMomentBlurRadiusTexels, light.ShadowMomentBlurPasses),
                    Params4 = new Vector4(light.ShadowMomentMinVariance, light.ShadowMomentLightBleedReduction, shadowFormat.PositiveExponent, shadowFormat.NegativeExponent),
                    Params5 = new Vector4(momentNearPlane, momentFarPlane, light.ShadowMomentMipBias, 0.0f),
                    AtlasPacked0 = atlasPacked0,
                    AtlasParams0 = atlasParams0,
                    AtlasParams1 = atlasParams1,
                    Params6 = light.ShadowBiasParameters,
                });
            }
            for (; spotShadowSlot < maxForwardShadowedSpotLights; ++spotShadowSlot)
                program.Sampler(_spotShadowMapNames[spotShadowSlot], DummyShadowMap, spotShadowStartUnit + spotShadowSlot);

            // Gate the host->GPU upload of shadow metadata to once per frame; sampler bindings above
            // still run per program. Host-side Set() calls remain (cheap) — only the PushSubData traffic
            // is skipped on repeat invocations within the same frame.
            long shadowFrameTicks = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
            if (shadowFrameTicks != _shadowMetadataUploadedFrameTicks)
            {
                _shadowMetadataUploadedFrameTicks = shadowFrameTicks;
                PushForwardBufferSubData<ForwardPointShadowGpu>(_forwardPointShadowMetadataBuffer!, pointLightCount);
                PushForwardBufferSubData<ForwardSpotShadowGpu>(_forwardSpotShadowMetadataBuffer!, spotLightCount);
            }

            Array.Clear(_directionalShadowAtlasPacked0);
            Array.Clear(_directionalShadowAtlasParams0);
            Array.Clear(_directionalShadowAtlasParams1);
            if (useDirectionalShadowAtlas)
            {
                for (int i = 0; i < directionalLightCount; i++)
                {
                    DirectionalLightComponent dirLight = DynamicDirectionalLights[i];
                    if (!dirLight.CastsShadows)
                        continue;

                    if (dirLight.ResolveShadowMapFormat(preferredStorageFormat: null).Encoding != directionalAtlasEncoding)
                        continue;

                    int atlasRecordOffset = i * ForwardMaxCascades;
                    bool perLightUseCascades = _directionalUseCascadedShadows[i] != 0;
                    dirLight.CopyPublishedDirectionalAtlasUniformData(
                        perLightUseCascades,
                        _directionalShadowAtlasPacked0.AsSpan(atlasRecordOffset, ForwardMaxCascades),
                        _directionalShadowAtlasParams0.AsSpan(atlasRecordOffset, ForwardMaxCascades),
                        _directionalShadowAtlasParams1.AsSpan(atlasRecordOffset, ForwardMaxCascades));

                    bool perLightAtlasSampleable = directionalAtlasTextureAvailable &&
                        AreRequiredDirectionalAtlasTilesSampleable(
                        _directionalShadowAtlasPacked0,
                        atlasRecordOffset,
                        perLightUseCascades ? dirLight.ActiveCascadeCount : 1,
                        directionalAtlasLayerCount);
                    _directionalShadowAtlasEnabled[i] = perLightAtlasSampleable ? 1 : 0;
                    if (perLightAtlasSampleable)
                    {
                        _directionalShadowMapEnabled[i] = 1;
                        anyDirectionalShadowEnabled = true;
                    }

                    if (i == 0)
                        directionalAtlasSampleable = perLightAtlasSampleable;
                }
            }

            program.Uniform("DirectionalShadowMapEnabled", _directionalShadowMapEnabled);
            program.Uniform("DirectionalUseCascadedShadows", _directionalUseCascadedShadows);
            program.Uniform("DirectionalShadowBiasProjectionParams", _directionalShadowBiasProjectionParams);
            program.Uniform("DirectionalShadowAtlasEnabled", _directionalShadowAtlasEnabled);
            program.Uniform("DirectionalShadowMapEncoding", _directionalShadowMapEncoding);
            program.Uniform("DirectionalShadowMomentParams0", _directionalShadowMomentParams0);
            program.Uniform("DirectionalShadowMomentFilterParams", _directionalShadowMomentFilterParams);
            program.Uniform("DirectionalShadowAtlasPacked0", _directionalShadowAtlasPacked0);
            program.Uniform("DirectionalShadowAtlasParams0", _directionalShadowAtlasParams0);
            program.Uniform("DirectionalShadowAtlasParams1", _directionalShadowAtlasParams1);

            LogForwardDirectionalShadowBinding(
                directionalLightCount > 0 ? DynamicDirectionalLights[0] : null,
                useDirectionalShadowAtlas,
                directionalAtlasSampleable,
                useCascadedDirectionalShadows,
                forwardShadowTex,
                forwardCascadeShadowTex);

            bool shadowEnabled = anyDirectionalShadowEnabled;
            program.Uniform("ShadowMapEnabled", shadowEnabled);
            if (!_loggedShadowMapEnabledOnce)
            {
                _loggedShadowMapEnabledOnce = true;
                Debug.Lighting(
                    $"[ForwardShadow] ShadowMapEnabled={shadowEnabled}, " +
                    $"forwardShadowTex={forwardShadowTex?.GetType().Name ?? "null"}, " +
                    $"cascadeTex={forwardCascadeShadowTex?.GetType().Name ?? "null"}, " +
                    $"directionalAtlasSampleable={directionalAtlasSampleable}");
            }

            // Bind the actual shadow texture after per-light SetUniforms.
            // ALWAYS bind a texture to unit 15 - if no shadow map, use a 1x1 white dummy.
            // This prevents OpenGL from sampling stale texture state.
            program.Sampler("ShadowMap", forwardShadowTex ?? DummyShadowMap, forwardShadowMapUnit);
            program.Sampler("ShadowMapArray", forwardCascadeShadowTex ?? DummyShadowMapArray, forwardShadowMapArrayUnit);

            // Bind the legacy cubemap samplers to a stable non-probe fallback.
            // Probe reflections now come from the prefilter/irradiance probe arrays, and
            // light probes may release their transient capture cubemap after IBL generation.
            // These bindings only exist to keep legacy samplerCube uniforms valid.
            const int envMapUnit = 12;
            const int reflCubeUnit = 13;
            float envMipLevels = 1.0f;
            XRTextureCube envCubemap = DummyEnvironmentCubemap;
            if (envCubemap.Mipmaps is { Length: > 0 })
                envMipLevels = envCubemap.Mipmaps.Length;

            program.Sampler("u_EnvironmentMap", envCubemap, envMapUnit);
            program.Uniform("u_EnvironmentMapMipLevels", envMipLevels);
            program.Sampler("_PBRReflCube", envCubemap, reflCubeUnit);
        }

        /// <summary>
        /// Finds the "ShadowMap" texture in a light's shadow material without LINQ allocations.
        /// </summary>
        private static XRTexture? FindShadowMapTexture(LightComponent light)
        {
            if (!light.CastsShadows)
                return null;
            var textures = light.ShadowMap?.Material?.Textures;
            if (textures is null)
                return null;
            for (int t = 0; t < textures.Count; t++)
                if (textures[t]?.SamplerName == "ShadowMap")
                    return textures[t];
            return null;
        }

        private void LogForwardDirectionalShadowBinding(
            DirectionalLightComponent? light,
            bool requested,
            bool shaderAtlasEnabled,
            bool useCascadedDirectionalShadows,
            XRTexture? forwardShadowTex,
            XRTexture2DArray? forwardCascadeShadowTex)
        {
            if (light is null ||
                !Debug.ShouldLogEvery(
                    $"DirectionalShadowAudit.ForwardBind.{light.GetHashCode()}",
                    TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            ShadowAtlasMetrics metrics = ShadowAtlas.PublishedFrameData.Metrics;
            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][ForwardBind] frame={0} light='{1}' requestedAtlas={2} shaderAtlasEnabled={3} cascades={4} activeCascades={5} shadowMapTex={6} cascadeTex={7} atlasRequests={8} atlasRenderedThisFrame={9} atlasPages={10} c0={11} c1={12} c2={13} c3={14}",
                RuntimeEngine.Rendering.State.RenderFrameId,
                light.SceneNode?.Name ?? light.Name ?? light.GetType().Name,
                requested,
                shaderAtlasEnabled,
                useCascadedDirectionalShadows,
                light.ActiveCascadeCount,
                forwardShadowTex?.GetType().Name ?? "null",
                forwardCascadeShadowTex?.GetType().Name ?? "null",
                metrics.RequestCount,
                metrics.TilesScheduledThisFrame,
                metrics.PageCount,
                FormatAtlasPacked(_directionalShadowAtlasPacked0, 0),
                FormatAtlasPacked(_directionalShadowAtlasPacked0, 1),
                FormatAtlasPacked(_directionalShadowAtlasPacked0, 2),
                FormatAtlasPacked(_directionalShadowAtlasPacked0, 3));
        }

        private static bool AreRequiredDirectionalAtlasTilesSampleable(IVector4[] packed0, int count)
            => AreRequiredDirectionalAtlasTilesSampleable(packed0, 0, count, int.MaxValue);

        private static bool AreRequiredDirectionalAtlasTilesSampleable(IVector4[] packed0, int startIndex, int count, int maxPageCount)
        {
            if ((uint)startIndex >= (uint)packed0.Length)
                return false;

            int clampedCount = Math.Min(Math.Max(count, 0), packed0.Length - startIndex);
            if (clampedCount <= 0)
                return false;

            for (int i = 0; i < clampedCount; i++)
            {
                IVector4 packed = packed0[startIndex + i];
                if (packed.X == 0 || packed.Y < 0 || packed.Y >= maxPageCount)
                    return false;
            }

            return true;
        }

        private static string FormatAtlasPacked(IVector4[] packed0, int index)
        {
            if ((uint)index >= (uint)packed0.Length)
                return "<out>";

            IVector4 value = packed0[index];
            return $"({value.X},{value.Y},{value.Z},{value.W})";
        }

        #endregion
    }
}
