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

        private static readonly IVector4[] _directionalShadowAtlasPacked0 = new IVector4[8];
        private static readonly Vector4[] _directionalShadowAtlasParams0 = new Vector4[8];
        private static readonly Vector4[] _directionalShadowAtlasParams1 = new Vector4[8];

        // Pre-computed sampler uniform names for indexed shadow resources.
        // Avoids per-draw string interpolation allocations on the render thread.
        private static readonly string[] _pointShadowMapNames = CreateIndexedNames("PointLightShadowMaps", 4);
        private static readonly string[] _spotShadowMapNames = CreateIndexedNames("SpotLightShadowMaps", 4);
        private static readonly string[] _spotShadowAtlasPageNames = CreateIndexedNames("SpotLightShadowAtlasPages", 2);
        private static readonly string[] _directionalShadowAtlasPageNames = CreateIndexedNames("DirectionalShadowAtlasPages", 2);

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
                WorldToLightInvViewMatrix = light.ShadowCamera?.Transform.WorldMatrix ?? Matrix4x4.Identity,
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

        internal void SetForwardLightingUniforms(XRRenderProgram program)
        {
            const int maxForwardShadowedPointLights = 4;
            const int maxForwardShadowedSpotLights = 4;
            const int pointShadowStartUnit = 17;
            const int spotShadowStartUnit = 21;
            const int forwardAmbientOcclusionArrayUnit = 25;
            const int forwardContactDepthUnit = 26;
            const int forwardContactNormalUnit = 27;
            const int forwardContactDepthArrayUnit = 28;
            const int forwardContactNormalArrayUnit = 29;
            const int directionalShadowAtlasStartUnit = 9;
            const int spotShadowAtlasStartUnit = 30;
            const int maxForwardSpotAtlasPages = 2;
            const int maxForwardDirectionalAtlasPages = 2;

            // Debug: log that we're being called
            if (!_loggedForwardLightingOnce)
            {
                _loggedForwardLightingOnce = true;
                Debug.Out($"[ForwardLighting] SetForwardLightingUniforms called. DirLights={DynamicDirectionalLights.Count}, PointLights={DynamicPointLights.Count}, SpotLights={DynamicSpotLights.Count}");
            }

            // Global ambient light - required by ForwardLighting snippet
            program.Uniform("GlobalAmbient", (Vector3)World.GetEffectiveAmbientColor());

            // Camera position for specular calculations
            program.Uniform("CameraPosition", Engine.Rendering.State.RenderingCamera?.Transform.RenderTranslation ?? Vector3.Zero);

            var area = Engine.Rendering.State.RenderArea;
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
            program.Uniform("ForwardPlusEnabled", Engine.Rendering.State.ForwardPlusEnabled);
            if (Engine.Rendering.State.ForwardPlusEnabled)
            {
                program.Uniform("ForwardPlusScreenSize", Engine.Rendering.State.ForwardPlusScreenSize);
                program.Uniform("ForwardPlusTileSize", Engine.Rendering.State.ForwardPlusTileSize);
                program.Uniform("ForwardPlusTileCountX", Engine.Rendering.State.ForwardPlusTileCountX);
                program.Uniform("ForwardPlusTileCountY", Engine.Rendering.State.ForwardPlusTileCountY);
                program.Uniform("ForwardPlusMaxLightsPerTile", Engine.Rendering.State.ForwardPlusMaxLightsPerTile);

                // Keep bindings in sync with the compute shader: 20 (local lights), 21 (visible indices).
                program.BindBuffer(Engine.Rendering.State.ForwardPlusLocalLightsBuffer!, 20u);
                program.BindBuffer(Engine.Rendering.State.ForwardPlusVisibleIndicesBuffer!, 21u);
            }
            program.Uniform("ForwardPlusEyeCount", Engine.Rendering.State.IsStereoPass ? 2 : 1);

            const int forwardAmbientOcclusionUnit = 14;
            XRTexture? ambientOcclusionTexture = null;
            var currentPipeline = Engine.Rendering.State.CurrentRenderingPipeline;
            var ambientOcclusionCamera = Engine.Rendering.State.RenderingCamera
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
            if (Engine.EditorPreferences.Debug.ForwardDepthPrePassEnabled && currentPipeline is not null)
            {
                currentPipeline.TryGetTexture(DefaultRenderPipeline.DepthViewTextureName, out forwardContactDepthTexture);
                currentPipeline.TryGetTexture(DefaultRenderPipeline.NormalTextureName, out forwardContactNormalTexture);
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
                    program.Uniform("ForwardPbrResourcesEnabled", false);
                    program.Uniform("ProbeCount", 0);
                    program.Uniform("TetraCount", 0);
                    program.Uniform("UseProbeGrid", false);
                    break;
            }

            /*
            Debug.RenderingEvery(
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
                    Debug.RenderingEvery(
                        "ForwardAO.Content.2D",
                        TimeSpan.FromSeconds(1),
                        "[ForwardAO] centerAo={0:F4} size={1}x{2}",
                        centerAo,
                        aoTexture2D.Width,
                        aoTexture2D.Height);
                }
                else if (ambientOcclusionTexture is XRTexture2DArray aoTexture2DArray)
                {
                    Debug.RenderingEvery(
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
                Debug.Out($"[ForwardAO] Initial binding enabled={ambientOcclusionEnabled}, texture={ambientOcclusionTexture?.Name ?? "null"}, textureType={ambientOcclusionTexture?.GetType().Name ?? "null"}, screen={area.Width}x{area.Height}, origin=<{area.X}, {area.Y}>");
            }
            */

            // Forward materials bind their own textures at units [0..N) where N is the texture index.
            // Using a low fixed unit (like 4) for the shadow map collides with multi-texture materials
            // (e.g., Sponza) and manifests as "shadow" sampling a regular color texture.
            // Pick a dedicated high unit for forward shadow sampling.
            const int forwardShadowMapUnit = 15;
            const int forwardShadowMapArrayUnit = 16;
            XRTexture? forwardShadowTex = null;
            XRTexture2DArray? forwardCascadeShadowTex = null;
            Matrix4x4 primaryDirLightWorldToLightInvView = Matrix4x4.Identity;
            Matrix4x4 primaryDirLightWorldToLightProj = Matrix4x4.Identity;
            bool useCascadedDirectionalShadows = false;
            bool useDirectionalShadowAtlas = false;
            if (directionalLightCount > 0)
            {
                var firstDirLight = DynamicDirectionalLights[0];
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

                if (firstDirLight.CastsShadows)
                {
                    // 2D shadow map (non-cascaded / fallback path).
                    if (firstDirLight.ShadowMap?.Material?.Textures.Count > 0)
                        forwardShadowTex = firstDirLight.ShadowMap.Material.Textures[0];

                    if (firstDirLight is OneViewLightComponent oneView && oneView.ShadowCamera is not null)
                    {
                        primaryDirLightWorldToLightInvView = oneView.ShadowCamera.Transform.RenderMatrix;
                        primaryDirLightWorldToLightProj = oneView.ShadowCamera.ProjectionMatrix;
                    }

                    // Cascaded shadow array — evaluated independently of the 2D map because
                    // in cascaded mode the 2D ShadowMap may never be populated but the
                    // cascade array IS available. Previously this branch was nested inside
                    // the 2D-map-populated check, so cascaded-only configs silently dropped
                    // all directional shadows (including the volumetric fog scatter pass).
                    var cameraComponent = currentPipeline?.RenderState.WindowViewport?.CameraComponent;
                    useCascadedDirectionalShadows =
                        cameraComponent?.DirectionalShadowRenderingMode == EDirectionalShadowRenderingMode.Cascaded &&
                        firstDirLight.EnableCascadedShadows &&
                        firstDirLight.CascadedShadowMapTexture is not null &&
                        firstDirLight.ActiveCascadeCount > 0;

                    if (useCascadedDirectionalShadows)
                        forwardCascadeShadowTex = firstDirLight.CascadedShadowMapTexture;

                    useDirectionalShadowAtlas =
                        Engine.Rendering.Settings.UseDirectionalShadowAtlas &&
                        useCascadedDirectionalShadows &&
                        firstDirLight.ActiveCascadeCount > 0;

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
                            Debug.Out($"[ForwardShadow] No shadow tex: {reason}");
                        }
                    }
                }
                else
                {
                    string reason = "CastsShadows=false";
                    if (reason != _lastForwardShadowNoTexReason)
                    {
                        _lastForwardShadowNoTexReason = reason;
                        Debug.Out($"[ForwardShadow] No shadow tex: {reason}");
                    }
                }
            }
            // ShadowMapEnabled is a "shader may sample a directional shadow" flag.
            // Before: it was forwardShadowTex != null, which only considered the 2D
            // ShadowMap. When the pipeline runs in cascaded mode the 2D map is often
            // unpopulated and only forwardCascadeShadowTex is available, so this
            // flag flipped to false and every consumer (uber shader, volumetric fog
            // scatter, etc.) short-circuited to "fully lit" — no shafts, no shadows.
            // Treat either texture as "shadows available"; downstream shaders already
            // prefer the cascade path when UseCascadedDirectionalShadows is true and
            // fall back to the 2D map otherwise.
            bool shadowEnabled = forwardShadowTex != null || forwardCascadeShadowTex != null || useDirectionalShadowAtlas;
            program.Uniform("ShadowMapEnabled", shadowEnabled);
            program.Uniform("UseCascadedDirectionalShadows", useCascadedDirectionalShadows);
            program.Uniform("DirectionalShadowAtlasEnabled", useDirectionalShadowAtlas);
            program.Uniform("PrimaryDirLightWorldToLightInvViewMatrix", primaryDirLightWorldToLightInvView);
            program.Uniform("PrimaryDirLightWorldToLightProjMatrix", primaryDirLightWorldToLightProj);
            if (!_loggedShadowMapEnabledOnce)
            {
                _loggedShadowMapEnabledOnce = true;
                Debug.Out($"[ForwardShadow] ShadowMapEnabled={shadowEnabled}, forwardShadowTex={forwardShadowTex?.GetType().Name ?? "null"}");
            }

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
                    Debug.Out(
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

            int pointShadowSlot = 0;
            if (pointLightCount == 0)
                _forwardPointShadowMetadataBuffer!.Set(0, default(ForwardPointShadowGpu));
            for (int i = 0; i < pointLightCount; ++i)
            {
                PointLightComponent light = DynamicPointLights[i];
                int shadowSlot = -1;

                XRTexture? shadowTexture = FindShadowMapTexture(light);
                if (shadowTexture is XRTextureCube shadowCube && pointShadowSlot < maxForwardShadowedPointLights)
                {
                    shadowSlot = pointShadowSlot;
                    program.Sampler(_pointShadowMapNames[pointShadowSlot], shadowCube, pointShadowStartUnit + pointShadowSlot);
                    pointShadowSlot++;
                }

                _forwardPointShadowMetadataBuffer!.Set((uint)i, new ForwardPointShadowGpu
                {
                    Packed0 = new IVector4(shadowSlot, light.FilterSamples, light.BlockerSamples, light.VogelTapCount),
                    Packed1 = new IVector4((int)light.SoftShadowMode, light.ShadowDebugMode, light.EnableContactShadows ? 1 : 0, light.ContactShadowSamples),
                    Params0 = new Vector4(light.ShadowNearPlaneDistance, light.Radius, light.ShadowExponentBase, light.ShadowExponent),
                    Params1 = new Vector4(light.ShadowMinBias, light.ShadowMaxBias, light.FilterRadius, light.BlockerSearchRadius),
                    Params2 = new Vector4(light.MinPenumbra, light.MaxPenumbra, light.EffectiveLightSourceRadius, light.ContactShadowDistance),
                    Params3 = new Vector4(light.ContactShadowThickness, light.ContactShadowFadeStart, light.ContactShadowFadeEnd, light.ContactShadowNormalOffset),
                    Params4 = new Vector4(light.ContactShadowJitterStrength, 0.0f, 0.0f, 0.0f),
                });
            }
            for (; pointShadowSlot < maxForwardShadowedPointLights; ++pointShadowSlot)
                program.Sampler(_pointShadowMapNames[pointShadowSlot], DummyPointShadowMap, pointShadowStartUnit + pointShadowSlot);

            int spotShadowSlot = 0;
            bool useSpotAtlas = Engine.Rendering.Settings.UseSpotShadowAtlas;
            for (int pageIndex = 0; pageIndex < maxForwardSpotAtlasPages; pageIndex++)
            {
                XRTexture2D atlasTexture = ShadowAtlas.TryGetPageTexture(EShadowMapEncoding.Depth, pageIndex, out XRTexture2D pageTexture)
                    ? pageTexture
                    : DummyShadowMap;
                program.Sampler(_spotShadowAtlasPageNames[pageIndex], atlasTexture, spotShadowAtlasStartUnit + pageIndex);
            }

            for (int pageIndex = 0; pageIndex < maxForwardDirectionalAtlasPages; pageIndex++)
            {
                XRTexture2D atlasTexture = useDirectionalShadowAtlas &&
                    ShadowAtlas.TryGetPageTexture(EShadowMapEncoding.Depth, pageIndex, out XRTexture2D pageTexture)
                        ? pageTexture
                        : DummyShadowMap;
                program.Sampler(_directionalShadowAtlasPageNames[pageIndex], atlasTexture, directionalShadowAtlasStartUnit + pageIndex);
            }

            if (spotLightCount == 0)
                _forwardSpotShadowMetadataBuffer!.Set(0, default(ForwardSpotShadowGpu));
            for (int i = 0; i < spotLightCount; ++i)
            {
                SpotLightComponent light = DynamicSpotLights[i];
                int shadowSlot = -1;

                IVector4 atlasPacked0 = new(0, -1, (int)ShadowFallbackMode.Lit, -1);
                Vector4 atlasParams0 = Vector4.Zero;
                Vector4 atlasParams1 = Vector4.Zero;
                bool atlasResident = false;
                if (useSpotAtlas &&
                    TryGetSpotShadowAtlasAllocation(light, out ShadowAtlasAllocation allocation, out int recordIndex))
                {
                    ShadowFallbackMode fallback = allocation.ActiveFallback != ShadowFallbackMode.None
                        ? allocation.ActiveFallback
                        : ShadowFallbackMode.Lit;
                    atlasResident = allocation.IsResident &&
                        allocation.LastRenderedFrame != 0u &&
                        allocation.PageIndex >= 0 &&
                        allocation.PageIndex < maxForwardSpotAtlasPages;
                    float atlasNearPlane = light.ShadowCamera?.NearZ ?? 0.1f;
                    float atlasFarPlane = light.ShadowCamera?.FarZ ?? MathF.Max(atlasNearPlane + 0.001f, light.Distance);
                    float texelSize = allocation.Resolution > 0u ? 1.0f / allocation.Resolution : 0.0f;

                    atlasPacked0 = new IVector4(atlasResident ? 1 : 0, allocation.PageIndex, (int)fallback, recordIndex);
                    atlasParams0 = allocation.UvScaleBias;
                    atlasParams1 = new Vector4(atlasNearPlane, atlasFarPlane, texelSize, 0.0f);
                }

                if ((!useSpotAtlas || !atlasResident) && spotShadowSlot < maxForwardShadowedSpotLights)
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
                    Packed2 = new IVector4((int)shadowFormat.Encoding, light.ShadowMomentUseMipmaps ? 1 : 0, 0, 0),
                    Params4 = new Vector4(light.ShadowMomentMinVariance, light.ShadowMomentLightBleedReduction, shadowFormat.PositiveExponent, shadowFormat.NegativeExponent),
                    Params5 = new Vector4(momentNearPlane, momentFarPlane, light.ShadowMomentMipBias, 0.0f),
                    AtlasPacked0 = atlasPacked0,
                    AtlasParams0 = atlasParams0,
                    AtlasParams1 = atlasParams1,
                });
            }
            for (; spotShadowSlot < maxForwardShadowedSpotLights; ++spotShadowSlot)
                program.Sampler(_spotShadowMapNames[spotShadowSlot], DummyShadowMap, spotShadowStartUnit + spotShadowSlot);

            PushForwardBufferSubData<ForwardPointShadowGpu>(_forwardPointShadowMetadataBuffer!, pointLightCount);
            PushForwardBufferSubData<ForwardSpotShadowGpu>(_forwardSpotShadowMetadataBuffer!, spotLightCount);

            Array.Clear(_directionalShadowAtlasPacked0);
            Array.Clear(_directionalShadowAtlasParams0);
            Array.Clear(_directionalShadowAtlasParams1);
            if (useDirectionalShadowAtlas && directionalLightCount > 0)
            {
                DynamicDirectionalLights[0].CopyPublishedCascadeAtlasUniformData(
                    _directionalShadowAtlasPacked0,
                    _directionalShadowAtlasParams0,
                    _directionalShadowAtlasParams1);
            }

            program.Uniform("DirectionalShadowAtlasPacked0", _directionalShadowAtlasPacked0);
            program.Uniform("DirectionalShadowAtlasParams0", _directionalShadowAtlasParams0);
            program.Uniform("DirectionalShadowAtlasParams1", _directionalShadowAtlasParams1);

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

        #endregion
    }
}
