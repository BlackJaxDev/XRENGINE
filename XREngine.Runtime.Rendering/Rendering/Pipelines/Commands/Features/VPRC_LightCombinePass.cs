using XREngine.Data.Rendering;
using XREngine.Data.Geometry;
using XREngine.Data.Colors;
using XREngine.Data.Vectors;
using System.Numerics;
using XREngine.Rendering.Models.Materials;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Scene;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_LightCombinePass : ViewportRenderCommand
    {
        private const string MsaaDeferredDefine = "XRENGINE_MSAA_DEFERRED";
        private const string StereoDeferredDefine = "XRENGINE_STEREO_DEFERRED";

        /// <summary>
        /// A 1x1x1 dummy depth texture array bound to the ShadowMapArray sampler
        /// when cascaded shadows are not active, preventing GL_INVALID_OPERATION
        /// from sampler type mismatch on the default texture unit.
        /// </summary>
        private static XRTexture2DArray? _dummyShadowMapArray;
        private static XRTexture2DArray DummyShadowMapArray => _dummyShadowMapArray ??= CreateDummyShadowMapArray();

        private static XRTexture2DArray CreateDummyShadowMapArray()
        {
            XRTexture2DArray texture = new(
                1,
                1,
                1,
                EPixelInternalFormat.DepthComponent16,
                EPixelFormat.DepthComponent,
                EPixelType.Float,
                allocateData: true)
            {
                Name = "DummyShadowMapArray",
                SamplerName = "ShadowMapArray",
                MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                AutoGenerateMipmaps = false,
            };

            return texture;
        }

        private static XRTexture2D? _dummyShadowMap;
        private static XRTexture2D DummyShadowMap => _dummyShadowMap ??= new XRTexture2D(1, 1, ColorF4.White);
        private const string DirectionalShadowAtlasName = "DirectionalShadowAtlas";
        private const int DirectionalShadowAtlasUnit = 9;
        private const string PointShadowAtlasName = "PointShadowAtlas";
        private const string SpotShadowAtlasName = "SpotShadowAtlas";
        private readonly ConditionalWeakTable<XRRenderPipelineInstance, LightRendererCache> _rendererCaches = new();

        public string AlbedoOpacityTexture { get; set; } = "AlbedoOpacityTexture";
        public string NormalTexture { get; set; } = "NormalTexture";
        public string RMSETexture { get; set; } = "RMSETexture";
        public string DepthViewTexture { get; set; } = "DepthViewTexture";

        /// <summary>
        /// When true, the pass will perform two-pass rendering per light:
        /// simple pixels (sample 0) then complex pixels (per-sample via GL_SAMPLE_SHADING).
        /// Set by the pipeline based on whether MSAA deferred is active.
        /// </summary>
        public bool MsaaDeferred { get; set; }

        /// <summary>
        /// MSAA GBuffer texture names for the complex pixel per-sample pass.
        /// </summary>
        public string MsaaAlbedoOpacityTexture { get; set; } = DefaultRenderPipeline.MsaaAlbedoOpacityTextureName;
        public string MsaaNormalTexture { get; set; } = DefaultRenderPipeline.MsaaNormalTextureName;
        public string MsaaRMSETexture { get; set; } = DefaultRenderPipeline.MsaaRMSETextureName;
        public string MsaaDepthViewTexture { get; set; } = DefaultRenderPipeline.MsaaDepthViewTextureName;

        public void SetOptions(string albedoOpacityTexture, string normalTexture, string rmseTexture, string depthViewTexture)
        {
            AlbedoOpacityTexture = albedoOpacityTexture;
            NormalTexture = normalTexture;
            RMSETexture = rmseTexture;
            DepthViewTexture = depthViewTexture;
        }

        private sealed class LightRendererCache
        {
            public XRTexture? AlbedoOpacityTexture;
            public XRTexture? NormalTexture;
            public XRTexture? RMSETexture;
            public XRTexture? DepthViewTexture;

            public XRTexture? MsaaAlbedoOpacityTexture;
            public XRTexture? MsaaNormalTexture;
            public XRTexture? MsaaRMSETexture;
            public XRTexture? MsaaDepthViewTexture;

            public XRMeshRenderer? PointLightRenderer;
            public XRMeshRenderer? SpotLightRenderer;
            public XRMeshRenderer? DirectionalLightRenderer;

            public XRMeshRenderer? MsaaSimplePointLightRenderer;
            public XRMeshRenderer? MsaaSimpleSpotLightRenderer;
            public XRMeshRenderer? MsaaSimpleDirectionalLightRenderer;

            public XRMeshRenderer? MsaaComplexPointLightRenderer;
            public XRMeshRenderer? MsaaComplexSpotLightRenderer;
            public XRMeshRenderer? MsaaComplexDirectionalLightRenderer;

            public readonly IVector4[] DirectionalShadowAtlasPacked0 = new IVector4[8];
            public readonly Vector4[] DirectionalShadowAtlasUvScaleBias = new Vector4[8];
            public readonly Vector4[] DirectionalShadowAtlasDepthParams = new Vector4[8];
            public readonly IVector4[] PointShadowAtlasPacked0 = new IVector4[PointLightComponent.ShadowFaceCount];
            public readonly Vector4[] PointShadowAtlasUvScaleBias = new Vector4[PointLightComponent.ShadowFaceCount];
            public readonly Vector4[] PointShadowAtlasDepthParams = new Vector4[PointLightComponent.ShadowFaceCount];
        }

        private LightRendererCache? _activeRendererCache;

        public XRMeshRenderer? PointLightRenderer => _activeRendererCache?.PointLightRenderer;
        public XRMeshRenderer? SpotLightRenderer => _activeRendererCache?.SpotLightRenderer;
        public XRMeshRenderer? DirectionalLightRenderer => _activeRendererCache?.DirectionalLightRenderer;

        // MSAA deferred uses two lighting phases:
        // simple pixels use the resolved GBuffer once, complex pixels use the MSAA GBuffer per-sample.
        public XRMeshRenderer? MsaaSimplePointLightRenderer => _activeRendererCache?.MsaaSimplePointLightRenderer;
        public XRMeshRenderer? MsaaSimpleSpotLightRenderer => _activeRendererCache?.MsaaSimpleSpotLightRenderer;
        public XRMeshRenderer? MsaaSimpleDirectionalLightRenderer => _activeRendererCache?.MsaaSimpleDirectionalLightRenderer;

        public XRMeshRenderer? MsaaComplexPointLightRenderer => _activeRendererCache?.MsaaComplexPointLightRenderer;
        public XRMeshRenderer? MsaaComplexSpotLightRenderer => _activeRendererCache?.MsaaComplexSpotLightRenderer;
        public XRMeshRenderer? MsaaComplexDirectionalLightRenderer => _activeRendererCache?.MsaaComplexDirectionalLightRenderer;

        protected override void Execute()
        {
            if (ActivePipelineInstance.RenderState.Scene is null)
                return;

            int passIndex = ResolvePassIndex(nameof(VPRC_LightCombinePass), out bool hasRenderGraphMetadata);
            if (passIndex == int.MinValue && hasRenderGraphMetadata)
            {
                Debug.RenderingWarningEvery(
                    $"LightCombine.MissingRenderGraphPass.{nameof(VPRC_LightCombinePass)}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDiag] Skipping light combine: no matching render-graph pass metadata was generated for '{0}'.",
                    nameof(VPRC_LightCombinePass));
                return;
            }

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            var world = viewport?.World;

            var albOpacTex = ActivePipelineInstance.GetTexture<XRTexture>(AlbedoOpacityTexture);
            var normTex = ActivePipelineInstance.GetTexture<XRTexture>(NormalTexture);
            var rmseTex = ActivePipelineInstance.GetTexture<XRTexture>(RMSETexture);
            var depthViewTex = ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTexture);
            if (albOpacTex is null || normTex is null || rmseTex is null || depthViewTex is null)
            {
                string missingTextures = string.Empty;

                static void AppendMissing(ref string missing, string name)
                    => missing = string.IsNullOrEmpty(missing) ? name : $"{missing}, {name}";

                if (albOpacTex is null)
                    AppendMissing(ref missingTextures, AlbedoOpacityTexture);
                if (normTex is null)
                    AppendMissing(ref missingTextures, NormalTexture);
                if (rmseTex is null)
                    AppendMissing(ref missingTextures, RMSETexture);
                if (depthViewTex is null)
                    AppendMissing(ref missingTextures, DepthViewTexture);

                BoundingRectangle missingRegion = ActivePipelineInstance.RenderState.CurrentRenderRegion;
                Debug.LightingWarningEvery(
                    $"RenderDiag.LightCombine.MissingTextures.{ActivePipelineInstance.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] LightCombine skipped: missing [{0}]. VP={1} World={2} Play={3} Camera={4} Region={5}x{6} Pipeline={7} Generation={8} Albedo={9} Normal={10} RMSE={11} Depth={12}",
                    missingTextures,
                    viewport?.Index ?? -1,
                    world?.TargetWorldName ?? "<null>",
                    RuntimeEngine.PlayMode.State,
                    viewport?.CameraComponent?.Name ?? "<null>",
                    missingRegion.Width,
                    missingRegion.Height,
                    ActivePipelineInstance.Pipeline?.DebugName ?? "<null>",
                    ActivePipelineInstance.ResourceGeneration,
                    DescribeTexture(albOpacTex),
                    DescribeTexture(normTex),
                    DescribeTexture(rmseTex),
                    DescribeTexture(depthViewTex));
                return;
            }

            LightRendererCache rendererCache = GetRendererCache();
            if (rendererCache.AlbedoOpacityTexture != albOpacTex ||
                rendererCache.NormalTexture != normTex ||
                rendererCache.RMSETexture != rmseTex ||
                rendererCache.DepthViewTexture != depthViewTex)
            {
                rendererCache.AlbedoOpacityTexture = albOpacTex;
                rendererCache.NormalTexture = normTex;
                rendererCache.RMSETexture = rmseTex;
                rendererCache.DepthViewTexture = depthViewTex;
                CreateLightRenderers(rendererCache, albOpacTex, normTex, rmseTex, depthViewTex);
            }

            // Build MSAA renderers when MSAA deferred is active
            bool msaaActive = MsaaDeferred && DefaultRenderPipeline.RuntimeEnableMsaaDeferred;
            if (msaaActive && !EnsureMsaaRenderers(rendererCache))
                msaaActive = false;

            var lights = world?.Lights;
            if (lights is null)
                return;

            BoundingRectangle region = ActivePipelineInstance.RenderState.CurrentRenderRegion;
            int directionalLightCount = lights.DynamicDirectionalLights.Count;
            int pointLightCount = lights.DynamicPointLights.Count;
            int spotLightCount = lights.DynamicSpotLights.Count;
            if (DeferredLightingDiagnostics.Enabled)
            {
                var targetBinding = ActivePipelineInstance.RenderState.CurrentRenderTargetBinding;
                Debug.VulkanEvery(
                    $"DeferredLighting.LightCombinePass.{ActivePipelineInstance.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[DeferredLightingDiag][LightCombinePass] pass={0} hasMetadata={1} target='{2}' write={3} msaa={4} lights(D/P/S)={5}/{6}/{7} region={8}x{9} gbuffer={10} | {11} | {12} | {13}",
                    passIndex,
                    hasRenderGraphMetadata,
                    targetBinding?.Name ?? "<none>",
                    targetBinding?.Write ?? false,
                    msaaActive,
                    directionalLightCount,
                    pointLightCount,
                    spotLightCount,
                    region.Width,
                    region.Height,
                    DescribeTexture(albOpacTex),
                    DescribeTexture(normTex),
                    DescribeTexture(rmseTex),
                    DescribeTexture(depthViewTex));
            }

/*
            Debug.LightingEvery(
                $"RenderDiag.LightCombine.{ActivePipelineInstance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] LightCombine: VP={0} World={1} Play={2} Region={3}x{4} Msaa={5} Lights(D/P/S)={6}/{7}/{8} GBuffer={9} | {10} | {11} | {12}",
                viewport?.Index ?? -1,
                world?.TargetWorld?.Name ?? "<null>",
                world?.PlayState.ToString() ?? "<null>",
                region.Width,
                region.Height,
                msaaActive,
                directionalLightCount,
                pointLightCount,
                spotLightCount,
                DescribeTexture(albOpacTex),
                DescribeTexture(normTex),
                DescribeTexture(rmseTex),
                DescribeTexture(depthViewTex));


            if (viewport is { Width: > 512, Height: > 512 } && (region.Width <= 128 || region.Height <= 128))
            {
                Debug.LightingWarningEvery(
                    $"RenderDiag.LightCombine.SmallRegion.{ActivePipelineInstance.GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] LightCombine is running with unexpectedly small region {0}x{1} while viewport is {2}x{3}. World={4} VP={5}",
                    region.Width,
                    region.Height,
                    viewport.Width,
                    viewport.Height,
                    world?.TargetWorld?.Name ?? "<null>",
                    viewport.Index);
            }

*/

            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            using (ActivePipelineInstance.RenderState.PushRenderingCamera(ResolveActiveRenderingCamera()))
            {
                if (msaaActive)
                    RenderLightsMsaaDeferred(lights, rendererCache);
                else
                    RenderLightsStandard(lights, rendererCache);
            }
        }

        private LightRendererCache GetRendererCache()
        {
            LightRendererCache cache = _rendererCaches.GetValue(
                ActivePipelineInstance,
                static _ => new LightRendererCache());
            _activeRendererCache = cache;
            return cache;
        }

        private void RenderLightsStandard(Lights3DCollection lights, LightRendererCache rendererCache)
        {
            // Index-based iteration avoids EventList ThreadSafe snapshot allocation.
            for (int i = 0; i < lights.DynamicPointLights.Count; i++)
                RenderLight(rendererCache.PointLightRenderer!, lights.DynamicPointLights[i]);
            for (int i = 0; i < lights.DynamicSpotLights.Count; i++)
                RenderLight(rendererCache.SpotLightRenderer!, lights.DynamicSpotLights[i]);
            for (int i = 0; i < lights.DynamicDirectionalLights.Count; i++)
                RenderLight(rendererCache.DirectionalLightRenderer!, lights.DynamicDirectionalLights[i]);
        }

        private void RenderLightsMsaaDeferred(Lights3DCollection lights, LightRendererCache rendererCache)
        {
            // Index-based iteration avoids EventList ThreadSafe snapshot allocation.
            RuntimeEngine.Rendering.State.DisableSampleShading();
            for (int i = 0; i < lights.DynamicPointLights.Count; i++)
                RenderLight(rendererCache.MsaaSimplePointLightRenderer!, lights.DynamicPointLights[i]);
            for (int i = 0; i < lights.DynamicSpotLights.Count; i++)
                RenderLight(rendererCache.MsaaSimpleSpotLightRenderer!, lights.DynamicSpotLights[i]);
            for (int i = 0; i < lights.DynamicDirectionalLights.Count; i++)
                RenderLight(rendererCache.MsaaSimpleDirectionalLightRenderer!, lights.DynamicDirectionalLights[i]);

            RuntimeEngine.Rendering.State.EnableSampleShading(1.0f);
            try
            {
                for (int i = 0; i < lights.DynamicPointLights.Count; i++)
                    RenderLight(rendererCache.MsaaComplexPointLightRenderer!, lights.DynamicPointLights[i]);
                for (int i = 0; i < lights.DynamicSpotLights.Count; i++)
                    RenderLight(rendererCache.MsaaComplexSpotLightRenderer!, lights.DynamicSpotLights[i]);
                for (int i = 0; i < lights.DynamicDirectionalLights.Count; i++)
                    RenderLight(rendererCache.MsaaComplexDirectionalLightRenderer!, lights.DynamicDirectionalLights[i]);
            }
            finally
            {
                RuntimeEngine.Rendering.State.DisableSampleShading();
            }
        }

        private void RenderPointLight(PointLightComponent c)
            => RenderLight(PointLightRenderer!, c);
        private void RenderSpotLight(SpotLightComponent c)
            => RenderLight(SpotLightRenderer!, c);

        private LightComponent? _currentLightComponent;

        private static XRCamera? ResolveActiveRenderingCamera()
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            return instance.RenderState.SceneCamera
                ?? instance.RenderState.RenderingCamera
                ?? instance.LastSceneCamera
                ?? instance.LastRenderingCamera
                ?? RuntimeEngine.Rendering.State.RenderingCamera;
        }

        private void RenderLight(XRMeshRenderer renderer, LightComponent comp)
        {
            _currentLightComponent = comp;
            try
            {
                ConfigureLightVolumeCullMode(renderer, comp);

                if (comp is DirectionalLightComponent)
                    renderer.Render(Matrix4x4.Identity, Matrix4x4.Identity, null, forceNoStereo: true);
                else
                    renderer.Render(comp.LightMeshMatrix, comp.LightMeshMatrix, null);
            }
            finally
            {
                _currentLightComponent = null;
            }
        }

        private void ConfigureLightVolumeCullMode(XRMeshRenderer renderer, LightComponent comp)
        {
            if (renderer.Material?.RenderOptions is not { } renderOptions)
                return;

            if (comp is DirectionalLightComponent)
            {
                renderOptions.CullMode = ECullMode.None;
                return;
            }

            if (comp is not SpotLightComponent spotLight)
            {
                renderOptions.CullMode = ECullMode.Front;
                return;
            }

            Vector3? cameraPosition = ResolveActiveRenderingCamera()?.Transform?.RenderTranslation;
            renderOptions.CullMode = cameraPosition is Vector3 position && !spotLight.OuterCone.ContainsPoint(position)
                ? ECullMode.Back
                : ECullMode.Front;
        }

        private void LightManager_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => BindCurrentLightUniforms(materialProgram);

        private void MsaaLightManager_SettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => BindCurrentLightUniforms(materialProgram);

        private void BindCurrentLightUniforms(XRRenderProgram materialProgram)
        {
            if (_currentLightComponent is null)
                return;

            LightRendererCache uniformCache = GetRendererCache();
            _currentLightComponent.SetUniforms(materialProgram);
            int deferredDebugMode = RenderDiagnosticsFlags.DeferredDebugView;
            materialProgram.Uniform("DeferredDebugMode", deferredDebugMode >= 0 && deferredDebugMode <= 18 ? deferredDebugMode : 0);
            materialProgram.Uniform("DirectionalShadowAtlasMaxStaleFrames", (float)RuntimeEngine.Rendering.Settings.MaxDirectionalCascadeAtlasStaleFrames);

            bool useCascadedDirectionalShadows = false;
            bool directionalAtlasSampleable = false;
            bool useDirectionalShadowAtlas = false;
            bool legacyDirectionalCascadeBound = false;
            if (_currentLightComponent is DirectionalLightComponent directionalLight)
            {
                ShadowMapFormatSelection directionalShadowFormat = directionalLight.ResolveShadowMapFormat(preferredStorageFormat: null);
                XRCamera? activeRenderingCamera = ResolveActiveRenderingCamera();
                XRTexture2DArray? directionalCascadeReceiverTexture = directionalLight.GetCascadedShadowReceiverTexture(activeRenderingCamera);
                int activeCascadeCount = directionalLight.GetActiveCascadeCount(activeRenderingCamera);
                useDirectionalShadowAtlas = directionalLight.UsesDirectionalShadowAtlasForCurrentEncoding && directionalLight.CastsShadows;
                bool viewportPrefersCascades = ActiveViewportPrefersCascadedDirectionalShadows(activeRenderingCamera);
                bool cascadeSourceMatchesActiveView = directionalLight.PublishedCascadesMatchCamera(activeRenderingCamera);
                useCascadedDirectionalShadows =
                    viewportPrefersCascades &&
                    cascadeSourceMatchesActiveView &&
                    directionalLight.EnableCascadedShadows &&
                    (useDirectionalShadowAtlas || directionalCascadeReceiverTexture is not null) &&
                    activeCascadeCount > 0;

                materialProgram.Uniform("ShadowMapEncoding", (int)directionalShadowFormat.Encoding);
                materialProgram.Uniform("ShadowMomentParams0", new Vector4(
                    directionalLight.ShadowMomentMinVariance,
                    directionalLight.ShadowMomentLightBleedReduction,
                    directionalShadowFormat.PositiveExponent,
                    directionalShadowFormat.NegativeExponent));
                materialProgram.Uniform("ShadowMomentFilterParams", new Vector4(
                    directionalLight.ShadowMomentBlurRadiusTexels,
                    directionalLight.ShadowMomentBlurPasses,
                    directionalLight.ShadowMomentUseMipmaps ? 1.0f : 0.0f,
                    directionalLight.ShadowMomentMipBias));

                BindDirectionalAtlasShadows(
                    materialProgram,
                    directionalLight,
                    activeRenderingCamera,
                    useCascadedDirectionalShadows,
                    uniformCache.DirectionalShadowAtlasPacked0,
                    uniformCache.DirectionalShadowAtlasUvScaleBias,
                    uniformCache.DirectionalShadowAtlasDepthParams,
                    out directionalAtlasSampleable);

                if (useDirectionalShadowAtlas && !directionalAtlasSampleable)
                    useDirectionalShadowAtlas = false;

                if (useCascadedDirectionalShadows && directionalCascadeReceiverTexture is not null)
                {
                    materialProgram.Sampler("ShadowMapArray", directionalCascadeReceiverTexture, 5);
                    legacyDirectionalCascadeBound = true;
                }
                else
                {
                    materialProgram.Sampler("ShadowMapArray", DummyShadowMapArray, 5);
                }
            }
            else
            {
                BindDisabledDirectionalAtlasShadows(
                    materialProgram,
                    uniformCache.DirectionalShadowAtlasPacked0,
                    uniformCache.DirectionalShadowAtlasUvScaleBias,
                    uniformCache.DirectionalShadowAtlasDepthParams);
            }

            materialProgram.Uniform("UseCascadedDirectionalShadows", useCascadedDirectionalShadows);

            // Bind shadow map for deferred rendering at unit 4 (deferred shaders expect it there).
            // This is done here rather than in SetUniforms to avoid overwriting material texture units
            // during forward rendering.
            XRTexture? selectedShadowMap = null;
            if (_currentLightComponent is DirectionalLightComponent selectedDirectionalLight)
            {
                selectedShadowMap = selectedDirectionalLight.PrimaryShadowReceiverTexture;
                if (selectedShadowMap is not null)
                    materialProgram.Sampler("ShadowMap", selectedShadowMap, 4);
            }
            else if (_currentLightComponent.CastsShadows && _currentLightComponent.ShadowMap?.Material?.Textures is { Count: > 0 } shadowTextures)
            {
                // Prefer the texture with SamplerName="ShadowMap" (the R16f distance cubemap);
                // fall back to the first non-null texture otherwise.
                foreach (var t in shadowTextures)
                {
                    if (t is not null && t.SamplerName == "ShadowMap")
                    {
                        selectedShadowMap = t;
                        break;
                    }
                }
                selectedShadowMap ??= shadowTextures[0];
                if (selectedShadowMap != null)
                    materialProgram.Sampler("ShadowMap", selectedShadowMap, 4);
            }

            bool hasShadowMap = _currentLightComponent.CastsShadows && selectedShadowMap is not null;
            bool directionalHasShadowMap = directionalAtlasSampleable ||
                legacyDirectionalCascadeBound;
            if (_currentLightComponent is DirectionalLightComponent)
                directionalHasShadowMap |= !useDirectionalShadowAtlas && hasShadowMap;

            if (_currentLightComponent is PointLightComponent pointLight)
            {
                bool usePointAtlas = pointLight.UsesPointShadowAtlasForCurrentEncoding;
                bool pointAtlasBound = false;
                bool useLegacyShadowMap = !usePointAtlas && hasShadowMap;
                if (usePointAtlas)
                    pointAtlasBound = TryBindPointAtlasShadow(
                        materialProgram,
                        pointLight,
                        uniformCache.PointShadowAtlasPacked0,
                        uniformCache.PointShadowAtlasUvScaleBias,
                        uniformCache.PointShadowAtlasDepthParams);
                else
                    BindDisabledPointAtlas(
                        materialProgram,
                        uniformCache.PointShadowAtlasPacked0,
                        uniformCache.PointShadowAtlasUvScaleBias,
                        uniformCache.PointShadowAtlasDepthParams);

                ShadowMapFormatSelection shadowFormat = pointLight.ResolveShadowMapFormat(preferredStorageFormat: pointLight.ShadowMapStorageFormat);
                materialProgram.Uniform("ShadowMapEncoding", (int)shadowFormat.Encoding);
                materialProgram.Uniform("ShadowMomentParams0", new Vector4(
                    pointLight.ShadowMomentMinVariance,
                    pointLight.ShadowMomentLightBleedReduction,
                    shadowFormat.PositiveExponent,
                    shadowFormat.NegativeExponent));
                materialProgram.Uniform("ShadowMomentFilterParams", new Vector4(
                    pointLight.ShadowMomentBlurRadiusTexels,
                    pointLight.ShadowMomentBlurPasses,
                    pointLight.ShadowMomentUseMipmaps ? 1.0f : 0.0f,
                    pointLight.ShadowMomentMipBias));

                materialProgram.Uniform("LightHasShadowMap", pointAtlasBound || useLegacyShadowMap);
                materialProgram.Sampler(
                    "ShadowMap",
                    useLegacyShadowMap && selectedShadowMap is XRTextureCube shadowCube
                        ? shadowCube
                        : Lights3DCollection.DummyPointShadowMap,
                    4);
            }
            else if (_currentLightComponent is SpotLightComponent spotLight)
            {
                bool useSpotAtlas = spotLight.UsesSpotShadowAtlasForCurrentEncoding;
                bool atlasBound = false;
                ShadowFallbackMode atlasFallback = ShadowFallbackMode.Lit;
                bool useLegacyShadowMap = !useSpotAtlas && hasShadowMap;
                if (useSpotAtlas)
                {
                    atlasBound = TryBindSpotAtlasShadow(materialProgram, spotLight, out atlasFallback);
                    useLegacyShadowMap = false;
                }
                else
                {
                    BindDisabledSpotAtlas(materialProgram);
                }

                ShadowMapFormatSelection shadowFormat = spotLight.ResolveShadowMapFormat(preferredStorageFormat: spotLight.ShadowMapStorageFormat);
                float nearPlane = spotLight.ShadowCamera?.NearZ ?? spotLight.ShadowNearPlaneDistance;
                float farPlane = spotLight.ShadowCamera?.FarZ ?? MathF.Max(nearPlane + 0.001f, spotLight.Distance);
                materialProgram.Uniform("ShadowMapEncoding", (int)shadowFormat.Encoding);
                materialProgram.Uniform("ShadowMomentParams0", new Vector4(
                    spotLight.ShadowMomentMinVariance,
                    spotLight.ShadowMomentLightBleedReduction,
                    shadowFormat.PositiveExponent,
                    shadowFormat.NegativeExponent));
                materialProgram.Uniform("ShadowMomentDepthParams", new Vector4(
                    nearPlane,
                    farPlane,
                    spotLight.ShadowMomentMipBias,
                    spotLight.ShadowMomentUseMipmaps ? 1.0f : 0.0f));
                materialProgram.Uniform("ShadowMomentFilterParams", new Vector4(
                    spotLight.ShadowMomentBlurRadiusTexels,
                    spotLight.ShadowMomentBlurPasses,
                    spotLight.ShadowMomentUseMipmaps ? 1.0f : 0.0f,
                    0.0f));

                materialProgram.Uniform("LightHasShadowMap", atlasBound || useLegacyShadowMap);
                materialProgram.Uniform("SpotShadowAtlasFallbackMode", (int)atlasFallback);
                // Spot lights use a 2D shadow map in legacy mode; rebind with explicit dummy fallback
                // to guarantee a valid sampler2D is always bound at unit 4.
                materialProgram.Sampler("ShadowMap", useLegacyShadowMap && selectedShadowMap is XRTexture2D shadow2D ? shadow2D : DummyShadowMap, 4);
            }
            else if (_currentLightComponent is DirectionalLightComponent)
            {
                materialProgram.Uniform("LightHasShadowMap", directionalHasShadowMap);
                materialProgram.Sampler("ShadowMap", selectedShadowMap is XRTexture2D shadow2D ? shadow2D : DummyShadowMap, 4);
            }
        }

        private bool ActiveViewportPrefersCascadedDirectionalShadows(XRCamera? renderingCamera)
        {
            XRViewport? viewport = ActivePipelineInstance.RenderState.WindowViewport;
            if (viewport is null)
                return false;

            if (viewport.CameraComponent is { } cameraComponent &&
                (renderingCamera is null || ReferenceEquals(cameraComponent.Camera, renderingCamera)))
            {
                return cameraComponent.DirectionalShadowRenderingMode == global::XREngine.Components.EDirectionalShadowRenderingMode.Cascaded;
            }

            if (DirectionalLightComponent.IsHmdEyeCameraForDirectionalCascades(renderingCamera))
                return true;

            return viewport.RendersToExternalSwapchainTarget &&
                viewport.ActiveCamera is not null &&
                (renderingCamera is null || ReferenceEquals(viewport.ActiveCamera, renderingCamera));
        }

        private static bool TryBindSpotAtlasShadow(
            XRRenderProgram materialProgram,
            SpotLightComponent spotLight,
            out ShadowFallbackMode fallback)
        {
            const int spotAtlasUnit = 30;
            fallback = ShadowFallbackMode.Lit;

            var lights = ResolveLightsForLight(spotLight);
            if (lights is null ||
                !lights.TryGetSpotShadowAtlasAllocation(spotLight, out ShadowAtlasAllocation allocation, out int recordIndex))
            {
                BindDisabledSpotAtlas(materialProgram);
                return false;
            }

            fallback = allocation.ActiveFallback != ShadowFallbackMode.None
                ? allocation.ActiveFallback
                : ShadowFallbackMode.Lit;

            ShadowMapFormatSelection shadowFormat = spotLight.ResolveShadowMapFormat(preferredStorageFormat: spotLight.ShadowMapStorageFormat);
            XRTexture2DArray? atlasTexture = null;
            bool resident = IsShadowAtlasAllocationSampleable(allocation) &&
                allocation.Key.Encoding == shadowFormat.Encoding &&
                lights.ShadowAtlas.TryGetPageTexture(allocation.AtlasKind, shadowFormat.Encoding, allocation.PageIndex, out atlasTexture);

            float nearPlane = spotLight.ShadowCamera?.NearZ ?? 0.1f;
            float farPlane = spotLight.ShadowCamera?.FarZ ?? MathF.Max(nearPlane + 0.001f, spotLight.Distance);
            uint sampleResolution = LightComponent.GetShadowAtlasSampleResolution(allocation);
            float texelSize = sampleResolution > 0u ? 1.0f / sampleResolution : 0.0f;
            float resolutionScale = spotLight.GetShadowAtlasResolutionScale(sampleResolution);

            materialProgram.Uniform("SpotShadowAtlasEnabled", resident);
            materialProgram.Uniform("SpotShadowAtlasRecordIndex", recordIndex);
            materialProgram.Uniform("SpotShadowAtlasPageIndex", allocation.PageIndex);
            materialProgram.Uniform("SpotShadowAtlasUvScaleBias", allocation.UvScaleBias);
            materialProgram.Uniform("SpotShadowAtlasDepthParams", new Vector4(nearPlane, farPlane, texelSize, resolutionScale));
            materialProgram.Sampler(SpotShadowAtlasName, resident && atlasTexture is not null ? atlasTexture : DummyShadowMapArray, spotAtlasUnit);

            return resident;
        }

        private static bool TryBindPointAtlasShadow(
            XRRenderProgram materialProgram,
            PointLightComponent pointLight,
            IVector4[] pointShadowAtlasPacked0,
            Vector4[] pointShadowAtlasUvScaleBias,
            Vector4[] pointShadowAtlasDepthParams)
        {
            const int pointAtlasUnit = 30;

            var lights = ResolveLightsForLight(pointLight);
            bool requested = pointLight.UsesPointShadowAtlasForCurrentEncoding &&
                lights is not null &&
                pointLight.CastsShadows;

            ClearPointAtlasUniformData(
                ShadowFallbackMode.ContactOnly,
                pointShadowAtlasPacked0,
                pointShadowAtlasUvScaleBias,
                pointShadowAtlasDepthParams);
            bool hasSampleableFace = false;
            XRTexture2DArray? atlasTexture = null;
            if (requested)
            {
                ShadowMapFormatSelection shadowFormat = pointLight.ResolveShadowMapFormat(preferredStorageFormat: pointLight.ShadowMapStorageFormat);
                lights!.ShadowAtlas.TryGetPageTexture(EShadowAtlasKind.Point, shadowFormat.Encoding, 0, out atlasTexture);
                int atlasLayerCount = atlasTexture is not null ? checked((int)Math.Max(1u, atlasTexture.Depth)) : 0;
                for (int faceIndex = 0; faceIndex < PointLightComponent.ShadowFaceCount; faceIndex++)
                {
                    if (!lights.TryGetPointShadowAtlasFaceAllocation(
                        pointLight,
                        faceIndex,
                        out ShadowAtlasAllocation allocation,
                        out int recordIndex))
                        continue;
                    
                    ShadowFallbackMode fallback = allocation.ActiveFallback != ShadowFallbackMode.None
                        ? allocation.ActiveFallback
                        : ShadowFallbackMode.Lit;
                    bool resident = allocation.Key.Encoding == shadowFormat.Encoding &&
                        IsShadowAtlasAllocationSampleable(allocation) &&
                        allocation.PageIndex < atlasLayerCount;
                    uint sampleResolution = LightComponent.GetShadowAtlasSampleResolution(allocation);
                    float texelSize = sampleResolution > 0u ? 1.0f / sampleResolution : 0.0f;
                    float resolutionScale = pointLight.GetShadowAtlasResolutionScale(sampleResolution);

                    pointShadowAtlasPacked0[faceIndex] = new IVector4(
                        resident ? 1 : 0,
                        allocation.PageIndex,
                        (int)fallback,
                        recordIndex);
                    pointShadowAtlasUvScaleBias[faceIndex] = allocation.UvScaleBias;
                    pointShadowAtlasDepthParams[faceIndex] = new Vector4(
                        pointLight.ShadowNearPlaneDistance,
                        MathF.Max(pointLight.Radius, pointLight.ShadowNearPlaneDistance + 0.001f),
                        texelSize,
                        resolutionScale);
                    hasSampleableFace |= resident;
                }
            }

            materialProgram.Sampler(PointShadowAtlasName, hasSampleableFace && atlasTexture is not null ? atlasTexture : DummyShadowMapArray, pointAtlasUnit);

            materialProgram.Uniform("PointShadowAtlasPathEnabled", requested);
            materialProgram.Uniform("PointShadowAtlasPacked0", pointShadowAtlasPacked0);
            materialProgram.Uniform("PointShadowAtlasUvScaleBias", pointShadowAtlasUvScaleBias);
            materialProgram.Uniform("PointShadowAtlasDepthParams", pointShadowAtlasDepthParams);
            return requested;
        }

        private static void BindDisabledPointAtlas(
            XRRenderProgram materialProgram,
            IVector4[] pointShadowAtlasPacked0,
            Vector4[] pointShadowAtlasUvScaleBias,
            Vector4[] pointShadowAtlasDepthParams)
        {
            const int pointAtlasUnit = 30;
            ClearPointAtlasUniformData(
                ShadowFallbackMode.Legacy,
                pointShadowAtlasPacked0,
                pointShadowAtlasUvScaleBias,
                pointShadowAtlasDepthParams);
            materialProgram.Sampler(PointShadowAtlasName, DummyShadowMapArray, pointAtlasUnit);

            materialProgram.Uniform("PointShadowAtlasPathEnabled", false);
            materialProgram.Uniform("PointShadowAtlasPacked0", pointShadowAtlasPacked0);
            materialProgram.Uniform("PointShadowAtlasUvScaleBias", pointShadowAtlasUvScaleBias);
            materialProgram.Uniform("PointShadowAtlasDepthParams", pointShadowAtlasDepthParams);
        }

        private static bool IsShadowAtlasAllocationSampleable(in ShadowAtlasAllocation allocation)
            => allocation.IsResident &&
               allocation.LastRenderedFrame != 0u &&
               allocation.ActiveFallback is ShadowFallbackMode.None or ShadowFallbackMode.StaleTile &&
               allocation.PageIndex >= 0;

        private static void ClearPointAtlasUniformData(
            ShadowFallbackMode fallbackMode,
            IVector4[] pointShadowAtlasPacked0,
            Vector4[] pointShadowAtlasUvScaleBias,
            Vector4[] pointShadowAtlasDepthParams)
        {
            for (int faceIndex = 0; faceIndex < PointLightComponent.ShadowFaceCount; faceIndex++)
            {
                pointShadowAtlasPacked0[faceIndex] = new IVector4(0, -1, (int)fallbackMode, -1);
                pointShadowAtlasUvScaleBias[faceIndex] = Vector4.Zero;
                pointShadowAtlasDepthParams[faceIndex] = new Vector4(0.1f, 1.0f, 0.0f, 1.0f);
            }
        }

        private static void BindDisabledSpotAtlas(XRRenderProgram materialProgram)
        {
            const int spotAtlasUnit = 30;
            materialProgram.Uniform("SpotShadowAtlasEnabled", false);
            materialProgram.Uniform("SpotShadowAtlasRecordIndex", -1);
            materialProgram.Uniform("SpotShadowAtlasPageIndex", 0);
            materialProgram.Uniform("SpotShadowAtlasUvScaleBias", Vector4.Zero);
            materialProgram.Uniform("SpotShadowAtlasDepthParams", new Vector4(0.1f, 1.0f, 0.0f, 1.0f));
            materialProgram.Sampler(SpotShadowAtlasName, DummyShadowMapArray, spotAtlasUnit);
        }

        private static Lights3DCollection? ResolveLightsForLight(LightComponent light)
        {
            Lights3DCollection? lightWorldLights = light.WorldAs<IRuntimeRenderWorld>()?.Lights;
            if (lightWorldLights is not null)
                return lightWorldLights;

            return ActivePipelineInstance.RenderState.WindowViewport?.World?.Lights;
        }

        private static bool BindDirectionalAtlasShadows(
            XRRenderProgram materialProgram,
            DirectionalLightComponent directionalLight,
            XRCamera? activeRenderingCamera,
            bool useCascadedDirectionalShadows,
            IVector4[] directionalShadowAtlasPacked0,
            Vector4[] directionalShadowAtlasUvScaleBias,
            Vector4[] directionalShadowAtlasDepthParams,
            out bool hasSampleableAtlasTile)
        {
            var lights = ResolveLightsForLight(directionalLight);
            bool requested = directionalLight.UsesDirectionalShadowAtlasForCurrentEncoding &&
                lights is not null &&
                directionalLight.CastsShadows;

            Array.Clear(directionalShadowAtlasPacked0);
            Array.Clear(directionalShadowAtlasUvScaleBias);
            Array.Clear(directionalShadowAtlasDepthParams);
            hasSampleableAtlasTile = false;
            if (requested)
            {
                directionalLight.CopyPublishedDirectionalAtlasUniformData(
                    activeRenderingCamera,
                    useCascadedDirectionalShadows,
                    directionalShadowAtlasPacked0,
                    directionalShadowAtlasUvScaleBias,
                    directionalShadowAtlasDepthParams);
                hasSampleableAtlasTile = AreRequiredDirectionalAtlasTilesSampleable(
                    directionalShadowAtlasPacked0,
                    useCascadedDirectionalShadows ? directionalLight.GetActiveCascadeCount(activeRenderingCamera) : 1);
            }

            XRTexture2DArray? atlasTexture = null;
            if (hasSampleableAtlasTile)
            {
                ShadowMapFormatSelection shadowFormat = directionalLight.ResolveShadowMapFormat(preferredStorageFormat: null);
                hasSampleableAtlasTile =
                    lights!.ShadowAtlas.TryGetPageTexture(EShadowAtlasKind.Directional, shadowFormat.Encoding, 0, out atlasTexture) &&
                    AreRequiredDirectionalAtlasTilesSampleable(
                        directionalShadowAtlasPacked0,
                        useCascadedDirectionalShadows ? directionalLight.GetActiveCascadeCount(activeRenderingCamera) : 1,
                        checked((int)Math.Max(1u, atlasTexture.Depth)));
            }
            materialProgram.Sampler(DirectionalShadowAtlasName, atlasTexture ?? DummyShadowMapArray, DirectionalShadowAtlasUnit);

            LogDeferredDirectionalShadowBinding(
                directionalLight,
                activeRenderingCamera,
                requested,
                hasSampleableAtlasTile,
                useCascadedDirectionalShadows,
                lights,
                directionalShadowAtlasPacked0);

            materialProgram.Uniform("DirectionalShadowAtlasEnabled", hasSampleableAtlasTile);
            materialProgram.Uniform("DirectionalShadowAtlasPacked0", directionalShadowAtlasPacked0);
            materialProgram.Uniform("DirectionalShadowAtlasUvScaleBias", directionalShadowAtlasUvScaleBias);
            materialProgram.Uniform("DirectionalShadowAtlasDepthParams", directionalShadowAtlasDepthParams);
            return hasSampleableAtlasTile;
        }

        private static bool AreRequiredDirectionalAtlasTilesSampleable(IVector4[] packed0, int count)
            => AreRequiredDirectionalAtlasTilesSampleable(packed0, count, int.MaxValue);

        private static bool AreRequiredDirectionalAtlasTilesSampleable(IVector4[] packed0, int count, int maxPageCount)
        {
            int clampedCount = Math.Min(Math.Max(count, 0), packed0.Length);
            if (clampedCount <= 0)
                return false;

            for (int i = 0; i < clampedCount; i++)
            {
                IVector4 packed = packed0[i];
                if (packed.X == 0 || packed.Y < 0 || packed.Y >= maxPageCount)
                    return false;
            }

            return true;
        }

        private static void LogDeferredDirectionalShadowBinding(
            DirectionalLightComponent light,
            XRCamera? activeRenderingCamera,
            bool requested,
            bool shaderAtlasEnabled,
            bool useCascadedDirectionalShadows,
            Lights3DCollection? lights,
            IVector4[] directionalShadowAtlasPacked0)
        {
            if (!RenderDiagnosticsFlags.DirectionalShadowAudit ||
                !Debug.ShouldLogEvery(
                $"DirectionalShadowAudit.DeferredBind.{light.GetHashCode()}",
                TimeSpan.FromSeconds(1.0)))
            {
                return;
            }

            ShadowAtlasMetrics metrics = lights?.ShadowAtlas.PublishedFrameData.Metrics ?? default;
            ShadowRequestSource source = DirectionalLightComponent.GetCascadeSourceForCamera(activeRenderingCamera);
            int activeCascadeCount = light.GetActiveCascadeCount(source);
            Debug.Lighting(
                EOutputVerbosity.Normal,
                false,
                "[DirectionalShadowAudit][DeferredBind] frame={0} light='{1}' source={2} requestedAtlas={3} shaderAtlasEnabled={4} cascades={5} activeCascades={6} shadowMap={7} cascadeColorTex={8} cascadeRasterDepthTex={9} cascadeReceiverTex={10} useRasterCascadeReceiver={11} atlasRequests={12} atlasRenderedThisFrame={13} atlasPages={14} c0={15} c1={16} c2={17} c3={18}",
                RuntimeEngine.Rendering.State.RenderFrameId,
                light.SceneNode?.Name ?? light.Name ?? light.GetType().Name,
                source,
                requested,
                shaderAtlasEnabled,
                useCascadedDirectionalShadows,
                activeCascadeCount,
                light.ShadowMap is not null,
                light.HasCascadeColorTextureForSource(source),
                light.HasCascadeRasterDepthTextureForSource(source),
                light.GetCascadedShadowReceiverTexture(source) is not null,
                light.UsesCascadeRasterDepthReceiver,
                metrics.RequestCount,
                metrics.TilesScheduledThisFrame,
                metrics.PageCount,
                FormatAtlasPacked(directionalShadowAtlasPacked0, 0),
                FormatAtlasPacked(directionalShadowAtlasPacked0, 1),
                FormatAtlasPacked(directionalShadowAtlasPacked0, 2),
                FormatAtlasPacked(directionalShadowAtlasPacked0, 3));
        }

        private static string FormatAtlasPacked(IVector4[] packed0, int index)
        {
            if ((uint)index >= (uint)packed0.Length)
                return "<out>";

            IVector4 value = packed0[index];
            return $"({value.X},{value.Y},{value.Z},{value.W})";
        }

        private static void BindDisabledDirectionalAtlasShadows(
            XRRenderProgram materialProgram,
            IVector4[] directionalShadowAtlasPacked0,
            Vector4[] directionalShadowAtlasUvScaleBias,
            Vector4[] directionalShadowAtlasDepthParams)
        {
            materialProgram.Sampler(DirectionalShadowAtlasName, DummyShadowMapArray, DirectionalShadowAtlasUnit);

            Array.Clear(directionalShadowAtlasPacked0);
            Array.Clear(directionalShadowAtlasUvScaleBias);
            Array.Clear(directionalShadowAtlasDepthParams);
            materialProgram.Uniform("DirectionalShadowAtlasEnabled", false);
            materialProgram.Uniform("DirectionalShadowAtlasPacked0", directionalShadowAtlasPacked0);
            materialProgram.Uniform("DirectionalShadowAtlasUvScaleBias", directionalShadowAtlasUvScaleBias);
            materialProgram.Uniform("DirectionalShadowAtlasDepthParams", directionalShadowAtlasDepthParams);
        }

        private void CreateLightRenderers(
            LightRendererCache cache,
            XRTexture albOpacTex,
            XRTexture normTex,
            XRTexture rmseTex,
            XRTexture depthViewTex)
        {
            XRTexture[] lightRefs =
            [
                albOpacTex,
                normTex,
                rmseTex,
                depthViewTex,
                //shadow map texture
            ];

            bool stereoDeferred = IsStereoDeferredLighting();
            XRShader pointLightShader = CreateDeferredLightingShader("DeferredLightingPoint.fs", stereoDeferred);
            XRShader spotLightShader = CreateDeferredLightingShader("DeferredLightingSpot.fs", stereoDeferred);
            XRShader dirLightShader = CreateDeferredLightingShader("DeferredLightingDir.fs", stereoDeferred);

            XRMaterial pointLightMat = new(lightRefs, pointLightShader) { RenderOptions = GetAdditiveParameters(), RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial spotLightMat = new(lightRefs, spotLightShader) { RenderOptions = GetAdditiveParameters(), RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial dirLightMat = CreateFullscreenDirectionalLightMaterial(lightRefs, dirLightShader, GetFullscreenAdditiveParameters());

            XRMesh pointLightMesh = PointLightComponent.GetVolumeMesh();
            XRMesh spotLightMesh = SpotLightComponent.GetVolumeMesh();

            // Phase C: deferred-light combine renderers must be ready on the
            // first frame their pipeline pass runs; opt out of async default.
            cache.PointLightRenderer = new XRMeshRenderer(pointLightMesh, pointLightMat)
            {
                GenerateAsync = false,
                CaptureUniformsOnRender = true,
            };
            PrepareRenderPipelineLightRenderer(cache.PointLightRenderer);
            cache.PointLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            cache.SpotLightRenderer = new XRMeshRenderer(spotLightMesh, spotLightMat)
            {
                GenerateAsync = false,
                CaptureUniformsOnRender = true,
            };
            PrepareRenderPipelineLightRenderer(cache.SpotLightRenderer);
            cache.SpotLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            cache.DirectionalLightRenderer = CreateFullscreenDirectionalLightRenderer(dirLightMat);
            cache.DirectionalLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            // Invalidate MSAA renderers since the simple phase depends on the resolved GBuffer.
            cache.MsaaSimplePointLightRenderer = null;
            cache.MsaaSimpleSpotLightRenderer = null;
            cache.MsaaSimpleDirectionalLightRenderer = null;
            cache.MsaaComplexPointLightRenderer = null;
            cache.MsaaComplexSpotLightRenderer = null;
            cache.MsaaComplexDirectionalLightRenderer = null;
        }

        private static bool IsStereoDeferredLighting()
            => ActivePipelineInstance.Pipeline is DefaultRenderPipeline { Stereo: true } or DefaultRenderPipeline2 { Stereo: true };

        private static XRShader CreateDeferredLightingShader(string shaderName, bool stereoDeferred)
        {
            XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath, shaderName), EShaderType.Fragment);
            return stereoDeferred
                ? ShaderHelper.CreateDefinedShaderVariant(shader, StereoDeferredDefine) ?? shader
                : shader;
        }

        private bool EnsureMsaaRenderers(LightRendererCache cache)
        {
            var msaaAlbedoTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaAlbedoOpacityTexture);
            var msaaNormTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaNormalTexture);
            var msaaRmseTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaRMSETexture);
            var msaaDepthTex = ActivePipelineInstance.GetTexture<XRTexture>(MsaaDepthViewTexture);
            if (msaaAlbedoTex is null || msaaNormTex is null || msaaRmseTex is null || msaaDepthTex is null)
                return false;

            if (cache.MsaaAlbedoOpacityTexture == msaaAlbedoTex &&
                cache.MsaaNormalTexture == msaaNormTex &&
                cache.MsaaRMSETexture == msaaRmseTex &&
                cache.MsaaDepthViewTexture == msaaDepthTex &&
                cache.MsaaSimplePointLightRenderer is not null &&
                cache.MsaaComplexPointLightRenderer is not null)
                return true;

            cache.MsaaAlbedoOpacityTexture = msaaAlbedoTex;
            cache.MsaaNormalTexture = msaaNormTex;
            cache.MsaaRMSETexture = msaaRmseTex;
            cache.MsaaDepthViewTexture = msaaDepthTex;

            CreateMsaaLightRenderers(cache);
            return cache.MsaaSimplePointLightRenderer is not null &&
                cache.MsaaComplexPointLightRenderer is not null;
        }

        private void CreateMsaaLightRenderers(LightRendererCache cache)
        {
            if (cache.AlbedoOpacityTexture is null ||
                cache.NormalTexture is null ||
                cache.RMSETexture is null ||
                cache.DepthViewTexture is null ||
                cache.MsaaAlbedoOpacityTexture is null ||
                cache.MsaaNormalTexture is null ||
                cache.MsaaRMSETexture is null ||
                cache.MsaaDepthViewTexture is null)
            {
                cache.MsaaSimplePointLightRenderer = null;
                cache.MsaaSimpleSpotLightRenderer = null;
                cache.MsaaSimpleDirectionalLightRenderer = null;
                cache.MsaaComplexPointLightRenderer = null;
                cache.MsaaComplexSpotLightRenderer = null;
                cache.MsaaComplexDirectionalLightRenderer = null;
                return;
            }

            uint complexPixelStencilBit = VPRC_MarkComplexMsaaPixels.ComplexPixelStencilBit;

            XRTexture[] simpleLightRefs =
            [
                cache.AlbedoOpacityTexture,
                cache.NormalTexture,
                cache.RMSETexture,
                cache.DepthViewTexture,
            ];

            XRShader basePoint = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DeferredLightingPoint.fs"), EShaderType.Fragment);
            XRShader baseSpot = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DeferredLightingSpot.fs"), EShaderType.Fragment);
            XRShader baseDir = XRShader.EngineShader(Path.Combine(SceneShaderPath, "DeferredLightingDir.fs"), EShaderType.Fragment);

            XRShader msaaPointShader = ShaderHelper.CreateDefinedShaderVariant(basePoint, MsaaDeferredDefine) ?? basePoint;
            XRShader msaaSpotShader = ShaderHelper.CreateDefinedShaderVariant(baseSpot, MsaaDeferredDefine) ?? baseSpot;
            XRShader msaaDirShader = ShaderHelper.CreateDefinedShaderVariant(baseDir, MsaaDeferredDefine) ?? baseDir;

            XRMaterial simplePointMat = new(simpleLightRefs, basePoint) { RenderOptions = GetAdditiveParametersWithStencil(complexPixelStencilBit, EComparison.Nequal), RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial simpleSpotMat = new(simpleLightRefs, baseSpot) { RenderOptions = GetAdditiveParametersWithStencil(complexPixelStencilBit, EComparison.Nequal), RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial simpleDirMat = CreateFullscreenDirectionalLightMaterial(simpleLightRefs, baseDir, GetFullscreenAdditiveParametersWithStencil(complexPixelStencilBit, EComparison.Nequal));

            XRTexture?[] msaaLightRefs =
            [
                cache.MsaaAlbedoOpacityTexture,
                cache.MsaaNormalTexture,
                cache.MsaaRMSETexture,
                cache.MsaaDepthViewTexture,
            ];

            XRMaterial msaaPointMat = new(msaaLightRefs, msaaPointShader) { RenderOptions = GetAdditiveParametersWithStencil(complexPixelStencilBit, EComparison.Equal), RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial msaaSpotMat = new(msaaLightRefs, msaaSpotShader) { RenderOptions = GetAdditiveParametersWithStencil(complexPixelStencilBit, EComparison.Equal), RenderPass = (int)EDefaultRenderPass.OpaqueForward };
            XRMaterial msaaDirMat = CreateFullscreenDirectionalLightMaterial(msaaLightRefs, msaaDirShader, GetFullscreenAdditiveParametersWithStencil(complexPixelStencilBit, EComparison.Equal));

            XRMesh pointMesh = PointLightComponent.GetVolumeMesh();
            XRMesh spotMesh = SpotLightComponent.GetVolumeMesh();

            // Phase C: MSAA combine renderers must be ready on the first
            // pipeline frame they run; opt out of async default.
            cache.MsaaSimplePointLightRenderer = new XRMeshRenderer(pointMesh, simplePointMat)
            {
                GenerateAsync = false,
                CaptureUniformsOnRender = true,
            };
            PrepareRenderPipelineLightRenderer(cache.MsaaSimplePointLightRenderer);
            cache.MsaaSimplePointLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            cache.MsaaSimpleSpotLightRenderer = new XRMeshRenderer(spotMesh, simpleSpotMat)
            {
                GenerateAsync = false,
                CaptureUniformsOnRender = true,
            };
            PrepareRenderPipelineLightRenderer(cache.MsaaSimpleSpotLightRenderer);
            cache.MsaaSimpleSpotLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            cache.MsaaSimpleDirectionalLightRenderer = CreateFullscreenDirectionalLightRenderer(simpleDirMat);
            cache.MsaaSimpleDirectionalLightRenderer.SettingUniforms += LightManager_SettingUniforms;

            cache.MsaaComplexPointLightRenderer = new XRMeshRenderer(pointMesh, msaaPointMat)
            {
                GenerateAsync = false,
                CaptureUniformsOnRender = true,
            };
            PrepareRenderPipelineLightRenderer(cache.MsaaComplexPointLightRenderer);
            cache.MsaaComplexPointLightRenderer.SettingUniforms += MsaaLightManager_SettingUniforms;

            cache.MsaaComplexSpotLightRenderer = new XRMeshRenderer(spotMesh, msaaSpotMat)
            {
                GenerateAsync = false,
                CaptureUniformsOnRender = true,
            };
            PrepareRenderPipelineLightRenderer(cache.MsaaComplexSpotLightRenderer);
            cache.MsaaComplexSpotLightRenderer.SettingUniforms += MsaaLightManager_SettingUniforms;

            cache.MsaaComplexDirectionalLightRenderer = CreateFullscreenDirectionalLightRenderer(msaaDirMat);
            cache.MsaaComplexDirectionalLightRenderer.SettingUniforms += MsaaLightManager_SettingUniforms;
        }

        private static XRMaterial CreateFullscreenDirectionalLightMaterial(XRTexture?[] lightRefs, XRShader fragmentShader, RenderingParameters renderOptions)
        {
            XRMaterial material = new(lightRefs, fragmentShader)
            {
                RenderOptions = renderOptions,
                RenderPass = (int)EDefaultRenderPass.OpaqueForward,
            };
            material.SetShader(
                EShaderType.Vertex,
                XRShader.EngineShader(Path.Combine(SceneShaderPath, "FullscreenTri.vs"), EShaderType.Vertex));
            return material;
        }

        private static XRMeshRenderer CreateFullscreenDirectionalLightRenderer(XRMaterial material)
        {
            XRMeshRenderer renderer = new(CreateFullscreenTriangle(), material)
            {
                GenerateAsync = false,
                CaptureUniformsOnRender = true,
            };
            PrepareRenderPipelineLightRenderer(renderer);
            return renderer;
        }

        private static void PrepareRenderPipelineLightRenderer(XRMeshRenderer renderer)
        {
            renderer.GenerationPriority = EMeshGenerationPriority.RenderPipeline;
            renderer.SetShaderPipelinesAllowedForAllVersions(false);
            renderer.EnsureRenderPipelineVersionsCreated();
        }

        private static XRMesh CreateFullscreenTriangle()
        {
            VertexTriangle triangle = new(
                new Vector3(-1, -1, 0),
                new Vector3( 3, -1, 0),
                new Vector3(-1,  3, 0));
            return XRMesh.Create(triangle);
        }

        private static RenderingParameters GetAdditiveParameters()
        {
            RenderingParameters additiveRenderParams = new()
            {
                //Render only the backside so that the light still shows if the camera is inside of the volume
                //and the light does not add itself twice for the front and back faces.
                CullMode = ECullMode.Front,
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy,
                BlendModeAllDrawBuffers = new()
                {
                    //Add the previous and current light colors together using FuncAdd with each mesh render
                    Enabled = ERenderParamUsage.Enabled,
                    RgbDstFactor = EBlendingFactor.One,
                    AlphaDstFactor = EBlendingFactor.One,
                    RgbSrcFactor = EBlendingFactor.One,
                    AlphaSrcFactor = EBlendingFactor.One,
                    RgbEquation = EBlendEquationMode.FuncAdd,
                    AlphaEquation = EBlendEquationMode.FuncAdd,
                },
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    },
                    StencilTest = new()
                    {
                        Enabled = ERenderParamUsage.Disabled,
                }
            };
            return additiveRenderParams;
        }

        private static RenderingParameters GetFullscreenAdditiveParameters()
        {
            RenderingParameters renderParams = GetAdditiveParameters();
            renderParams.CullMode = ECullMode.None;
            return renderParams;
        }

        private static string DescribeTexture(XRTexture? texture)
        {
            if (texture is null)
                return "<null>";

            var size = texture.WidthHeightDepth;
            return $"{texture.Name ?? texture.GetDescribingName()}({(int)size.X}x{(int)size.Y}x{(int)size.Z})";
        }

        /// <summary>
        /// Creates additive rendering parameters with a stencil test that gates on the complex pixel bit.
        /// </summary>
        private static RenderingParameters GetAdditiveParametersWithStencil(uint stencilBit, EComparison comparison)
        {
            var stencilFace = new StencilTestFace
            {
                Function = comparison,
                Reference = (int)stencilBit,
                ReadMask = stencilBit,
                WriteMask = 0, // Don't modify stencil during lighting
                BothFailOp = EStencilOp.Keep,
                StencilPassDepthFailOp = EStencilOp.Keep,
                BothPassOp = EStencilOp.Keep,
            };

            return new RenderingParameters
            {
                CullMode = ECullMode.Front,
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy,
                BlendModeAllDrawBuffers = new()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    RgbDstFactor = EBlendingFactor.One,
                    AlphaDstFactor = EBlendingFactor.One,
                    RgbSrcFactor = EBlendingFactor.One,
                    AlphaSrcFactor = EBlendingFactor.One,
                    RgbEquation = EBlendEquationMode.FuncAdd,
                    AlphaEquation = EBlendEquationMode.FuncAdd,
                },
                DepthTest = new()
                {
                    Enabled = ERenderParamUsage.Disabled,
                },
                StencilTest = new()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    FrontFace = stencilFace,
                    BackFace = stencilFace,
                },
            };
        }

        private static RenderingParameters GetFullscreenAdditiveParametersWithStencil(uint stencilBit, EComparison comparison)
        {
            RenderingParameters renderParams = GetAdditiveParametersWithStencil(stencilBit, comparison);
            renderParams.CullMode = ECullMode.None;
            return renderParams;
        }

        private int ResolvePassIndex(string passName, out bool hasRenderGraphMetadata)
        {
            var metadata = ParentPipeline?.PassMetadata;
            if (metadata is not { Count: > 0 } renderPasses)
            {
                hasRenderGraphMetadata = false;
                return int.MinValue;
            }

            hasRenderGraphMetadata = true;

            foreach (RenderPassMetadata pass in renderPasses)
            {
                if (string.Equals(pass.Name, passName, StringComparison.OrdinalIgnoreCase))
                    return pass.PassIndex;
            }

            return int.MinValue;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_LightCombinePass), ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeTextureResource(AlbedoOpacityTexture));
            builder.SampleTexture(MakeTextureResource(NormalTexture));
            builder.SampleTexture(MakeTextureResource(RMSETexture));
            builder.SampleTexture(MakeTextureResource(DepthViewTexture));

            if (context.CurrentRenderTarget is { } target)
            {
                target.ConsumeColorLoadOp();
                builder.UseColorAttachment(
                    MakeFboColorResource(target.Name),
                    target.ColorAccess,
                    ERenderPassLoadOp.Clear,
                    target.GetColorStoreOp());
            }
        }
    }
}
