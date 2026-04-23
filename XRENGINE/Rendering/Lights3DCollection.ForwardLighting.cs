using System;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        #region Forward Lighting

        // Cached arrays for per-draw shadow metadata uploads.
        // Reused every call to avoid 24 heap allocations per draw on the render thread.
        // Safe because only the single render thread calls SetForwardLightingUniforms.
        private static readonly int[] _pointShadowSlots = new int[16];
        private static readonly float[] _pointShadowNearPlanes = new float[16];
        private static readonly float[] _pointShadowFarPlanes = new float[16];
        private static readonly float[] _pointShadowBase = new float[16];
        private static readonly float[] _pointShadowExponent = new float[16];
        private static readonly float[] _pointShadowBiasMin = new float[16];
        private static readonly float[] _pointShadowBiasMax = new float[16];
        private static readonly int[] _pointShadowSamples = new int[16];
        private static readonly int[] _pointShadowVogelTapCount = new int[16];
        private static readonly float[] _pointShadowFilterRadius = new float[16];
        private static readonly int[] _pointShadowSoftShadowMode = new int[16];
        private static readonly float[] _pointShadowLightSourceRadius = new float[16];
        private static readonly int[] _pointShadowDebugModes = new int[16];

        private static readonly int[] _spotShadowSlots = new int[16];
        private static readonly float[] _spotShadowBase = new float[16];
        private static readonly float[] _spotShadowExponent = new float[16];
        private static readonly float[] _spotShadowBiasMin = new float[16];
        private static readonly float[] _spotShadowBiasMax = new float[16];
        private static readonly int[] _spotShadowSamples = new int[16];
        private static readonly int[] _spotShadowVogelTapCount = new int[16];
        private static readonly float[] _spotShadowFilterRadius = new float[16];
        private static readonly int[] _spotShadowSoftShadowMode = new int[16];
        private static readonly float[] _spotShadowLightSourceRadius = new float[16];
        private static readonly int[] _spotShadowDebugModes = new int[16];

        // Pre-computed uniform name strings for light array indices.
        // Avoids per-draw string interpolation allocations on the render thread.
        private static readonly string[] _dirLightGenNames = CreateIndexedNames("DirectionalLights", 16);
        private static readonly string[] _dirLightLegacyNames = CreateIndexedNames("DirLightData", 16);
        private static readonly string[] _spotLightGenNames = CreateIndexedNames("SpotLights", 16);
        private static readonly string[] _spotLightLegacyNames = CreateIndexedNames("SpotLightData", 16);
        private static readonly string[] _pointLightGenNames = CreateIndexedNames("PointLights", 16);
        private static readonly string[] _pointLightLegacyNames = CreateIndexedNames("PointLightData", 16);
        private static readonly string[] _pointShadowMapNames = CreateIndexedNames("PointLightShadowMaps", 4);
        private static readonly string[] _spotShadowMapNames = CreateIndexedNames("SpotLightShadowMaps", 4);

        private static string[] CreateIndexedNames(string prefix, int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++)
                names[i] = $"{prefix}[{i}]";
            return names;
        }

        internal void SetForwardLightingUniforms(XRRenderProgram program)
        {
            const int maxForwardShadowedPointLights = 4;
            const int maxForwardShadowedSpotLights = 4;
            const int pointShadowStartUnit = 17;
            const int spotShadowStartUnit = 21;
            const int forwardAmbientOcclusionArrayUnit = 25;

            // Debug: log that we're being called
            if (!_loggedForwardLightingOnce)
            {
                _loggedForwardLightingOnce = true;
                Debug.Out($"[ForwardLighting] SetForwardLightingUniforms called. DirLights={DynamicDirectionalLights.Count}, PointLights={DynamicPointLights.Count}, SpotLights={DynamicSpotLights.Count}");
            }

            // Global ambient light - required by ForwardLighting snippet
            program.Uniform("GlobalAmbient", new Vector3(0.1f, 0.1f, 0.1f));

            // Camera position for specular calculations
            program.Uniform("CameraPosition", Engine.Rendering.State.RenderingCamera?.Transform.RenderTranslation ?? Vector3.Zero);

            var area = Engine.Rendering.State.RenderArea;
            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)area.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)area.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), new Vector2(area.X, area.Y));

            program.Uniform("DirLightCount", DynamicDirectionalLights.Count);
            program.Uniform("PointLightCount", DynamicPointLights.Count);
            program.Uniform("SpotLightCount", DynamicSpotLights.Count);

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
            if (DynamicDirectionalLights.Count > 0)
            {
                var firstDirLight = DynamicDirectionalLights[0];
                program.Uniform("ShadowBase", firstDirLight.ShadowExponentBase);
                program.Uniform("ShadowMult", firstDirLight.ShadowExponent);
                program.Uniform("ShadowBiasMin", firstDirLight.ShadowMinBias);
                program.Uniform("ShadowBiasMax", firstDirLight.ShadowMaxBias);
                program.Uniform("ShadowSamples", firstDirLight.Samples);
                program.Uniform("ShadowVogelTapCount", firstDirLight.VogelTapCount);
                program.Uniform("ShadowFilterRadius", firstDirLight.FilterRadius);
                program.Uniform("SoftShadowMode", (int)firstDirLight.SoftShadowMode);
                program.Uniform("LightSourceRadius", firstDirLight.LightSourceRadius);
                program.Uniform("EnableCascadedShadows", firstDirLight.EnableCascadedShadows);
                program.Uniform("EnableContactShadows", firstDirLight.EnableContactShadows);
                program.Uniform("ContactShadowDistance", firstDirLight.ContactShadowDistance);
                program.Uniform("ContactShadowSamples", firstDirLight.ContactShadowSamples);

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
            bool shadowEnabled = forwardShadowTex != null || forwardCascadeShadowTex != null;
            program.Uniform("ShadowMapEnabled", shadowEnabled);
            program.Uniform("UseCascadedDirectionalShadows", useCascadedDirectionalShadows);
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

            // ALWAYS set the ShadowMap sampler to point to unit 15, even when shadows are disabled.
            // This prevents stale state from deferred passes (which use unit 4) from leaking through.
            // The shader's layout(binding=15) should handle this, but we force it to be safe against
            // cached shader binaries that might not have the layout qualifier.
            program.Uniform("ShadowMap", forwardShadowMapUnit);
            program.Uniform("ShadowMapArray", forwardShadowMapArrayUnit);

            for (int i = 0; i < DynamicDirectionalLights.Count; ++i)
            {
                DynamicDirectionalLights[i].SetUniforms(program, _dirLightGenNames[i]);
                DynamicDirectionalLights[i].SetUniforms(program, _dirLightLegacyNames[i]);
            }
            for (int i = 0; i < DynamicSpotLights.Count; ++i)
            {
                DynamicSpotLights[i].SetUniforms(program, _spotLightGenNames[i]);
                DynamicSpotLights[i].SetUniforms(program, _spotLightLegacyNames[i]);
            }
            for (int i = 0; i < DynamicPointLights.Count; ++i)
            {
                DynamicPointLights[i].SetUniforms(program, _pointLightGenNames[i]);
                DynamicPointLights[i].SetUniforms(program, _pointLightLegacyNames[i]);
            }

            Array.Fill(_pointShadowSlots, -1);
            Array.Clear(_pointShadowNearPlanes);
            Array.Clear(_pointShadowFarPlanes);
            Array.Clear(_pointShadowBase);
            Array.Clear(_pointShadowExponent);
            Array.Clear(_pointShadowBiasMin);
            Array.Clear(_pointShadowBiasMax);
            Array.Clear(_pointShadowSamples);
            Array.Clear(_pointShadowVogelTapCount);
            Array.Clear(_pointShadowFilterRadius);
            Array.Clear(_pointShadowSoftShadowMode);
            Array.Clear(_pointShadowLightSourceRadius);
            Array.Clear(_pointShadowDebugModes);
            int pointShadowSlot = 0;
            for (int i = 0; i < DynamicPointLights.Count && i < _pointShadowSlots.Length; ++i)
            {
                PointLightComponent light = DynamicPointLights[i];
                _pointShadowNearPlanes[i] = light.ShadowNearPlaneDistance;
                _pointShadowFarPlanes[i] = light.Radius;
                _pointShadowBase[i] = light.ShadowExponentBase;
                _pointShadowExponent[i] = light.ShadowExponent;
                _pointShadowBiasMin[i] = light.ShadowMinBias;
                _pointShadowBiasMax[i] = light.ShadowMaxBias;
                _pointShadowSamples[i] = light.Samples;
                _pointShadowVogelTapCount[i] = light.VogelTapCount;
                _pointShadowFilterRadius[i] = light.FilterRadius;
                _pointShadowSoftShadowMode[i] = (int)light.SoftShadowMode;
                _pointShadowLightSourceRadius[i] = light.LightSourceRadius;
                _pointShadowDebugModes[i] = light.ShadowDebugMode;

                XRTexture? shadowTexture = FindShadowMapTexture(light);
                if (shadowTexture is XRTextureCube shadowCube && pointShadowSlot < maxForwardShadowedPointLights)
                {
                    _pointShadowSlots[i] = pointShadowSlot;
                    program.Sampler(_pointShadowMapNames[pointShadowSlot], shadowCube, pointShadowStartUnit + pointShadowSlot);
                    pointShadowSlot++;
                }
            }
            for (; pointShadowSlot < maxForwardShadowedPointLights; ++pointShadowSlot)
                program.Sampler(_pointShadowMapNames[pointShadowSlot], DummyPointShadowMap, pointShadowStartUnit + pointShadowSlot);

            Array.Fill(_spotShadowSlots, -1);
            Array.Clear(_spotShadowBase);
            Array.Clear(_spotShadowExponent);
            Array.Clear(_spotShadowBiasMin);
            Array.Clear(_spotShadowBiasMax);
            Array.Clear(_spotShadowSamples);
            Array.Clear(_spotShadowVogelTapCount);
            Array.Clear(_spotShadowFilterRadius);
            Array.Clear(_spotShadowSoftShadowMode);
            Array.Clear(_spotShadowLightSourceRadius);
            Array.Clear(_spotShadowDebugModes);
            int spotShadowSlot = 0;
            for (int i = 0; i < DynamicSpotLights.Count && i < _spotShadowSlots.Length; ++i)
            {
                SpotLightComponent light = DynamicSpotLights[i];
                _spotShadowBase[i] = light.ShadowExponentBase;
                _spotShadowExponent[i] = light.ShadowExponent;
                _spotShadowBiasMin[i] = light.ShadowMinBias;
                _spotShadowBiasMax[i] = light.ShadowMaxBias;
                _spotShadowSamples[i] = light.Samples;
                _spotShadowVogelTapCount[i] = light.VogelTapCount;
                _spotShadowFilterRadius[i] = light.FilterRadius;
                _spotShadowSoftShadowMode[i] = (int)light.SoftShadowMode;
                _spotShadowLightSourceRadius[i] = light.LightSourceRadius;
                _spotShadowDebugModes[i] = light.ShadowDebugMode;

                XRTexture? shadowTexture = FindShadowMapTexture(light);
                if (shadowTexture is XRTexture2D shadowMap && spotShadowSlot < maxForwardShadowedSpotLights)
                {
                    _spotShadowSlots[i] = spotShadowSlot;
                    program.Sampler(_spotShadowMapNames[spotShadowSlot], shadowMap, spotShadowStartUnit + spotShadowSlot);
                    spotShadowSlot++;
                }
            }
            for (; spotShadowSlot < maxForwardShadowedSpotLights; ++spotShadowSlot)
                program.Sampler(_spotShadowMapNames[spotShadowSlot], DummyShadowMap, spotShadowStartUnit + spotShadowSlot);

            program.Uniform("PointLightShadowSlots", _pointShadowSlots);
            program.Uniform("PointLightShadowNearPlanes", _pointShadowNearPlanes);
            program.Uniform("PointLightShadowFarPlanes", _pointShadowFarPlanes);
            program.Uniform("PointLightShadowBase", _pointShadowBase);
            program.Uniform("PointLightShadowExponent", _pointShadowExponent);
            program.Uniform("PointLightShadowBiasMin", _pointShadowBiasMin);
            program.Uniform("PointLightShadowBiasMax", _pointShadowBiasMax);
            program.Uniform("PointLightShadowSamples", _pointShadowSamples);
            program.Uniform("PointLightShadowVogelTapCount", _pointShadowVogelTapCount);
            program.Uniform("PointLightShadowFilterRadius", _pointShadowFilterRadius);
            program.Uniform("PointLightShadowSoftShadowMode", _pointShadowSoftShadowMode);
            program.Uniform("PointLightShadowLightSourceRadius", _pointShadowLightSourceRadius);
            program.Uniform("PointLightShadowDebugModes", _pointShadowDebugModes);

            program.Uniform("SpotLightShadowSlots", _spotShadowSlots);
            program.Uniform("SpotLightShadowBase", _spotShadowBase);
            program.Uniform("SpotLightShadowExponent", _spotShadowExponent);
            program.Uniform("SpotLightShadowBiasMin", _spotShadowBiasMin);
            program.Uniform("SpotLightShadowBiasMax", _spotShadowBiasMax);
            program.Uniform("SpotLightShadowSamples", _spotShadowSamples);
            program.Uniform("SpotLightShadowVogelTapCount", _spotShadowVogelTapCount);
            program.Uniform("SpotLightShadowFilterRadius", _spotShadowFilterRadius);
            program.Uniform("SpotLightShadowSoftShadowMode", _spotShadowSoftShadowMode);
            program.Uniform("SpotLightShadowLightSourceRadius", _spotShadowLightSourceRadius);
            program.Uniform("SpotLightShadowDebugModes", _spotShadowDebugModes);

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
