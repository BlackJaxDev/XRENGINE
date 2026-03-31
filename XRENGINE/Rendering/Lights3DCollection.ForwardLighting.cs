using System.Linq;
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
            program.Uniform(EEngineUniform.ScreenWidth.ToString(), (float)area.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToString(), (float)area.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToString(), new Vector2(area.X, area.Y));

            program.Uniform("DirLightCount", DynamicDirectionalLights.Count);
            program.Uniform("PointLightCount", DynamicPointLights.Count);
            program.Uniform("SpotLightCount", DynamicSpotLights.Count);

            // Forward+ bindings (optional). Shaders may ignore these if they don't declare Forward+ support.
            program.Uniform("ForwardPlusEnabled", Engine.Rendering.State.ForwardPlusEnabled);
            if (Engine.Rendering.State.ForwardPlusEnabled)
            {
                program.Uniform("ForwardPlusScreenSize", Engine.Rendering.State.ForwardPlusScreenSize);
                program.Uniform("ForwardPlusTileSize", Engine.Rendering.State.ForwardPlusTileSize);
                program.Uniform("ForwardPlusMaxLightsPerTile", Engine.Rendering.State.ForwardPlusMaxLightsPerTile);

                // Keep bindings in sync with the compute shader: 20 (local lights), 21 (visible indices).
                program.BindBuffer(Engine.Rendering.State.ForwardPlusLocalLightsBuffer!, 20u);
                program.BindBuffer(Engine.Rendering.State.ForwardPlusVisibleIndicesBuffer!, 21u);
            }
            program.Uniform("ForwardPlusEyeCount", Engine.Rendering.State.IsStereoPass ? 2 : 1);

            // Support both legacy uniform names (DirLightData/PointLightData/SpotLightData)
            // and dynamically generated forward shader names (DirectionalLights/PointLights/SpotLights).
            // NOTE: We intentionally do not rely on program.HasUniform(...) here because these bindings are
            // part of the renderer's required forward-lighting contract rather than optional material features.
            const string dirArrayGenerated = "DirectionalLights";
            const string spotArrayGenerated = "SpotLights";
            const string pointArrayGenerated = "PointLights";

            const string dirArrayLegacy = "DirLightData";
            const string spotArrayLegacy = "SpotLightData";
            const string pointArrayLegacy = "PointLightData";

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
                program.Uniform("ShadowFilterRadius", firstDirLight.FilterRadius);
                program.Uniform("EnablePCSS", firstDirLight.EnablePCSS);
                program.Uniform("EnableCascadedShadows", firstDirLight.EnableCascadedShadows);
                program.Uniform("EnableContactShadows", firstDirLight.EnableContactShadows);
                program.Uniform("ContactShadowDistance", firstDirLight.ContactShadowDistance);
                program.Uniform("ContactShadowSamples", firstDirLight.ContactShadowSamples);

                if (firstDirLight.CastsShadows && firstDirLight.ShadowMap?.Material?.Textures.Count > 0)
                {
                    forwardShadowTex = firstDirLight.ShadowMap.Material.Textures[0];
                    if (firstDirLight is OneViewLightComponent oneView && oneView.ShadowCamera is not null)
                    {
                        primaryDirLightWorldToLightInvView = oneView.ShadowCamera.Transform.RenderMatrix;
                        primaryDirLightWorldToLightProj = oneView.ShadowCamera.ProjectionMatrix;
                    }

                    var cameraComponent = currentPipeline?.RenderState.WindowViewport?.CameraComponent;
                    useCascadedDirectionalShadows =
                        cameraComponent?.DirectionalShadowRenderingMode == EDirectionalShadowRenderingMode.Cascaded &&
                        firstDirLight.EnableCascadedShadows &&
                        firstDirLight.CascadedShadowMapTexture is not null &&
                        firstDirLight.ActiveCascadeCount > 0;

                    if (useCascadedDirectionalShadows)
                        forwardCascadeShadowTex = firstDirLight.CascadedShadowMapTexture;
                }
                else
                {
                    /*
                    // Debug: log why shadow map isn't available
                    string reason = !firstDirLight.CastsShadows ? "CastsShadows=false" :
                                    firstDirLight.ShadowMap is null ? "ShadowMap=null" :
                                    firstDirLight.ShadowMap.Material is null ? "ShadowMap.Material=null" :
                                    $"Textures.Count={firstDirLight.ShadowMap.Material.Textures.Count}";
                    Debug.Out($"[ForwardShadow] No shadow tex: {reason}");
                    */
                }
            }
            bool shadowEnabled = forwardShadowTex != null;
            program.Uniform("ShadowMapEnabled", shadowEnabled);
            program.Uniform("UseCascadedDirectionalShadows", useCascadedDirectionalShadows);
            program.Uniform("PrimaryDirLightWorldToLightInvViewMatrix", primaryDirLightWorldToLightInvView);
            program.Uniform("PrimaryDirLightWorldToLightProjMatrix", primaryDirLightWorldToLightProj);
            if (!_loggedShadowMapEnabledOnce)
            {
                _loggedShadowMapEnabledOnce = true;
                Debug.Out($"[ForwardShadow] ShadowMapEnabled={shadowEnabled}, forwardShadowTex={forwardShadowTex?.GetType().Name ?? "null"}");
            }

            // ALWAYS set the ShadowMap sampler to point to unit 15, even when shadows are disabled.
            // This prevents stale state from deferred passes (which use unit 4) from leaking through.
            // The shader's layout(binding=15) should handle this, but we force it to be safe against
            // cached shader binaries that might not have the layout qualifier.
            program.Uniform("ShadowMap", forwardShadowMapUnit);
            program.Uniform("ShadowMapArray", forwardShadowMapArrayUnit);

            for (int i = 0; i < DynamicDirectionalLights.Count; ++i)
            {
                DynamicDirectionalLights[i].SetUniforms(program, $"{dirArrayGenerated}[{i}]");
                DynamicDirectionalLights[i].SetUniforms(program, $"{dirArrayLegacy}[{i}]");
            }
            for (int i = 0; i < DynamicSpotLights.Count; ++i)
            {
                DynamicSpotLights[i].SetUniforms(program, $"{spotArrayGenerated}[{i}]");
                DynamicSpotLights[i].SetUniforms(program, $"{spotArrayLegacy}[{i}]");
            }
            for (int i = 0; i < DynamicPointLights.Count; ++i)
            {
                DynamicPointLights[i].SetUniforms(program, $"{pointArrayGenerated}[{i}]");
                DynamicPointLights[i].SetUniforms(program, $"{pointArrayLegacy}[{i}]");
            }

            int[] pointShadowSlots = Enumerable.Repeat(-1, 16).ToArray();
            float[] pointShadowNearPlanes = new float[16];
            float[] pointShadowFarPlanes = new float[16];
            float[] pointShadowBase = new float[16];
            float[] pointShadowExponent = new float[16];
            float[] pointShadowBiasMin = new float[16];
            float[] pointShadowBiasMax = new float[16];
            int[] pointShadowSamples = new int[16];
            float[] pointShadowFilterRadius = new float[16];
            bool[] pointShadowEnablePCSS = new bool[16];
            int[] pointShadowDebugModes = new int[16];
            int pointShadowSlot = 0;
            for (int i = 0; i < DynamicPointLights.Count && i < pointShadowSlots.Length; ++i)
            {
                PointLightComponent light = DynamicPointLights[i];
                pointShadowNearPlanes[i] = light.ShadowNearPlaneDistance;
                pointShadowFarPlanes[i] = light.Radius;
                pointShadowBase[i] = light.ShadowExponentBase;
                pointShadowExponent[i] = light.ShadowExponent;
                pointShadowBiasMin[i] = light.ShadowMinBias;
                pointShadowBiasMax[i] = light.ShadowMaxBias;
                pointShadowSamples[i] = light.Samples;
                pointShadowFilterRadius[i] = light.FilterRadius;
                pointShadowEnablePCSS[i] = light.EnablePCSS;
                pointShadowDebugModes[i] = light.ShadowDebugMode;

                XRTexture? shadowTexture = light.CastsShadows
                    ? light.ShadowMap?.Material?.Textures.FirstOrDefault(x => x?.SamplerName == "ShadowMap")
                    : null;
                if (shadowTexture is XRTextureCube shadowCube && pointShadowSlot < maxForwardShadowedPointLights)
                {
                    pointShadowSlots[i] = pointShadowSlot;
                    program.Sampler($"PointLightShadowMaps[{pointShadowSlot}]", shadowCube, pointShadowStartUnit + pointShadowSlot);
                    pointShadowSlot++;
                }
            }
            for (; pointShadowSlot < maxForwardShadowedPointLights; ++pointShadowSlot)
                program.Sampler($"PointLightShadowMaps[{pointShadowSlot}]", DummyPointShadowMap, pointShadowStartUnit + pointShadowSlot);

            int[] spotShadowSlots = Enumerable.Repeat(-1, 16).ToArray();
            float[] spotShadowBase = new float[16];
            float[] spotShadowExponent = new float[16];
            float[] spotShadowBiasMin = new float[16];
            float[] spotShadowBiasMax = new float[16];
            int[] spotShadowSamples = new int[16];
            float[] spotShadowFilterRadius = new float[16];
            bool[] spotShadowEnablePCSS = new bool[16];
            int[] spotShadowDebugModes = new int[16];
            int spotShadowSlot = 0;
            for (int i = 0; i < DynamicSpotLights.Count && i < spotShadowSlots.Length; ++i)
            {
                SpotLightComponent light = DynamicSpotLights[i];
                spotShadowBase[i] = light.ShadowExponentBase;
                spotShadowExponent[i] = light.ShadowExponent;
                spotShadowBiasMin[i] = light.ShadowMinBias;
                spotShadowBiasMax[i] = light.ShadowMaxBias;
                spotShadowSamples[i] = light.Samples;
                spotShadowFilterRadius[i] = light.FilterRadius;
                spotShadowEnablePCSS[i] = light.EnablePCSS;
                spotShadowDebugModes[i] = light.ShadowDebugMode;

                XRTexture? shadowTexture = light.CastsShadows
                    ? light.ShadowMap?.Material?.Textures.FirstOrDefault(x => x?.SamplerName == "ShadowMap")
                    : null;
                if (shadowTexture is XRTexture2D shadowMap && spotShadowSlot < maxForwardShadowedSpotLights)
                {
                    spotShadowSlots[i] = spotShadowSlot;
                    program.Sampler($"SpotLightShadowMaps[{spotShadowSlot}]", shadowMap, spotShadowStartUnit + spotShadowSlot);
                    spotShadowSlot++;
                }
            }
            for (; spotShadowSlot < maxForwardShadowedSpotLights; ++spotShadowSlot)
                program.Sampler($"SpotLightShadowMaps[{spotShadowSlot}]", DummyShadowMap, spotShadowStartUnit + spotShadowSlot);

            program.Uniform("PointLightShadowSlots", pointShadowSlots);
            program.Uniform("PointLightShadowNearPlanes", pointShadowNearPlanes);
            program.Uniform("PointLightShadowFarPlanes", pointShadowFarPlanes);
            program.Uniform("PointLightShadowBase", pointShadowBase);
            program.Uniform("PointLightShadowExponent", pointShadowExponent);
            program.Uniform("PointLightShadowBiasMin", pointShadowBiasMin);
            program.Uniform("PointLightShadowBiasMax", pointShadowBiasMax);
            program.Uniform("PointLightShadowSamples", pointShadowSamples);
            program.Uniform("PointLightShadowFilterRadius", pointShadowFilterRadius);
            program.Uniform("PointLightShadowEnablePCSS", pointShadowEnablePCSS);
            program.Uniform("PointLightShadowDebugModes", pointShadowDebugModes);

            program.Uniform("SpotLightShadowSlots", spotShadowSlots);
            program.Uniform("SpotLightShadowBase", spotShadowBase);
            program.Uniform("SpotLightShadowExponent", spotShadowExponent);
            program.Uniform("SpotLightShadowBiasMin", spotShadowBiasMin);
            program.Uniform("SpotLightShadowBiasMax", spotShadowBiasMax);
            program.Uniform("SpotLightShadowSamples", spotShadowSamples);
            program.Uniform("SpotLightShadowFilterRadius", spotShadowFilterRadius);
            program.Uniform("SpotLightShadowEnablePCSS", spotShadowEnablePCSS);
            program.Uniform("SpotLightShadowDebugModes", spotShadowDebugModes);

            // Bind the actual shadow texture after per-light SetUniforms.
            // ALWAYS bind a texture to unit 15 - if no shadow map, use a 1x1 white dummy.
            // This prevents OpenGL from sampling stale texture state.
            program.Sampler("ShadowMap", forwardShadowTex ?? DummyShadowMap, forwardShadowMapUnit);
            program.Sampler("ShadowMapArray", forwardCascadeShadowTex ?? DummyShadowMapArray, forwardShadowMapArrayUnit);

            // Bind environment/reflection cubemap to dedicated high units.
            // The uber PBR shader (pbr.glsl) declares samplerCube _PBRReflCube and u_EnvironmentMap.
            // Without a valid cubemap bound, these samplers default to unit 0 (which has a 2D texture),
            // causing GL_INVALID_OPERATION "program texture usage" on every draw call.
            const int envMapUnit = 12;
            const int reflCubeUnit = 13;

            // Try to use the nearest light probe's environment cubemap if available.
            XRTexture? envCubemap = null;
            float envMipLevels = 1.0f;
            if (LightProbes.Count > 0)
            {
                var probe = LightProbes[0];
                envCubemap = probe.EnvironmentTextureCubemap;
                if (envCubemap is XRTextureCube envCube && envCube.Mipmaps is { Length: > 0 })
                    envMipLevels = envCube.Mipmaps.Length;
            }
            envCubemap ??= DummyEnvironmentCubemap;

            program.Sampler("u_EnvironmentMap", envCubemap, envMapUnit);
            program.Uniform("u_EnvironmentMapMipLevels", envMipLevels);
            program.Sampler("_PBRReflCube", envCubemap, reflCubeUnit);
        }

        #endregion
    }
}
