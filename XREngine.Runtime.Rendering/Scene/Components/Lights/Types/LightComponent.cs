using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Shadows;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine.Components.Capture.Lights.Types
{
    public abstract class LightComponent : XRComponent, IRenderable
    {
        public const int MaxVogelTapCount = 32;
        public const float MaxAutomaticContactHardeningLightRadius = 0.25f;

        public readonly record struct FrustumIntersectionAabb(int FrustumIndex, Vector3 Min, Vector3 Max);
        public readonly record struct ShadowMapTextureFormat(
            EPixelInternalFormat InternalFormat,
            EPixelFormat PixelFormat,
            EPixelType PixelType,
            ESizedInternalFormat SizedInternalFormat);

        protected ColorF3 _color = new(1.0f, 1.0f, 1.0f);
        protected float _diffuseIntensity = 1.0f;
        private XRMaterialFrameBuffer? _shadowMap = null;
        private ELightType _type = ELightType.Dynamic;
        private bool _castsShadows = true;
        private float _shadowMaxBias = 0.004f;
        private float _shadowMinBias = 0.00001f;
        private float _shadowExponent = 1.221f;
        private float _shadowExponentBase = 0.035f;
        private Matrix4x4 _lightMatrix = Matrix4x4.Identity;
        private Matrix4x4 _meshCenterAdjustMatrix = Matrix4x4.Identity;
        private readonly RenderCommandMesh3D _shadowVolumeRC = new((int)EDefaultRenderPass.OpaqueForward);
        private readonly List<FrustumIntersectionAabb> _cameraIntersections = new(6);
        private bool _previewBoundingVolume = false;
        private IRuntimeRenderWorld? _registeredDynamicWorld;
        private int _filterSamples = 4;
        private int _blockerSamples = 4;
        private int _vogelTapCount = 5;
        private float _filterRadius = 0.0012f;
        private float _blockerSearchRadius = 0.0012f;
        private float _minPenumbra = 0.0002f;
        private float _maxPenumbra = 0.0048f;
        private ESoftShadowMode _softShadowMode = ESoftShadowMode.FixedPoisson;
        private float _lightSourceRadius = 0.01f;
        private bool _useLightRadiusForContactHardening = true;
        private bool _enableCascadedShadows = true;
        private bool _enableContactShadows = true;
        private float _contactShadowDistance = 0.1f;
        private int _contactShadowSamples = 4;
        private float _contactShadowThickness = 0.25f;
        private float _contactShadowFadeStart = 10.0f;
        private float _contactShadowFadeEnd = 40.0f;
        private float _contactShadowNormalOffset = 0.0f;
        private float _contactShadowJitterStrength = 1.0f;
        private int _shadowDebugMode = 0;
        private EShadowMapStorageFormat _shadowMapStorageFormat;
        private EShadowMapEncoding _shadowMapEncoding = EShadowMapEncoding.Depth;
        private float _shadowMomentMinVariance = ShadowMapResourceFactory.DefaultMomentMinVariance;
        private float _shadowMomentLightBleedReduction = ShadowMapResourceFactory.DefaultMomentLightBleedReduction;
        private float _shadowMomentPositiveExponent = ShadowMapResourceFactory.DefaultEvsmPositiveExponent;
        private float _shadowMomentNegativeExponent = ShadowMapResourceFactory.DefaultEvsmNegativeExponent;
        private int _shadowMomentBlurRadiusTexels;
        private int _shadowMomentBlurPasses;
        private float _shadowMomentMipBias;
        private bool _shadowMomentUseMipmaps;
        private bool _shadowMapEncodingDemotionLogged;
        private EShadowMapEncoding _lastLoggedRequestedShadowMapEncoding;
        private EShadowMapEncoding _lastLoggedResolvedShadowMapEncoding;
        private ShadowRequestDiagnostic _shadowAtlasDiagnostic;

        private long _lastMovedTicks;
        private uint _movementVersion = 0;

        internal static float TimeSinceLastMovementSeconds(long currentTicks, long lastMovedTicks)
            => RuntimeTiming.TicksToSeconds(Math.Max(0L, currentTicks - lastMovedTicks));

        /// <summary>
        /// Increments whenever the light's render transform changes.
        /// Used by systems that want to react when the light has moved.
        /// </summary>
        [Browsable(false)]
        public uint MovementVersion => _movementVersion;

        /// <summary>
        /// Seconds since the last observed movement of this light.
        /// </summary>
        [Browsable(false)]
        public float TimeSinceLastMovement
            => World is null ? 0.0f : TimeSinceLastMovementSeconds(Engine.ElapsedTicks, _lastMovedTicks);

        /// <summary>
        /// This matrix is the location of the center of the light source. Used for rendering the light mesh.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 MeshCenterAdjustMatrix
        {
            get => _meshCenterAdjustMatrix;
            protected set => SetField(ref _meshCenterAdjustMatrix, value);
        }

        [Browsable(false)]
        public Matrix4x4 LightMeshMatrix => _lightMatrix;

        private void UpdateLightMatrix(Matrix4x4 renderMatrix)
        {
            _lightMatrix = MeshCenterAdjustMatrix * renderMatrix;

            if (World is not null)
                _shadowVolumeRC.WorldMatrix = _lightMatrix;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(CastsShadows):
                    if (CastsShadows)
                        SetShadowMapResolution(ShadowMapResolutionWidth, ShadowMapResolutionHeight);
                    else
                    {
                        ShadowMap?.Destroy();
                        ShadowMap = null;
                    }
                    break;
                case nameof(ShadowMapStorageFormat):
                    if (CastsShadows)
                        RecreateShadowMap();
                    break;
                case nameof(ShadowMapEncoding):
                    _shadowMapEncodingDemotionLogged = false;
                    if (CastsShadows)
                        RecreateShadowMap();
                    break;
                case nameof(ShadowMomentUseMipmaps):
                    if (CastsShadows && ShadowMapEncoding != EShadowMapEncoding.Depth)
                        RecreateShadowMap();
                    break;
                case nameof(MeshCenterAdjustMatrix):
                    if (SceneNode is not null && !SceneNode.IsTransformNull)
                        UpdateLightMatrix(Transform.RenderMatrix);
                    break;
                case nameof(ShadowMap):
                    if (prev is XRMaterialFrameBuffer previousShadowMap && previousShadowMap.Material is not null)
                        previousShadowMap.Material.SettingShadowUniforms -= SetShadowMapUniforms;

                    if (ShadowMap?.Material is not null)
                        ShadowMap.Material.SettingShadowUniforms += SetShadowMapUniforms;
                    break;
                case nameof(World):
                    SyncDynamicWorldRegistration();
                    if (World is not null)
                        _shadowVolumeRC.WorldMatrix = _lightMatrix;
                    break;
                case nameof(Type):
                    if (Type == ELightType.Dynamic)
                        SyncDynamicWorldRegistration();
                    else
                        ClearDynamicWorldRegistration();
                    break;
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            EnsureShadowMapForActiveDynamicLight();
            SyncDynamicWorldRegistration();
        }

        protected void EnsureShadowMapForActiveDynamicLight()
        {
            if (Type != ELightType.Dynamic || !CastsShadows || ShadowMap is not null)
                return;

            SetShadowMapResolution(
                Math.Max(1u, ShadowMapResolutionWidth),
                Math.Max(1u, ShadowMapResolutionHeight));
        }

        protected virtual void RegisterDynamicLight(IRuntimeRenderWorld world)
        {
        }

        protected virtual void UnregisterDynamicLight(IRuntimeRenderWorld world)
        {
        }

        private void SyncDynamicWorldRegistration()
        {
            IRuntimeRenderWorld? currentWorld = WorldAs<IRuntimeRenderWorld>();

            if (_registeredDynamicWorld is not null &&
                (_registeredDynamicWorld != currentWorld || Type != ELightType.Dynamic || !IsActiveInHierarchy))
            {
                UnregisterDynamicLight(_registeredDynamicWorld);
                _registeredDynamicWorld = null;
            }

            if (Type != ELightType.Dynamic || !IsActiveInHierarchy || currentWorld is null || ReferenceEquals(_registeredDynamicWorld, currentWorld))
                return;

            RegisterDynamicLight(currentWorld);
            _registeredDynamicWorld = currentWorld;
        }

        private void ClearDynamicWorldRegistration()
        {
            if (_registeredDynamicWorld is null)
                return;

            UnregisterDynamicLight(_registeredDynamicWorld);
            _registeredDynamicWorld = null;
        }

        /// <summary>
        /// Override this method to set any additional uniforms needed for the shadow map material.
        /// </summary>
        /// <param name="base"></param>
        /// <param name="program"></param>
        protected virtual void SetShadowMapUniforms(XRMaterialBase @base, XRRenderProgram program) { }

        protected LightComponent(EShadowMapStorageFormat shadowMapStorageFormat) : base()
        {
            _shadowMapStorageFormat = shadowMapStorageFormat;
            _lastMovedTicks = 0L;

            RenderInfo = RenderInfo3D.New(this, _shadowVolumeRC);
            RenderInfo.IsVisible = _previewBoundingVolume;
            RenderInfo.CastsShadows = false;
            RenderInfo.ReceivesShadows = false;
            RenderInfo.VisibleInLightingProbes = false;
            RenderedObjects = [RenderInfo];
        }

        public LightComponent()
            : this(EShadowMapStorageFormat.R16Float) { }

        private void EnsurePreviewVolumeMesh()
        {
            if (_shadowVolumeRC.Mesh is not null)
                return;

            XRMaterial mat = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.0f, 1.0f, 0.0f, 0.0f));
            mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
            mat.RenderOptions.DepthTest.Enabled = Rendering.Models.Materials.ERenderParamUsage.Disabled;
            _shadowVolumeRC.Mesh = new XRMeshRenderer(GetWireframeMesh(), mat);
        }

        protected abstract XRMesh GetWireframeMesh();

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            if (World is not null)
                _lastMovedTicks = Engine.ElapsedTicks;
            unchecked { _movementVersion++; }
            UpdateLightMatrix(renderMatrix);
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        public XRMaterialFrameBuffer? ShadowMap
        {
            get => _shadowMap;
            protected set => SetField(ref _shadowMap, value);
        }

        [Browsable(false)]
        public ShadowRequestDiagnostic ShadowAtlasDiagnostic => _shadowAtlasDiagnostic;

        internal void SetShadowAtlasDiagnostic(ShadowRequestDiagnostic diagnostic)
            => SetField(ref _shadowAtlasDiagnostic, diagnostic, nameof(ShadowAtlasDiagnostic));

        public ShadowRequestKey CreateShadowRequestKey(
            EShadowProjectionType projectionType,
            int faceOrCascadeIndex,
            EShadowMapEncoding encoding = EShadowMapEncoding.Depth,
            ShadowRequestDomain domain = ShadowRequestDomain.Live)
            => new(ID, domain, projectionType, faceOrCascadeIndex, encoding);

        [Category("Shadows")]
        public bool CastsShadows
        {
            get => _castsShadows;
            set => SetField(ref _castsShadows, value);
        }

        [Category("Shadows")]
        [DisplayName("Shadow Map Format")]
        [Description("GPU storage format used by this light's sampled shadow map.")]
        public EShadowMapStorageFormat ShadowMapStorageFormat
        {
            get => _shadowMapStorageFormat;
            set => SetField(ref _shadowMapStorageFormat, NormalizeShadowMapStorageFormat(value));
        }

        [Category("Shadow Filtering")]
        [DisplayName("Shadow Map Encoding")]
        [Description("Controls what the sampled shadow map stores. Depth preserves the existing depth-compare filter path; VSM/EVSM moment encodings use moment filtering settings.")]
        public EShadowMapEncoding ShadowMapEncoding
        {
            get => _shadowMapEncoding;
            set => SetField(ref _shadowMapEncoding, value);
        }

        [Category("Shadow Filtering")]
        [DisplayName("Moment Min Variance")]
        [Description("Minimum variance floor used by VSM/EVSM moment visibility to reduce acne.")]
        public float ShadowMomentMinVariance
        {
            get => _shadowMomentMinVariance;
            set => SetField(ref _shadowMomentMinVariance, MathF.Max(0.0f, value));
        }

        [Category("Shadow Filtering")]
        [DisplayName("Moment Light Bleed Reduction")]
        [Description("Reduces VSM/EVSM light bleeding by remapping low Chebyshev probabilities toward shadow.")]
        public float ShadowMomentLightBleedReduction
        {
            get => _shadowMomentLightBleedReduction;
            set => SetField(ref _shadowMomentLightBleedReduction, Math.Clamp(value, 0.0f, 0.999f));
        }

        [Category("Shadow Filtering")]
        [DisplayName("Moment Positive Exponent")]
        [Description("Positive EVSM exponent. It is clamped by the selected moment texture format.")]
        public float ShadowMomentPositiveExponent
        {
            get => _shadowMomentPositiveExponent;
            set => SetField(ref _shadowMomentPositiveExponent, MathF.Max(0.0f, value));
        }

        [Category("Shadow Filtering")]
        [DisplayName("Moment Negative Exponent")]
        [Description("Negative EVSM exponent used by EVSM4. It is clamped by the selected moment texture format.")]
        public float ShadowMomentNegativeExponent
        {
            get => _shadowMomentNegativeExponent;
            set => SetField(ref _shadowMomentNegativeExponent, MathF.Max(0.0f, value));
        }

        [Category("Shadow Filtering")]
        [DisplayName("Moment Blur Radius Texels")]
        [Description("Optional separable moment blur radius in texels. Zero disables moment blur.")]
        public int ShadowMomentBlurRadiusTexels
        {
            get => _shadowMomentBlurRadiusTexels;
            set => SetField(ref _shadowMomentBlurRadiusTexels, Math.Clamp(value, 0, 64));
        }

        [Category("Shadow Filtering")]
        [DisplayName("Moment Blur Passes")]
        [Description("Number of separable blur pass pairs for moment maps.")]
        public int ShadowMomentBlurPasses
        {
            get => _shadowMomentBlurPasses;
            set => SetField(ref _shadowMomentBlurPasses, Math.Clamp(value, 0, 8));
        }

        [Category("Shadow Filtering")]
        [DisplayName("Moment Mip Bias")]
        [Description("Mip bias applied when sampling mipmapped moment maps.")]
        public float ShadowMomentMipBias
        {
            get => _shadowMomentMipBias;
            set => SetField(ref _shadowMomentMipBias, float.IsFinite(value) ? value : 0.0f);
        }

        [Category("Shadow Filtering")]
        [DisplayName("Moment Use Mipmaps")]
        [Description("Enables mipmapped sampling for moment maps once the resource path has safe gutters.")]
        public bool ShadowMomentUseMipmaps
        {
            get => _shadowMomentUseMipmaps;
            set => SetField(ref _shadowMomentUseMipmaps, value);
        }

        [Category("Shadows")]
        public float ShadowExponentBase 
        {
            get => _shadowExponentBase;
            set => SetField(ref _shadowExponentBase, value);
        }

        [Category("Shadows")]
        public float ShadowExponent
        {
            get => _shadowExponent;
            set => SetField(ref _shadowExponent, value);
        }

        [Category("Shadows")]
        public float ShadowMinBias
        {
            get => _shadowMinBias;
            set => SetField(ref _shadowMinBias, value);
        }

        [Category("Shadows")]
        public float ShadowMaxBias
        {
            get => _shadowMaxBias;
            set => SetField(ref _shadowMaxBias, value);
        }

        private uint _shadowMapResolutionWidth = 4096u;
        [Category("Shadows")]
        public uint ShadowMapResolutionWidth
        {
            get => _shadowMapResolutionWidth;
            set => SetShadowMapResolution(value, ShadowMapResolutionHeight);
        }

        private uint _shadowMapResolutionHeight = 4096u;
        [Category("Shadows")]
        public uint ShadowMapResolutionHeight
        {
            get => _shadowMapResolutionHeight;
            set => SetShadowMapResolution(ShadowMapResolutionWidth, value);
        }

        public ColorF3 Color
        {
            get => _color;
            set => SetField(ref _color, value);
        }

        public float DiffuseIntensity
        {
            get => _diffuseIntensity;
            set => SetField(ref _diffuseIntensity, value);
        }

        public ELightType Type
        {
            get => _type;
            set => SetField(ref _type, value);
        }

        public RenderInfo3D RenderInfo { get; }
        public RenderInfo[] RenderedObjects { get; }

        /// <summary>
        /// When true, renders a wireframe preview of this light's bounding volume.
        /// </summary>
        [Category("Debug")]
        [DisplayName("Preview Bounding Volume")]
        public bool PreviewBoundingVolume
        {
            get => _previewBoundingVolume;
            set
            {
                if (value)
                    EnsurePreviewVolumeMesh();

                if (SetField(ref _previewBoundingVolume, value))
                    RenderInfo.IsVisible = value || (World is not null && Engine.EditorPreferences.Debug.VisualizeDirectionalLightVolumes);
            }
        }

        /// <summary>
        /// Most recent intersections between the active player camera frustum and this light's shadow frusta.
        /// </summary>
        public IReadOnlyList<FrustumIntersectionAabb> CameraIntersections => _cameraIntersections;
        /// <summary>
        /// True when the active player camera intersects at least one of this light's shadow frusta.
        /// </summary>
        public bool IntersectsActiveCamera => _cameraIntersections.Count > 0;

        public virtual void SetShadowMapResolution(uint width, uint height)
        {
            SetField(ref _shadowMapResolutionWidth, width, nameof(ShadowMapResolutionWidth));
            SetField(ref _shadowMapResolutionHeight, height, nameof(ShadowMapResolutionHeight));

            if (ShadowMap is null)
                ShadowMap = new XRMaterialFrameBuffer(GetShadowMapMaterial(width, height));
            else
                ShadowMap.Resize(width, height);
        }

        protected virtual EShadowMapStorageFormat DefaultShadowMapStorageFormat => EShadowMapStorageFormat.R16Float;

        public virtual bool SupportsShadowMapStorageFormat(EShadowMapStorageFormat format)
            => IsColorShadowMapStorageFormat(format) || IsMomentShadowMapStorageFormat(format);

        protected EShadowMapStorageFormat NormalizeShadowMapStorageFormat(EShadowMapStorageFormat format)
            => SupportsShadowMapStorageFormat(format) ? format : DefaultShadowMapStorageFormat;

        protected virtual void RecreateShadowMap()
        {
            ShadowMap?.Destroy();
            ShadowMap = null;
            SetShadowMapResolution(
                Math.Max(1u, ShadowMapResolutionWidth),
                Math.Max(1u, ShadowMapResolutionHeight));
        }

        protected override void OnComponentDeactivated()
        {
            ClearDynamicWorldRegistration();
            base.OnComponentDeactivated();
            ShadowMap?.Destroy();
            ShadowMap = null;
        }

        public int Samples
        {
            get => FilterSamples;
            set => FilterSamples = value;
        }

        public int FilterSamples
        {
            get => _filterSamples;
            set => SetField(ref _filterSamples, Math.Max(1, value));
        }

        public int BlockerSamples
        {
            get => _blockerSamples;
            set => SetField(ref _blockerSamples, Math.Max(1, value));
        }

        /// <summary>
        /// Tap count used by <see cref="ESoftShadowMode.VogelDisk"/>.
        /// </summary>
        [Category("Shadows")]
        public int VogelTapCount
        {
            get => _vogelTapCount;
            set => SetField(ref _vogelTapCount, Math.Clamp(value, 1, MaxVogelTapCount));
        }

        public float FilterRadius
        {
            get => _filterRadius;
            set => SetField(ref _filterRadius, MathF.Max(0.0f, value));
        }

        public float BlockerSearchRadius
        {
            get => _blockerSearchRadius;
            set => SetField(ref _blockerSearchRadius, MathF.Max(0.0f, value));
        }

        public float MinPenumbra
        {
            get => _minPenumbra;
            set => SetField(ref _minPenumbra, MathF.Max(0.0f, value));
        }

        public float MaxPenumbra
        {
            get => _maxPenumbra;
            set => SetField(ref _maxPenumbra, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Selects the soft shadow technique: Hard (PCF fallback), FixedPoisson (fixed-radius Poisson disk),
        /// VogelDisk (fixed-radius Vogel disk), or ContactHardeningPcss (blocker-search variable penumbra).
        /// </summary>
        public ESoftShadowMode SoftShadowMode
        {
            get => _softShadowMode;
            set => SetField(ref _softShadowMode, value);
        }

        [Browsable(false)]
        public virtual bool SupportsLightRadiusContactHardening => false;

        [Browsable(false)]
        protected virtual float ContactHardeningLightRadius => LightSourceRadius;

        [Browsable(false)]
        public float EffectiveLightSourceRadius
            => UseLightRadiusForContactHardening
                ? ClampedAutomaticContactHardeningLightRadius()
                : LightSourceRadius;

        private float ClampedAutomaticContactHardeningLightRadius()
        {
            float radius = ContactHardeningLightRadius;
            if (!float.IsFinite(radius))
                return 0.0f;

            return Math.Clamp(radius, 0.0f, MaxAutomaticContactHardeningLightRadius);
        }

        [Category("Shadows")]
        [DisplayName("Use Light Radius For Contact Hardening")]
        [Description("Uses a capped light-derived source radius instead of the manual source radius for PCSS/contact-hardening shadows.")]
        public bool UseLightRadiusForContactHardening
        {
            get => _useLightRadiusForContactHardening && SupportsLightRadiusContactHardening;
            set => SetField(ref _useLightRadiusForContactHardening, value && SupportsLightRadiusContactHardening);
        }

        /// <summary>
        /// Physical radius of the light source in world units. Used by <see cref="ESoftShadowMode.ContactHardeningPcss"/>
        /// to compute the penumbra width. Larger values produce wider, softer penumbrae.
        /// </summary>
        [Category("Shadows")]
        public float LightSourceRadius
        {
            get => _lightSourceRadius;
            set => SetField(ref _lightSourceRadius, MathF.Max(0.0f, value));
        }

        public bool EnableCascadedShadows
        {
            get => _enableCascadedShadows;
            set => SetField(ref _enableCascadedShadows, value);
        }

        public bool EnableContactShadows
        {
            get => _enableContactShadows;
            set => SetField(ref _enableContactShadows, value);
        }

        public float ContactShadowDistance
        {
            get => _contactShadowDistance;
            set => SetField(ref _contactShadowDistance, MathF.Max(0.0f, value));
        }

        public int ContactShadowSamples
        {
            get => _contactShadowSamples;
            set => SetField(ref _contactShadowSamples, Math.Clamp(value, 1, 32));
        }

        public float ContactShadowThickness
        {
            get => _contactShadowThickness;
            set => SetField(ref _contactShadowThickness, MathF.Max(0.0f, value));
        }

        public float ContactShadowFadeStart
        {
            get => _contactShadowFadeStart;
            set => SetField(ref _contactShadowFadeStart, MathF.Max(0.0f, value));
        }

        public float ContactShadowFadeEnd
        {
            get => _contactShadowFadeEnd;
            set => SetField(ref _contactShadowFadeEnd, MathF.Max(0.0f, value));
        }

        public float ContactShadowNormalOffset
        {
            get => _contactShadowNormalOffset;
            set => SetField(ref _contactShadowNormalOffset, MathF.Max(0.0f, value));
        }

        public float ContactShadowJitterStrength
        {
            get => _contactShadowJitterStrength;
            set => SetField(ref _contactShadowJitterStrength, Math.Clamp(value, 0.0f, 1.0f));
        }

        /// <summary>
        /// Shadow debug visualisation mode: 0 = off, 1 = shadow-only, 2 = margin heatmap.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Shadow Debug Mode")]
        [Description("0 = normal, 1 = shadow-only (white=lit), 2 = margin heatmap (green=lit margin, red=shadow).")]
        public int ShadowDebugMode
        {
            get => _shadowDebugMode;
            set => SetField(ref _shadowDebugMode, Math.Clamp(value, 0, 2));
        }

        public virtual void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            program.Uniform(Engine.Rendering.Constants.ShadowExponentBaseUniform, ShadowExponentBase);
            program.Uniform(Engine.Rendering.Constants.ShadowExponentUniform, ShadowExponent);
            program.Uniform(Engine.Rendering.Constants.ShadowBiasMinUniform, ShadowMinBias);
            program.Uniform(Engine.Rendering.Constants.ShadowBiasMaxUniform, ShadowMaxBias);

            program.Uniform(Engine.Rendering.Constants.ShadowSamples, FilterSamples);
            program.Uniform(Engine.Rendering.Constants.ShadowBlockerSamples, BlockerSamples);
            program.Uniform(Engine.Rendering.Constants.ShadowFilterSamples, FilterSamples);
            program.Uniform(Engine.Rendering.Constants.ShadowVogelTapCount, VogelTapCount);
            program.Uniform(Engine.Rendering.Constants.ShadowFilterRadius, FilterRadius);
            program.Uniform(Engine.Rendering.Constants.ShadowBlockerSearchRadius, BlockerSearchRadius);
            program.Uniform(Engine.Rendering.Constants.ShadowMinPenumbra, MinPenumbra);
            program.Uniform(Engine.Rendering.Constants.ShadowMaxPenumbra, MaxPenumbra);
            program.Uniform(Engine.Rendering.Constants.SoftShadowMode, (int)SoftShadowMode);
            program.Uniform(Engine.Rendering.Constants.LightSourceRadius, EffectiveLightSourceRadius);

            program.Uniform(Engine.Rendering.Constants.EnableCascadedShadows, EnableCascadedShadows);
            program.Uniform(Engine.Rendering.Constants.EnableContactShadows, EnableContactShadows);
            program.Uniform(Engine.Rendering.Constants.ContactShadowDistance, ContactShadowDistance);
            program.Uniform(Engine.Rendering.Constants.ContactShadowSamples, ContactShadowSamples);
            program.Uniform(Engine.Rendering.Constants.ContactShadowThickness, ContactShadowThickness);
            program.Uniform(Engine.Rendering.Constants.ContactShadowFadeStart, ContactShadowFadeStart);
            program.Uniform(Engine.Rendering.Constants.ContactShadowFadeEnd, ContactShadowFadeEnd);
            program.Uniform(Engine.Rendering.Constants.ContactShadowNormalOffset, ContactShadowNormalOffset);
            program.Uniform(Engine.Rendering.Constants.ContactShadowJitterStrength, ContactShadowJitterStrength);

            program.Uniform("ShadowDebugMode", _shadowDebugMode);
        }

        public abstract XRMaterial GetShadowMapMaterial(uint width, uint height, EDepthPrecision precision = EDepthPrecision.Flt32);

        public abstract void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager lightmapBaking);
        public abstract void CollectVisibleItems();
        public abstract void RenderShadowMap(bool collectVisibleNow = false);

        /// <summary>
        /// Populate a prepared-frustum list representing this light's shadow cameras.
        /// Point lights emit six frusta; directional/spot emit one.
        /// </summary>
        internal abstract void BuildShadowFrusta(List<PreparedFrustum> output);

        internal void UpdateCameraIntersections(in PreparedFrustum cameraFrustum, List<PreparedFrustum> lightFrusta)
        {
            _cameraIntersections.Clear();

            if (lightFrusta.Count == 0)
                return;

            for (int i = 0; i < lightFrusta.Count; i++)
            {
                if (GeoUtil.Intersect.FrustaAsAABB(cameraFrustum, lightFrusta[i], out Vector3 min, out Vector3 max))
                    _cameraIntersections.Add(new FrustumIntersectionAabb(i, min, max));
            }
        }

        public static EPixelInternalFormat GetShadowDepthMapFormat(EDepthPrecision precision)
            => precision switch
            {
                EDepthPrecision.Int16 => EPixelInternalFormat.DepthComponent16,
                EDepthPrecision.Int24 => EPixelInternalFormat.DepthComponent24,
                EDepthPrecision.Int32 => EPixelInternalFormat.DepthComponent32,
                _ => EPixelInternalFormat.DepthComponent32f,
            };

        public static bool IsColorShadowMapStorageFormat(EShadowMapStorageFormat format)
            => format is EShadowMapStorageFormat.R8UNorm
                or EShadowMapStorageFormat.R16UNorm
                or EShadowMapStorageFormat.R16Float
                or EShadowMapStorageFormat.R32Float;

        public static bool IsMomentShadowMapStorageFormat(EShadowMapStorageFormat format)
            => format is EShadowMapStorageFormat.RG16Float
                or EShadowMapStorageFormat.RG32Float
                or EShadowMapStorageFormat.RGBA16Float
                or EShadowMapStorageFormat.RGBA32Float;

        public static bool IsDepthShadowMapStorageFormat(EShadowMapStorageFormat format)
            => format is EShadowMapStorageFormat.Depth16
                or EShadowMapStorageFormat.Depth24
                or EShadowMapStorageFormat.Depth32Float;

        public static ShadowMapTextureFormat GetShadowMapTextureFormat(EShadowMapStorageFormat format)
            => format switch
            {
                EShadowMapStorageFormat.R8UNorm => new(
                    EPixelInternalFormat.R8,
                    EPixelFormat.Red,
                    EPixelType.UnsignedByte,
                    ESizedInternalFormat.R8),
                EShadowMapStorageFormat.R16UNorm => new(
                    EPixelInternalFormat.R16,
                    EPixelFormat.Red,
                    EPixelType.UnsignedShort,
                    ESizedInternalFormat.R16),
                EShadowMapStorageFormat.R32Float => new(
                    EPixelInternalFormat.R32f,
                    EPixelFormat.Red,
                    EPixelType.Float,
                    ESizedInternalFormat.R32f),
                EShadowMapStorageFormat.RG16Float => new(
                    EPixelInternalFormat.RG16f,
                    EPixelFormat.Rg,
                    EPixelType.HalfFloat,
                    ESizedInternalFormat.Rg16f),
                EShadowMapStorageFormat.RG32Float => new(
                    EPixelInternalFormat.RG32f,
                    EPixelFormat.Rg,
                    EPixelType.Float,
                    ESizedInternalFormat.Rg32f),
                EShadowMapStorageFormat.RGBA16Float => new(
                    EPixelInternalFormat.Rgba16f,
                    EPixelFormat.Rgba,
                    EPixelType.HalfFloat,
                    ESizedInternalFormat.Rgba16f),
                EShadowMapStorageFormat.RGBA32Float => new(
                    EPixelInternalFormat.Rgba32f,
                    EPixelFormat.Rgba,
                    EPixelType.Float,
                    ESizedInternalFormat.Rgba32f),
                EShadowMapStorageFormat.Depth16 => new(
                    EPixelInternalFormat.DepthComponent16,
                    EPixelFormat.DepthComponent,
                    EPixelType.UnsignedShort,
                    ESizedInternalFormat.DepthComponent16),
                EShadowMapStorageFormat.Depth24 => new(
                    EPixelInternalFormat.DepthComponent24,
                    EPixelFormat.DepthComponent,
                    EPixelType.UnsignedInt,
                    ESizedInternalFormat.DepthComponent24),
                EShadowMapStorageFormat.Depth32Float => new(
                    EPixelInternalFormat.DepthComponent32f,
                    EPixelFormat.DepthComponent,
                    EPixelType.Float,
                    ESizedInternalFormat.DepthComponent32f),
                _ => new(
                    EPixelInternalFormat.R16f,
                    EPixelFormat.Red,
                    EPixelType.HalfFloat,
                    ESizedInternalFormat.R16f),
            };

        public ShadowMapFormatSelection ResolveShadowMapFormat(
            IShadowMapFormatCapabilities? capabilities = null,
            EShadowMapStorageFormat? preferredStorageFormat = null,
            ShadowDepthDirection depthDirection = ShadowDepthDirection.Normal)
        {
            ShadowMapFormatSelection selection = ShadowMapResourceFactory.SelectFormat(
                ShadowMapEncoding,
                capabilities,
                ShadowMomentPositiveExponent,
                ShadowMomentNegativeExponent,
                depthDirection,
                preferredStorageFormat);
            LogShadowEncodingDemotion(selection);
            return selection;
        }

        private void LogShadowEncodingDemotion(ShadowMapFormatSelection selection)
        {
            if (!selection.WasDemoted)
                return;

            if (_shadowMapEncodingDemotionLogged &&
                _lastLoggedRequestedShadowMapEncoding == selection.RequestedEncoding &&
                _lastLoggedResolvedShadowMapEncoding == selection.Encoding)
            {
                return;
            }

            _shadowMapEncodingDemotionLogged = true;
            _lastLoggedRequestedShadowMapEncoding = selection.RequestedEncoding;
            _lastLoggedResolvedShadowMapEncoding = selection.Encoding;
            Debug.RenderingWarning($"Shadow map encoding {selection.RequestedEncoding} is unsupported for {GetType().Name}; using {selection.Encoding}.");
        }
    }
}
