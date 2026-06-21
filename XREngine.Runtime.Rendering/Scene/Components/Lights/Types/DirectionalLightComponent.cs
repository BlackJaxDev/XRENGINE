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

        /// <summary>
        /// Creates a directional light with tuned default shadow filtering and contact-shadow settings.
        /// </summary>
        public DirectionalLightComponent()
            : base(EShadowMapStorageFormat.Depth24)
        {
            _cascadeAabbView = new(this);

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

        protected override EShadowMapStorageFormat DefaultShadowMapStorageFormat => EShadowMapStorageFormat.Depth24;

        public override bool SupportsShadowMapStorageFormat(EShadowMapStorageFormat format)
            => IsDepthShadowMapStorageFormat(format);

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

        private static float ResolveFiniteCascadeDistanceLimit(float current, float candidate)
            => float.IsFinite(candidate) && candidate > 0.0f
                ? MathF.Min(current, candidate)
                : current;

        public static XRMesh GetVolumeMesh()
            => XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f));

        protected override XRMesh GetWireframeMesh()
            => XRMesh.Shapes.WireframeBox(new Vector3(-0.5f), new Vector3(0.5f));

        protected override XRCameraParameters GetCameraParameters()
        {
            XROrthographicCameraParameters parameters = new(Scale.X, Scale.Y, NearZ, Scale.Z - NearZ);
            parameters.SetOriginPercentages(0.5f, 0.5f);
            return parameters;
        }

        [Browsable(false)]
        public override Vector4 ShadowBiasProjectionParameters
            => GetPrimaryShadowBiasProjectionParameters();

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

        protected override TransformBase GetShadowCameraParentTransform()
            => ShadowCameraTransform;

        private Transform? _shadowCameraTransform;
        private Transform ShadowCameraTransform => _shadowCameraTransform ??= new Transform()
        {
            Parent = Transform,
            Order = XREngine.Animation.ETransformOrder.TRS,
            Translation = Globals.Backward * Scale.Z * 0.5f,
        };

        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();
            _shadowCameraTransform?.Parent = Transform;
        }

        protected override void RegisterDynamicLight(IRuntimeRenderWorld world)
            => world.Lights.DynamicDirectionalLights.Add(this);

        protected override void UnregisterDynamicLight(IRuntimeRenderWorld world)
            => world.Lights.DynamicDirectionalLights.Remove(this);

        /// <summary>
        /// Publishes both the legacy flat uniforms and the structured ForwardLighting uniforms.
        /// </summary>
        public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            base.SetUniforms(program, targetStructName);

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
            CopyPublishedCascadeUniformData(
                cascadeSplits,
                cascadeBlendWidths,
                cascadeBiasMins,
                cascadeBiasMaxes,
                cascadeReceiverOffsets,
                cascadeMatrices,
                out int cascadeCount);

            program.Uniform($"{flatPrefix}CascadeCount", cascadeCount);
            if (IsVulkanDirectionalShadowBackend())
            {
                program.Uniform($"{flatPrefix}CascadeSplits", cascadeSplits);
                program.Uniform($"{flatPrefix}CascadeBlendWidths", cascadeBlendWidths);
                program.Uniform($"{flatPrefix}CascadeBiasMin", cascadeBiasMins);
                program.Uniform($"{flatPrefix}CascadeBiasMax", cascadeBiasMaxes);
                program.Uniform($"{flatPrefix}CascadeReceiverOffsets", cascadeReceiverOffsets);
                program.Uniform($"{flatPrefix}CascadeMatrices", cascadeMatrices);
            }

            for (int i = 0; i < MaxCascadeRenderCount; i++)
            {
                program.Uniform($"{flatPrefix}CascadeSplits[{i}]", cascadeSplits[i]);
                program.Uniform($"{flatPrefix}CascadeBlendWidths[{i}]", cascadeBlendWidths[i]);
                program.Uniform($"{flatPrefix}CascadeBiasMin[{i}]", cascadeBiasMins[i]);
                program.Uniform($"{flatPrefix}CascadeBiasMax[{i}]", cascadeBiasMaxes[i]);
                program.Uniform($"{flatPrefix}CascadeReceiverOffsets[{i}]", cascadeReceiverOffsets[i]);
                program.Uniform($"{flatPrefix}CascadeMatrices[{i}]", cascadeMatrices[i]);
            }

            program.Uniform("DebugCascadeColors", _debugCascadeColors);

            program.Uniform($"{basePrefix}Color", _color);
            program.Uniform($"{basePrefix}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{basePrefix}AmbientIntensity", 0.05f);
            program.Uniform($"{basePrefix}WorldToLightSpaceProjMatrix", lightViewProj);
            // Note: Shadow map sampler is bound by the caller (deferred pass or forward lighting collection)
            // to avoid overwriting material texture units.
        }

        protected override void SetShadowMapUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            base.SetShadowMapUniforms(material, program);

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

            int cascadeCount;
            lock (_cascadeDataLock)
            {
                cascadeCount = Math.Min(_cascadeShadowSlices.Count, MaxCascadeRenderCount);
                for (int i = 0; i < cascadeCount; i++)
                    program.Uniform(CascadeViewProjectionMatrixUniformNames[i], _cascadeShadowSlices[i].WorldToLightSpaceMatrix);
            }

            program.Uniform("CascadeLayerCount", cascadeCount);
        }

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
            XRTexture[] refs =
            [
                new XRTexture2D(width, height, depthFormat.InternalFormat, depthFormat.PixelFormat, depthFormat.PixelType)
                {
                    Name = GetDirectionalShadowResourceName("PrimaryRasterDepth"),
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                    SamplerName = "ShadowRasterDepth",
                },
                new XRTexture2D(width, height, shadowFormat.InternalFormat, shadowFormat.PixelFormat, shadowFormat.PixelType)
                {
                    Name = GetDirectionalShadowResourceName("PrimaryColor"),
                    MinFilter = minFilter,
                    MagFilter = magFilter,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
                    SamplerName = "ShadowMap",
                    AutoGenerateMipmaps = momentEncoding && ShadowMomentUseMipmaps,
                    SmallestAllowedMipmapLevel = ResolveShadowMomentSmallestAllowedMipmapLevel(momentEncoding, ShadowMomentUseMipmaps, width, height),
                },
            ];

            // This material is used for rendering to the framebuffer.
            XRMaterial mat = new(refs, new XRShader(EShaderType.Fragment, ShaderHelper.Frag_ShadowMomentOutput));

            // No culling so a light inside geometry still shadows everything around it.
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;

            return mat;
        }

        private string GetDirectionalShadowResourceName(string suffix)
            => $"DirectionalShadow.{ID:N}.{suffix}";

        private static int ResolveShadowMomentSmallestAllowedMipmapLevel(bool momentEncoding, bool useMipmaps, uint width, uint height)
            => momentEncoding && useMipmaps
                ? XRTexture.GetSmallestMipmapLevel(width, height)
                : 1000;

        private ShadowMapFormatSelection ResolveDirectionalSamplingShadowMapFormat()
            => ResolveShadowMapFormat(preferredStorageFormat: null);

        private static bool IsVulkanDirectionalShadowBackend()
            => RuntimeEngine.Rendering.State.IsVulkan ||
               RuntimeEngine.Rendering.IsVulkanRendererActive();

        private bool ShouldUseVulkanRasterDepthReceiverTexture()
            => IsVulkanDirectionalShadowBackend() &&
               ResolveDirectionalSamplingShadowMapFormat().Encoding == EShadowMapEncoding.Depth;

        internal XRTexture? PrimaryShadowReceiverTexture
            => FindShadowMapMaterialTexture(
                ShouldUseVulkanRasterDepthReceiverTexture()
                    ? "ShadowRasterDepth"
                    : "ShadowMap");

        private XRTexture? FindShadowMapMaterialTexture(string samplerName)
        {
            if (!CastsShadows ||
                ShadowMap?.Material?.Textures is not { } textures)
            {
                return null;
            }

            for (int i = 0; i < textures.Count; i++)
                if (textures[i]?.SamplerName == samplerName)
                    return textures[i];

            return null;
        }

        protected override ColorF4 GetShadowMapClearColor()
        {
            ShadowMapFormatSelection selection = ResolveDirectionalSamplingShadowMapFormat();
            Vector4 clear = selection.ClearSentinel.Value;
            return new ColorF4(clear.X, clear.Y, clear.Z, clear.W);
        }

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
