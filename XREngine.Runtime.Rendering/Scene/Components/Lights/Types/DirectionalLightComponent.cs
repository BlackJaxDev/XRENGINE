using System;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shadows;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights
{
    /// <summary>
    /// Infinite directional light with one primary orthographic shadow view plus optional cascaded shadows.
    /// </summary>
    [XRComponentEditor("XREngine.Editor.ComponentEditors.DirectionalLightComponentEditor")]
    [RequiresTransform(typeof(Transform))]
    [Category("Lighting")]
    [DisplayName("Directional Light")]
    [Description("Illuminates the scene with an infinite directional light that can cast cascaded shadows.")]
    public partial class DirectionalLightComponent : OneViewLightComponent
    {
        private const float NearZ = 0.01f;

        private Vector3 _scale = Vector3.One;
        private float _cascadedShadowDistance = 200.0f;
        private readonly float[] _uniformCascadeSplits = new float[MaxCascadeRenderCount];
        private readonly float[] _uniformCascadeBlendWidths = new float[MaxCascadeRenderCount];
        private readonly float[] _uniformCascadeBiasMins = new float[MaxCascadeRenderCount];
        private readonly float[] _uniformCascadeBiasMaxes = new float[MaxCascadeRenderCount];
        private readonly float[] _uniformCascadeReceiverOffsets = new float[MaxCascadeRenderCount];
        private readonly Matrix4x4[] _uniformCascadeMatrices = new Matrix4x4[MaxCascadeRenderCount];
        private readonly float[] _uniformRenderedCascadeSplits = new float[MaxCascadeRenderCount];
        private readonly float[] _uniformRenderedCascadeBlendWidths = new float[MaxCascadeRenderCount];
        private readonly float[] _uniformRenderedCascadeBiasMins = new float[MaxCascadeRenderCount];
        private readonly float[] _uniformRenderedCascadeBiasMaxes = new float[MaxCascadeRenderCount];
        private readonly float[] _uniformRenderedCascadeReceiverOffsets = new float[MaxCascadeRenderCount];
        private readonly float[] _uniformRenderedCascadeStaleAges = new float[MaxCascadeRenderCount];
        private readonly Matrix4x4[] _uniformRenderedCascadeMatrices = new Matrix4x4[MaxCascadeRenderCount];

        /// <summary>
        /// Creates a directional light with tuned default shadow filtering and contact-shadow settings.
        /// </summary>
        public DirectionalLightComponent()
            : base(EShadowMapStorageFormat.Depth24)
        {
            _cascadeAabbView = new(this, ShadowRequestSource.Desktop);

            // Match the tuned runtime defaults used for live shadow-map rendering.
            SetShadowMapResolution(2048u, 2048u);
            ShadowExponentBase = 0.035f;
            ShadowExponent = 1.221f;
            ShadowMinBias = 0.00001f;
            ShadowMaxBias = 0.004f;
            ShadowDepthBiasTexels = 1.0f;
            ShadowSlopeBiasTexels = 2.0f;
            ShadowNormalBiasTexels = 1.0f;
            BlockerSamples = 8;
            FilterSamples = 8;
            FilterRadius = 0.0012f;
            BlockerSearchRadius = 0.01f;
            MinPenumbra = 0.001f;
            MaxPenumbra = 0.015f;
            SoftShadowMode = ESoftShadowMode.ContactHardeningPcss;
            LightSourceRadius = 1.2f;
            EnableContactShadows = true;
            ContactShadowDistance = 1.0f;
            ContactShadowSamples = 16;
            ContactShadowThickness = 2.0f;
            ContactShadowFadeStart = 10.0f;
            ContactShadowFadeEnd = 40.0f;
            ContactShadowNormalOffset = 0.0f;
            ContactShadowJitterStrength = 1.0f;
        }

        /// <summary>
        /// Indicates whether the directional light uses the directional shadow atlas for the current shadow map encoding.
        /// </summary>
        protected override EShadowMapStorageFormat DefaultShadowMapStorageFormat => EShadowMapStorageFormat.Depth24;

        /// <summary>
        /// Determines if the specified shadow map storage format is supported by the directional light.
        /// </summary>
        /// <param name="format">The shadow map storage format to check for support.</param>
        /// <returns>True if the specified format is supported; otherwise, false.</returns>
        public override bool SupportsShadowMapStorageFormat(EShadowMapStorageFormat format)
            => IsDepthShadowMapStorageFormat(format);

        /// <summary>
        /// Indicates whether the directional light uses the directional shadow atlas for the current shadow map encoding.
        /// </summary>
        [Browsable(false)]
        public bool UsesDirectionalShadowAtlasForCurrentEncoding
        {
            get
            {
                if (!RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas)
                    return false;

                EShadowMapEncoding encoding = ResolveDirectionalSamplingShadowMapFormat().Encoding;
                if (IsVulkanDirectionalShadowBackend())
                    return encoding == EShadowMapEncoding.Depth;

                return encoding is
                    EShadowMapEncoding.Depth or
                    EShadowMapEncoding.Variance2 or
                    EShadowMapEncoding.ExponentialVariance2 or
                    EShadowMapEncoding.ExponentialVariance4;
            }
        }

        /// <summary>
        /// Indicates whether the directional light uses the directional shadow atlas for the current shadow map encoding.
        /// </summary>
        protected override bool UsesAtlasShadowViewport
            => UsesDirectionalShadowAtlasForCurrentEncoding;

        /// <summary>
        /// Scale of the orthographic shadow volume.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Shadow Volume Scale")]
        [Description("Dimensions of the orthographic shadow frustum (width, height, depth).")]
        public Vector3 Scale
        {
            get => _scale;
            set => SetField(ref _scale, value);
        }

        /// <summary>
        /// Maximum camera distance covered by cascaded shadow splits for this light.
        /// When unset, the system falls back to the source camera shadow collection distance
        /// and then the camera far plane.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Distance")]
        [Description("Maximum view-space distance covered by cascaded shadows. Set to 0 or Infinity to use the source camera shadow distance / far plane.")]
        public float CascadedShadowDistance
        {
            get => _cascadedShadowDistance;
            set
            {
                float normalized = value;
                if (!float.IsFinite(normalized) || normalized <= 0.0f)
                    normalized = float.PositiveInfinity;

                SetField(ref _cascadedShadowDistance, normalized);
            }
        }

        /// <summary>
        /// Gets the effective far distance for cascaded shadows based on the source camera and the light's cascade settings.
        /// </summary>
        /// <param name="sourceCamera">The source camera used to determine the effective far distance.</param>
        /// <returns>The effective far distance for cascaded shadows.</returns>
        internal float GetEffectiveCascadedShadowFarDistance(XRCamera sourceCamera)
        {
            float near = sourceCamera.NearZ;
            float effectiveFar = sourceCamera.FarZ;
            if (!float.IsFinite(effectiveFar) || effectiveFar <= near)
                effectiveFar = near + 1.0f;

            effectiveFar = ResolveFiniteCascadeDistanceLimit(effectiveFar, sourceCamera.ShadowCollectMaxDistance);
            effectiveFar = ResolveFiniteCascadeDistanceLimit(effectiveFar, CascadedShadowDistance);
            return MathF.Max(near + 1e-4f, effectiveFar);
        }

        /// <summary>
        /// Resolves the effective finite cascade distance limit by comparing the current distance with a candidate distance and returning the smaller valid value.
        /// </summary>
        /// <param name="current">The current effective cascade distance.</param>
        /// <param name="candidate">The candidate cascade distance to compare against.</param>
        /// <returns>The resolved effective cascade distance limit.</returns>
        private static float ResolveFiniteCascadeDistanceLimit(float current, float candidate)
            => float.IsFinite(candidate) && candidate > 0.0f
                ? MathF.Min(current, candidate)
                : current;

        /// <summary>
        /// Gets the volume mesh used to represent the directional light's influence in the scene.
        /// </summary>
        /// <returns>The volume mesh representing the directional light's influence.</returns>
        public static XRMesh GetVolumeMesh()
            => XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f));

        /// <summary>
        /// Gets the wireframe mesh used to represent the directional light's influence in the scene.
        /// </summary>
        /// <returns>The wireframe mesh representing the directional light's influence.</returns>
        protected override XRMesh GetWireframeMesh()
            => XRMesh.Shapes.WireframeBox(new Vector3(-0.5f), new Vector3(0.5f));

        /// <summary>
        /// Gets the camera parameters used for the directional light's shadow camera.
        /// </summary>
        /// <returns>The camera parameters for the directional light's shadow camera.</returns>
        protected override XRCameraParameters GetCameraParameters()
        {
            XROrthographicCameraParameters parameters = new(Scale.X, Scale.Y, NearZ, Scale.Z - NearZ);
            parameters.SetOriginPercentages(0.5f, 0.5f);
            return parameters;
        }

        /// <summary>
        /// Gets the shadow bias projection parameters for the directional light's primary shadow map.
        /// </summary>
        /// <returns>The shadow bias projection parameters as a Vector4.</returns>
        /// </summary>
        [Browsable(false)]
        public override Vector4 ShadowBiasProjectionParameters
            => GetPrimaryShadowBiasProjectionParameters();

        /// <summary>
        /// Gets the shadow bias projection parameters for the directional light's primary shadow map.
        /// </summary>
        /// <returns>The shadow bias projection parameters as a Vector4.</returns>
        internal Vector4 GetPrimaryShadowBiasProjectionParameters()
        {
            float width = MathF.Max(Scale.X, 1e-4f);
            float height = MathF.Max(Scale.Y, 1e-4f);
            float depthRange = MathF.Max(Scale.Z - NearZ, 1e-4f);

            if (ShadowCamera?.Parameters is XROrthographicCameraParameters ortho)
            {
                width = MathF.Max(ortho.Width, 1e-4f);
                height = MathF.Max(ortho.Height, 1e-4f);
                depthRange = MathF.Max(ortho.FarZ - ortho.NearZ, 1e-4f);
            }

            float mapWidth = MathF.Max(1.0f, ShadowMapResolutionWidth);
            float mapHeight = MathF.Max(1.0f, ShadowMapResolutionHeight);
            float texelWorldSize = MathF.Max(width / mapWidth, height / mapHeight);
            float constantDepthBias = texelWorldSize * ShadowDepthBiasTexels / depthRange;
            float normalOffset = texelWorldSize * ShadowNormalBiasTexels;
            return new Vector4(constantDepthBias, normalOffset, texelWorldSize, depthRange);
        }

        /// <summary>
        /// Gets the parent transform for the directional light's shadow camera.
        /// </summary>
        /// <returns>The parent transform for the shadow camera.</returns>
        protected override TransformBase GetShadowCameraParentTransform()
            => ShadowCameraTransform;

        private Transform? _shadowCameraTransform;
        /// <summary>
        /// Gets the transform for the directional light's shadow camera.
        /// </summary>
        /// <returns>The transform for the shadow camera.</returns>
        /// </summary>
        private Transform ShadowCameraTransform => _shadowCameraTransform ??= new Transform()
        {
            Parent = Transform,
            Order = XREngine.Animation.ETransformOrder.TRS,
            Translation = Globals.Backward * Scale.Z * 0.5f,
        };

        /// <summary>
        /// Called when the transform of the directional light changes, updating the shadow camera's parent transform accordingly.
        /// </summary>
        /// <remarks>
        /// This ensures that the shadow camera remains correctly positioned relative to the directional light.
        /// </remarks>
        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();
            _shadowCameraTransform?.Parent = Transform;
        }

        /// <summary>
        /// Registers the directional light as a dynamic light within the specified render world.
        /// </summary>
        /// <param name="world">The render world in which to register the dynamic light.</param>
        protected override void RegisterDynamicLight(IRuntimeRenderWorld world)
            => world.Lights.DynamicDirectionalLights.Add(this);

        /// <summary>
        /// Unregisters the directional light from the specified render world.
        /// </summary>
        /// <param name="world">The render world from which to unregister the dynamic light.</param>
        protected override void UnregisterDynamicLight(IRuntimeRenderWorld world)
            => world.Lights.DynamicDirectionalLights.Remove(this);

        /// <summary>
        /// Sets the shader uniforms for the directional light, including both legacy flat uniforms and structured ForwardLighting uniforms.
        /// </summary>
        /// <remarks>
        /// This method ensures that both the legacy flat uniforms and the structured ForwardLighting uniforms are updated for the directional light.
        /// </remarks>
        /// <param name="program">The render program to which the uniforms will be set.</param>
        /// <param name="targetStructName">Optional. The name of the target struct in the shader for structured uniforms.</param>
        public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            base.SetUniforms(program, targetStructName);

            bool enableCascadesForBackend = EnableCascadedShadows && CanRenderDirectionalCascadesForCurrentBackend();
            program.Uniform(RuntimeEngine.Rendering.Constants.EnableCascadedShadows, enableCascadesForBackend);

            string prefix = targetStructName ?? RuntimeEngine.Rendering.Constants.LightsStructName;
            string flatPrefix = $"{prefix}.";
            string basePrefix = $"{prefix}.Base.";

            // Populate both legacy flat uniforms and structured Base.* uniforms expected by the ForwardLighting snippet.
            program.Uniform($"{flatPrefix}Direction", Transform.WorldForward);
            program.Uniform($"{flatPrefix}Color", _color);
            program.Uniform($"{flatPrefix}DiffuseIntensity", _diffuseIntensity);
            Matrix4x4 lightView = ShadowCamera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 lightProj = ShadowCamera?.ProjectionMatrix ?? Matrix4x4.Identity;
            // C# Matrix4x4 is row-major but OpenGL expects column-major.
            // When uploading with transpose=false, the matrix gets transposed.
            // For GLSL's (mat * vec) convention to work, we need to reverse the multiplication order:
            // CPU: View * Proj (which becomes (Proj * View)^T when uploaded)
            // GLSL then computes: ((Proj * View)^T) * v = v^T * (Proj * View) = same result
            Matrix4x4 lightViewProj = lightView * lightProj;

            program.Uniform($"{flatPrefix}WorldToLightProjMatrix", lightProj);
            program.Uniform($"{flatPrefix}WorldToLightInvViewMatrix", ShadowCamera?.Transform.WorldMatrix ?? Matrix4x4.Identity);
            program.Uniform($"{flatPrefix}WorldToLightSpaceMatrix", lightViewProj);  // Pre-computed for deferred shadow mapping

            float[] cascadeSplits = _uniformCascadeSplits;
            float[] cascadeBlendWidths = _uniformCascadeBlendWidths;
            float[] cascadeBiasMins = _uniformCascadeBiasMins;
            float[] cascadeBiasMaxes = _uniformCascadeBiasMaxes;
            float[] cascadeReceiverOffsets = _uniformCascadeReceiverOffsets;
            Matrix4x4[] cascadeMatrices = _uniformCascadeMatrices;
            float[] renderedCascadeSplits = _uniformRenderedCascadeSplits;
            float[] renderedCascadeBlendWidths = _uniformRenderedCascadeBlendWidths;
            float[] renderedCascadeBiasMins = _uniformRenderedCascadeBiasMins;
            float[] renderedCascadeBiasMaxes = _uniformRenderedCascadeBiasMaxes;
            float[] renderedCascadeReceiverOffsets = _uniformRenderedCascadeReceiverOffsets;
            float[] renderedCascadeStaleAges = _uniformRenderedCascadeStaleAges;
            Matrix4x4[] renderedCascadeMatrices = _uniformRenderedCascadeMatrices;
            CopyPublishedCascadeUniformData(
                RuntimeEngine.Rendering.State.RenderingCamera,
                cascadeSplits,
                cascadeBlendWidths,
                cascadeBiasMins,
                cascadeBiasMaxes,
                cascadeReceiverOffsets,
                cascadeMatrices,
                out int cascadeCount);
            CopyPublishedRenderedCascadeUniformData(
                RuntimeEngine.Rendering.State.RenderingCamera,
                renderedCascadeSplits,
                renderedCascadeBlendWidths,
                renderedCascadeBiasMins,
                renderedCascadeBiasMaxes,
                renderedCascadeReceiverOffsets,
                renderedCascadeMatrices,
                renderedCascadeStaleAges,
                out _);

            // Set the cascade count uniform before setting individual cascade uniforms.
            program.Uniform($"{flatPrefix}CascadeCount", cascadeCount);
            if (IsVulkanDirectionalShadowBackend())
            {
                program.Uniform($"{flatPrefix}CascadeSplits", cascadeSplits);
                program.Uniform($"{flatPrefix}CascadeBlendWidths", cascadeBlendWidths);
                program.Uniform($"{flatPrefix}CascadeBiasMin", cascadeBiasMins);
                program.Uniform($"{flatPrefix}CascadeBiasMax", cascadeBiasMaxes);
                program.Uniform($"{flatPrefix}CascadeReceiverOffsets", cascadeReceiverOffsets);
                program.Uniform($"{flatPrefix}CascadeMatrices", cascadeMatrices);
                program.Uniform($"{flatPrefix}RenderedCascadeSplits", renderedCascadeSplits);
                program.Uniform($"{flatPrefix}RenderedCascadeBlendWidths", renderedCascadeBlendWidths);
                program.Uniform($"{flatPrefix}RenderedCascadeBiasMin", renderedCascadeBiasMins);
                program.Uniform($"{flatPrefix}RenderedCascadeBiasMax", renderedCascadeBiasMaxes);
                program.Uniform($"{flatPrefix}RenderedCascadeReceiverOffsets", renderedCascadeReceiverOffsets);
                program.Uniform($"{flatPrefix}RenderedCascadeMatrices", renderedCascadeMatrices);
                program.Uniform($"{flatPrefix}RenderedCascadeStaleAge", renderedCascadeStaleAges);
            }

            // Set individual cascade uniforms for the maximum number of cascades.
            for (int i = 0; i < MaxCascadeRenderCount; i++)
            {
                program.Uniform($"{flatPrefix}CascadeSplits[{i}]", cascadeSplits[i]);
                program.Uniform($"{flatPrefix}CascadeBlendWidths[{i}]", cascadeBlendWidths[i]);
                program.Uniform($"{flatPrefix}CascadeBiasMin[{i}]", cascadeBiasMins[i]);
                program.Uniform($"{flatPrefix}CascadeBiasMax[{i}]", cascadeBiasMaxes[i]);
                program.Uniform($"{flatPrefix}CascadeReceiverOffsets[{i}]", cascadeReceiverOffsets[i]);
                program.Uniform($"{flatPrefix}CascadeMatrices[{i}]", cascadeMatrices[i]);
                program.Uniform($"{flatPrefix}RenderedCascadeSplits[{i}]", renderedCascadeSplits[i]);
                program.Uniform($"{flatPrefix}RenderedCascadeBlendWidths[{i}]", renderedCascadeBlendWidths[i]);
                program.Uniform($"{flatPrefix}RenderedCascadeBiasMin[{i}]", renderedCascadeBiasMins[i]);
                program.Uniform($"{flatPrefix}RenderedCascadeBiasMax[{i}]", renderedCascadeBiasMaxes[i]);
                program.Uniform($"{flatPrefix}RenderedCascadeReceiverOffsets[{i}]", renderedCascadeReceiverOffsets[i]);
                program.Uniform($"{flatPrefix}RenderedCascadeMatrices[{i}]", renderedCascadeMatrices[i]);
                program.Uniform($"{flatPrefix}RenderedCascadeStaleAge[{i}]", renderedCascadeStaleAges[i]);
            }

            program.Uniform("DebugCascadeColors", _debugCascadeColors);
            program.Uniform("DirectionalShadowAtlasMaxStaleFrames", (float)RuntimeEngine.Rendering.Settings.MaxDirectionalCascadeAtlasStaleFrames);

            program.Uniform($"{basePrefix}Color", _color);
            program.Uniform($"{basePrefix}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{basePrefix}AmbientIntensity", 0.05f);
            program.Uniform($"{basePrefix}WorldToLightSpaceProjMatrix", lightViewProj);

            // Note: Shadow map sampler is bound by the caller (deferred pass or forward lighting collection)
            // to avoid overwriting material texture units.
        }

        /// <summary>
        /// Sets the shadow map related uniforms for the directional light.
        /// </summary>
        /// <param name="material">The material for which the shadow map uniforms are being set.</param>
        /// <param name="program">The render program to which the shadow map uniforms will be set.</param>
        protected override void SetShadowMapUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            base.SetShadowMapUniforms(material, program);

            // Resolve the appropriate shadow map format for the directional light.
            ShadowMapFormatSelection selection = ResolveDirectionalSamplingShadowMapFormat();
            program.Uniform("ShadowDepthSourceMode", 1);
            program.Uniform("ShadowMapEncoding", (int)selection.Encoding);
            program.Uniform("ShadowMomentMinVariance", ShadowMomentMinVariance);
            program.Uniform("ShadowMomentLightBleedReduction", ShadowMomentLightBleedReduction);
            program.Uniform("ShadowMomentPositiveExponent", selection.PositiveExponent);
            program.Uniform("ShadowMomentNegativeExponent", selection.NegativeExponent);
            program.Uniform("ShadowMomentMipBias", ShadowMomentMipBias);
            program.Uniform("CameraNearZ", ShadowCamera?.NearZ ?? NearZ);
            program.Uniform("CameraFarZ", ShadowCamera?.FarZ ?? MathF.Max(Scale.Z, NearZ + 0.001f));

            // Set the cascade count uniform to zero initially. It will be updated later if cascades are available.
            if (!CanRenderDirectionalCascadesForCurrentBackend())
            {
                program.Uniform("CascadeLayerCount", 0);
                return;
            }

            // Acquire the cascade data lock to safely access the cascade state and set the view-projection matrices for each cascade.
            int cascadeCount;
            lock (_cascadeDataLock)
            {
                DirectionalCascadeSourceState cascadeState = GetCascadeSourceState(ResolveCurrentCascadeRenderSource());
                cascadeCount = Math.Min(cascadeState.Slices.Count, MaxCascadeRenderCount);
                for (int i = 0; i < cascadeCount; i++)
                    program.Uniform(CascadeViewProjectionMatrixUniformNames[i], cascadeState.Slices[i].WorldToLightSpaceMatrix);
            }

            program.Uniform("CascadeLayerCount", cascadeCount);
        }

        /// <summary>
        /// Gets the shadow map material for the directional light with the specified width, height, and depth precision.
        /// </summary>
        /// <param name="width">The width of the shadow map texture.</param>
        /// <param name="height">The height of the shadow map texture.</param>
        /// <param name="precision">The depth precision for the shadow map.</param>
        /// <returns>The material configured for rendering the directional light's shadow map.</returns>
        public override XRMaterial GetShadowMapMaterial(uint width, uint height, EDepthPrecision precision = EDepthPrecision.Int24)
        {
            EShadowMapStorageFormat depthStorageFormat = IsDepthShadowMapStorageFormat(ShadowMapStorageFormat)
                ? ShadowMapStorageFormat
                : DefaultShadowMapStorageFormat;
            ShadowMapTextureFormat depthFormat = GetShadowMapTextureFormat(depthStorageFormat);
            ShadowMapFormatSelection selection = ResolveDirectionalSamplingShadowMapFormat();
            ShadowMapTextureFormat shadowFormat = GetShadowMapTextureFormat(selection.Format.StorageFormat);
            bool momentEncoding = selection.Encoding != EShadowMapEncoding.Depth;
            ETexMinFilter minFilter = selection.Format.RequiresLinearFiltering
                ? (ShadowMomentUseMipmaps ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear)
                : ETexMinFilter.Nearest;
            ETexMagFilter magFilter = selection.Format.RequiresLinearFiltering ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
            XRTexture2D rasterDepth = XRTexture2D.CreateFrameBufferTexture(
                width,
                height,
                depthFormat.InternalFormat,
                depthFormat.PixelFormat,
                depthFormat.PixelType,
                EFrameBufferAttachment.DepthAttachment);
            rasterDepth.Name = GetDirectionalShadowResourceName("PrimaryRasterDepth");
            rasterDepth.MinFilter = ETexMinFilter.Nearest;
            rasterDepth.MagFilter = ETexMagFilter.Nearest;
            rasterDepth.UWrap = ETexWrapMode.ClampToEdge;
            rasterDepth.VWrap = ETexWrapMode.ClampToEdge;
            rasterDepth.SamplerName = "ShadowRasterDepth";

            XRTexture2D shadowColor = XRTexture2D.CreateFrameBufferTexture(
                width,
                height,
                shadowFormat.InternalFormat,
                shadowFormat.PixelFormat,
                shadowFormat.PixelType,
                EFrameBufferAttachment.ColorAttachment0);
            shadowColor.Name = GetDirectionalShadowResourceName("PrimaryColor");
            shadowColor.MinFilter = minFilter;
            shadowColor.MagFilter = magFilter;
            shadowColor.UWrap = ETexWrapMode.ClampToEdge;
            shadowColor.VWrap = ETexWrapMode.ClampToEdge;
            shadowColor.SamplerName = "ShadowMap";
            shadowColor.AutoGenerateMipmaps = momentEncoding && ShadowMomentUseMipmaps;
            shadowColor.SmallestAllowedMipmapLevel = ResolveShadowMomentSmallestAllowedMipmapLevel(momentEncoding, ShadowMomentUseMipmaps, width, height);

            XRTexture[] refs = [rasterDepth, shadowColor];

            // This material is used for rendering to the framebuffer.
            XRMaterial mat = new(refs, new XRShader(EShaderType.Fragment, ShaderHelper.Frag_ShadowMomentOutput));

            // No culling so a light inside geometry still shadows everything around it.
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;

            return mat;
        }

        /// <summary>
        /// Gets the resource name for the directional shadow map with the specified suffix.
        /// </summary>
        /// <param name="suffix">The suffix to append to the directional shadow resource name.</param>
        /// <returns>The full resource name for the directional shadow map.</returns>
        private string GetDirectionalShadowResourceName(string suffix)
            => $"DirectionalShadow.{ID:N}.{suffix}";

        /// <summary>
        /// Resolves the smallest allowed mipmap level for the shadow moment texture based on the encoding, mipmap usage, and texture dimensions.
        /// </summary>
        /// <param name="momentEncoding">Indicates whether the shadow moment texture uses moment encoding.</param>
        /// <param name="useMipmaps">Indicates whether mipmaps are used for the shadow moment texture.</param>
        /// <param name="width">The width of the shadow moment texture.</param>
        /// <param name="height">The height of the shadow moment texture.</param>
        /// <returns>The smallest allowed mipmap level for the shadow moment texture.</returns>
        private static int ResolveShadowMomentSmallestAllowedMipmapLevel(bool momentEncoding, bool useMipmaps, uint width, uint height)
            => momentEncoding && useMipmaps
                ? XRTexture.GetSmallestMipmapLevel(width, height)
                : 1000;

        /// <summary>
        /// Resolves the appropriate shadow map format for directional sampling based on the current settings.
        /// </summary>
        /// <returns>The selected shadow map format for directional sampling.</returns>
        private ShadowMapFormatSelection ResolveDirectionalSamplingShadowMapFormat()
            => ResolveShadowMapFormat(preferredStorageFormat: null);

        /// <summary>
        /// Determines whether the current directional shadow backend is using Vulkan.
        /// </summary>
        /// <returns>True if the current directional shadow backend is using Vulkan; otherwise, false.</returns>
        private static bool IsVulkanDirectionalShadowBackend()
            => RuntimeEngine.Rendering.State.IsVulkan ||
               RuntimeEngine.Rendering.IsVulkanRendererActive();

        /// <summary>
        /// Determines whether the Vulkan raster depth receiver texture should be used for the current directional shadow setup.
        /// </summary>
        /// <returns>True if the Vulkan raster depth receiver texture should be used; otherwise, false.</returns>
        private bool ShouldUseVulkanRasterDepthReceiverTexture()
            => IsVulkanDirectionalShadowBackend() &&
               ResolveDirectionalSamplingShadowMapFormat().Encoding == EShadowMapEncoding.Depth;

        /// <summary>
        /// Gets the primary shadow receiver texture for the current directional light setup.
        /// </summary>
        /// <returns>The primary shadow receiver texture if available; otherwise, null.</returns>
        internal XRTexture? PrimaryShadowReceiverTexture
            => FindShadowMapMaterialTexture(
                ShouldUseVulkanRasterDepthReceiverTexture()
                    ? "ShadowRasterDepth"
                    : "ShadowMap");

        /// <summary>
        /// Finds the shadow map texture in the material by its sampler name.
        /// </summary>
        /// <param name="samplerName">The name of the sampler to look for in the material's textures.</param>
        /// <returns>The shadow map texture if found; otherwise, null.</returns>
        private XRTexture? FindShadowMapMaterialTexture(string samplerName)
        {
            if (!CastsShadows ||
                ShadowMap?.Material?.Textures is not { } textures)
                return null;

            for (int i = 0; i < textures.Count; i++)
                if (textures[i]?.SamplerName == samplerName)
                    return textures[i];

            return null;
        }

        /// <summary>
        /// Gets the clear color for the shadow map of the current directional light setup.
        /// </summary>
        /// <returns>The clear color for the shadow map.</returns>
        protected override ColorF4 GetShadowMapClearColor()
        {
            ShadowMapFormatSelection selection = ResolveDirectionalSamplingShadowMapFormat();
            Vector4 clear = selection.ClearSentinel.Value;
            return new ColorF4(clear.X, clear.Y, clear.Z, clear.W);
        }

        /// <summary>
        /// Called when a property of the directional light component changes.
        /// </summary>
        /// <typeparam name="T">The type of the property that changed.</typeparam>
        /// <param name="propName">The name of the property that changed.</param>
        /// <param name="prev">The previous value of the property.</param>
        /// <param name="field">The new value of the property.</param>
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Transform):
                    _shadowCameraTransform?.Parent = Transform;
                    break;
                case nameof(Scale):
                    MeshCenterAdjustMatrix = Matrix4x4.CreateScale(Scale);
                    _shadowCameraTransform?.Translation = Globals.Backward * Scale.Z * 0.5f;
                    if (ShadowCamera is not null)
                    {
                        if (ShadowCamera.Parameters is not XROrthographicCameraParameters p)
                        {
                            XROrthographicCameraParameters parameters = new(Scale.X, Scale.Y, NearZ, Scale.Z - NearZ);
                            parameters.SetOriginPercentages(0.5f, 0.5f);
                            ShadowCamera.Parameters = parameters;
                        }
                        else
                        {
                            p.Width = Scale.X;
                            p.Height = Scale.Y;
                            p.FarZ = Scale.Z - NearZ;
                            p.NearZ = NearZ;
                        }
                    }
                    break;
                case nameof(CascadeCount):
                    EnsureCascadeShadowResources();
                    break;
                case nameof(EnableCascadedShadows):
                    ClearDirectionalAtlasSlots();
                    if (EnableCascadedShadows)
                        EnsureCascadeShadowResources();
                    else
                        ClearCascadeShadows();
                    EnsureShadowMapForActiveDynamicLight();
                    break;
                case nameof(ShadowMapStorageFormat):
                case nameof(ShadowMapEncoding):
                case nameof(ShadowMomentUseMipmaps):
                    EnsureCascadeShadowResources();
                    break;
                case nameof(CastsShadows):
                    if (CastsShadows)
                        EnsureCascadeShadowResources();
                    else
                        ReleaseCascadeShadowResources();
                    break;
            }
        }
    }
}
