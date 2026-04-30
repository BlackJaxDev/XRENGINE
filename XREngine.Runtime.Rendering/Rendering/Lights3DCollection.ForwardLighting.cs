using System;
using System.Numerics;
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

        // Cached arrays for per-draw shadow metadata uploads.
        // Reused every call to avoid 24 heap allocations per draw on the render thread.
        // Safe because only the single render thread calls SetForwardLightingUniforms.
        private static readonly IVector4 _inactiveShadowPack = new(-1, 0, 0, 0);
        private static readonly IVector4[] _pointShadowPacked0 = new IVector4[16];
        private static readonly IVector4[] _pointShadowPacked1 = new IVector4[16];
        private static readonly Vector4[] _pointShadowParams0 = new Vector4[16];
        private static readonly Vector4[] _pointShadowParams1 = new Vector4[16];
        private static readonly Vector4[] _pointShadowParams2 = new Vector4[16];
        private static readonly Vector4[] _pointShadowParams3 = new Vector4[16];
        private static readonly Vector4[] _pointShadowParams4 = new Vector4[16];

        private static readonly IVector4[] _spotShadowPacked0 = new IVector4[16];
        private static readonly IVector4[] _spotShadowPacked1 = new IVector4[16];
        private static readonly Vector4[] _spotShadowParams0 = new Vector4[16];
        private static readonly Vector4[] _spotShadowParams1 = new Vector4[16];
        private static readonly Vector4[] _spotShadowParams2 = new Vector4[16];
        private static readonly Vector4[] _spotShadowParams3 = new Vector4[16];
        private static readonly IVector4[] _spotShadowPacked2 = new IVector4[16];
        private static readonly Vector4[] _spotShadowParams4 = new Vector4[16];
        private static readonly Vector4[] _spotShadowParams5 = new Vector4[16];
        private static readonly IVector4[] _spotShadowAtlasPacked0 = new IVector4[16];
        private static readonly Vector4[] _spotShadowAtlasParams0 = new Vector4[16];
        private static readonly Vector4[] _spotShadowAtlasParams1 = new Vector4[16];
        private static readonly IVector4[] _directionalShadowAtlasPacked0 = new IVector4[8];
        private static readonly Vector4[] _directionalShadowAtlasParams0 = new Vector4[8];
        private static readonly Vector4[] _directionalShadowAtlasParams1 = new Vector4[8];

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
        private static readonly string[] _spotShadowAtlasPageNames = CreateIndexedNames("SpotLightShadowAtlasPages", 2);
        private static readonly string[] _directionalShadowAtlasPageNames = CreateIndexedNames("DirectionalShadowAtlasPages", 2);

        private static string[] CreateIndexedNames(string prefix, int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++)
                names[i] = $"{prefix}[{i}]";
            return names;
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
            if (DynamicDirectionalLights.Count > 0)
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

            Array.Fill(_pointShadowPacked0, _inactiveShadowPack);
            Array.Clear(_pointShadowPacked1);
            Array.Clear(_pointShadowParams0);
            Array.Clear(_pointShadowParams1);
            Array.Clear(_pointShadowParams2);
            Array.Clear(_pointShadowParams3);
            Array.Clear(_pointShadowParams4);
            int pointShadowSlot = 0;
            for (int i = 0; i < DynamicPointLights.Count && i < _pointShadowPacked0.Length; ++i)
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

                _pointShadowPacked0[i] = new IVector4(shadowSlot, light.FilterSamples, light.BlockerSamples, light.VogelTapCount);
                _pointShadowPacked1[i] = new IVector4((int)light.SoftShadowMode, light.ShadowDebugMode, light.EnableContactShadows ? 1 : 0, light.ContactShadowSamples);
                _pointShadowParams0[i] = new Vector4(light.ShadowNearPlaneDistance, light.Radius, light.ShadowExponentBase, light.ShadowExponent);
                _pointShadowParams1[i] = new Vector4(light.ShadowMinBias, light.ShadowMaxBias, light.FilterRadius, light.BlockerSearchRadius);
                _pointShadowParams2[i] = new Vector4(light.MinPenumbra, light.MaxPenumbra, light.EffectiveLightSourceRadius, light.ContactShadowDistance);
                _pointShadowParams3[i] = new Vector4(light.ContactShadowThickness, light.ContactShadowFadeStart, light.ContactShadowFadeEnd, light.ContactShadowNormalOffset);
                _pointShadowParams4[i] = new Vector4(light.ContactShadowJitterStrength, 0.0f, 0.0f, 0.0f);
            }
            for (; pointShadowSlot < maxForwardShadowedPointLights; ++pointShadowSlot)
                program.Sampler(_pointShadowMapNames[pointShadowSlot], DummyPointShadowMap, pointShadowStartUnit + pointShadowSlot);

            Array.Fill(_spotShadowPacked0, _inactiveShadowPack);
            Array.Clear(_spotShadowPacked1);
            Array.Clear(_spotShadowParams0);
            Array.Clear(_spotShadowParams1);
            Array.Clear(_spotShadowParams2);
            Array.Clear(_spotShadowParams3);
            Array.Clear(_spotShadowPacked2);
            Array.Clear(_spotShadowParams4);
            Array.Clear(_spotShadowParams5);
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
                XRTexture2D atlasDepthTexture = useDirectionalShadowAtlas &&
                    ShadowAtlas.TryGetPageRasterDepthTexture(EShadowMapEncoding.Depth, pageIndex, out XRTexture2D pageDepthTexture)
                        ? pageDepthTexture
                        : DummyShadowMap;
                program.Sampler(_directionalShadowAtlasPageNames[pageIndex], atlasDepthTexture, directionalShadowAtlasStartUnit + pageIndex);
            }

            Array.Clear(_spotShadowAtlasPacked0);
            Array.Clear(_spotShadowAtlasParams0);
            Array.Clear(_spotShadowAtlasParams1);
            for (int i = 0; i < DynamicSpotLights.Count && i < _spotShadowPacked0.Length; ++i)
            {
                SpotLightComponent light = DynamicSpotLights[i];
                int shadowSlot = -1;

                if (!useSpotAtlas)
                {
                    XRTexture? shadowTexture = FindShadowMapTexture(light);
                    if (shadowTexture is XRTexture2D shadowMap && spotShadowSlot < maxForwardShadowedSpotLights)
                    {
                        shadowSlot = spotShadowSlot;
                        program.Sampler(_spotShadowMapNames[spotShadowSlot], shadowMap, spotShadowStartUnit + spotShadowSlot);
                        spotShadowSlot++;
                    }
                }

                _spotShadowAtlasPacked0[i] = new IVector4(0, -1, (int)ShadowFallbackMode.Lit, -1);
                if (useSpotAtlas &&
                    TryGetSpotShadowAtlasAllocation(light, out ShadowAtlasAllocation allocation, out int recordIndex))
                {
                    ShadowFallbackMode fallback = allocation.ActiveFallback != ShadowFallbackMode.None
                        ? allocation.ActiveFallback
                        : ShadowFallbackMode.Lit;
                    bool atlasResident = allocation.IsResident &&
                        allocation.LastRenderedFrame != 0u &&
                        allocation.PageIndex >= 0 &&
                        allocation.PageIndex < maxForwardSpotAtlasPages;
                    float atlasNearPlane = light.ShadowCamera?.NearZ ?? 0.1f;
                    float atlasFarPlane = light.ShadowCamera?.FarZ ?? MathF.Max(atlasNearPlane + 0.001f, light.Distance);
                    float texelSize = allocation.Resolution > 0u ? 1.0f / allocation.Resolution : 0.0f;

                    _spotShadowAtlasPacked0[i] = new IVector4(atlasResident ? 1 : 0, allocation.PageIndex, (int)fallback, recordIndex);
                    _spotShadowAtlasParams0[i] = allocation.UvScaleBias;
                    _spotShadowAtlasParams1[i] = new Vector4(atlasNearPlane, atlasFarPlane, texelSize, 0.0f);
                }

                _spotShadowPacked0[i] = new IVector4(shadowSlot, light.FilterSamples, light.BlockerSamples, light.VogelTapCount);
                _spotShadowPacked1[i] = new IVector4((int)light.SoftShadowMode, light.ShadowDebugMode, light.EnableContactShadows ? 1 : 0, light.ContactShadowSamples);
                _spotShadowParams0[i] = new Vector4(light.ShadowExponentBase, light.ShadowExponent, light.ShadowMinBias, light.ShadowMaxBias);
                _spotShadowParams1[i] = new Vector4(light.FilterRadius, light.BlockerSearchRadius, light.MinPenumbra, light.MaxPenumbra);
                _spotShadowParams2[i] = new Vector4(light.EffectiveLightSourceRadius, light.ContactShadowDistance, light.ContactShadowThickness, light.ContactShadowFadeStart);
                _spotShadowParams3[i] = new Vector4(light.ContactShadowFadeEnd, light.ContactShadowNormalOffset, light.ContactShadowJitterStrength, 0.0f);

                ShadowMapFormatSelection shadowFormat = light.ResolveShadowMapFormat(preferredStorageFormat: light.ShadowMapStorageFormat);
                float momentNearPlane = light.ShadowCamera?.NearZ ?? light.ShadowNearPlaneDistance;
                float momentFarPlane = light.ShadowCamera?.FarZ ?? MathF.Max(momentNearPlane + 0.001f, light.Distance);
                _spotShadowPacked2[i] = new IVector4((int)shadowFormat.Encoding, light.ShadowMomentUseMipmaps ? 1 : 0, 0, 0);
                _spotShadowParams4[i] = new Vector4(light.ShadowMomentMinVariance, light.ShadowMomentLightBleedReduction, shadowFormat.PositiveExponent, shadowFormat.NegativeExponent);
                _spotShadowParams5[i] = new Vector4(momentNearPlane, momentFarPlane, light.ShadowMomentMipBias, 0.0f);
            }
            for (; spotShadowSlot < maxForwardShadowedSpotLights; ++spotShadowSlot)
                program.Sampler(_spotShadowMapNames[spotShadowSlot], DummyShadowMap, spotShadowStartUnit + spotShadowSlot);

            Array.Clear(_directionalShadowAtlasPacked0);
            Array.Clear(_directionalShadowAtlasParams0);
            Array.Clear(_directionalShadowAtlasParams1);
            if (useDirectionalShadowAtlas && DynamicDirectionalLights.Count > 0)
            {
                DynamicDirectionalLights[0].CopyPublishedCascadeAtlasUniformData(
                    _directionalShadowAtlasPacked0,
                    _directionalShadowAtlasParams0,
                    _directionalShadowAtlasParams1);
            }

            program.Uniform("PointLightShadowPacked0", _pointShadowPacked0);
            program.Uniform("PointLightShadowPacked1", _pointShadowPacked1);
            program.Uniform("PointLightShadowParams0", _pointShadowParams0);
            program.Uniform("PointLightShadowParams1", _pointShadowParams1);
            program.Uniform("PointLightShadowParams2", _pointShadowParams2);
            program.Uniform("PointLightShadowParams3", _pointShadowParams3);
            program.Uniform("PointLightShadowParams4", _pointShadowParams4);

            program.Uniform("SpotLightShadowPacked0", _spotShadowPacked0);
            program.Uniform("SpotLightShadowPacked1", _spotShadowPacked1);
            program.Uniform("SpotLightShadowParams0", _spotShadowParams0);
            program.Uniform("SpotLightShadowParams1", _spotShadowParams1);
            program.Uniform("SpotLightShadowParams2", _spotShadowParams2);
            program.Uniform("SpotLightShadowParams3", _spotShadowParams3);
            program.Uniform("SpotLightShadowPacked2", _spotShadowPacked2);
            program.Uniform("SpotLightShadowParams4", _spotShadowParams4);
            program.Uniform("SpotLightShadowParams5", _spotShadowParams5);
            program.Uniform("SpotLightShadowAtlasPacked0", _spotShadowAtlasPacked0);
            program.Uniform("SpotLightShadowAtlasParams0", _spotShadowAtlasParams0);
            program.Uniform("SpotLightShadowAtlasParams1", _spotShadowAtlasParams1);
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
