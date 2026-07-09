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
    /// <summary>
    /// Shared runtime state for all scene lights, including dynamic-world registration,
    /// preview-volume rendering, shadow-map resources, and shader uniform publication.
    /// </summary>
    public abstract class LightComponent : XRComponent, IRenderable
    {
        /// <summary>
        /// Maximum supported sample count for Vogel-disk soft shadows.
        /// </summary>
        public const int MaxVogelTapCount = 32;

        /// <summary>
        /// Upper bound for automatic PCSS/contact-hardening source radii.
        /// </summary>
        public const float MaxAutomaticContactHardeningLightRadius = 0.25f;

        /// <summary>
        /// A camera/light frustum overlap expressed as an AABB for debug visualization.
        /// </summary>
        public readonly record struct FrustumIntersectionAabb(int FrustumIndex, Vector3 Min, Vector3 Max);

        /// <summary>
        /// Complete GPU texture format tuple used when allocating sampled shadow maps.
        /// </summary>
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
        private float _shadowDepthBiasTexels = 1.0f;
        private float _shadowSlopeBiasTexels = 2.0f;
        private float _shadowNormalBiasTexels = 1.0f;
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
            => World is null ? 0.0f : TimeSinceLastMovementSeconds(RuntimeEngine.ElapsedTicks, _lastMovedTicks);

        /// <summary>
        /// This matrix is the location of the center of the light source. Used for rendering the light mesh.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 MeshCenterAdjustMatrix
        {
            get => _meshCenterAdjustMatrix;
            protected set => SetField(ref _meshCenterAdjustMatrix, value);
        }

        /// <summary>
        /// Gets the transformation matrix for the light mesh.
        /// This matrix combines the mesh center adjustment with the render matrix to position the light mesh correctly.
        /// </summary>
        [Browsable(false)]
        public Matrix4x4 LightMeshMatrix => _lightMatrix;

        /// <summary>
        /// Updates the light's transformation matrix based on the provided render matrix.
        /// </summary>
        /// <param name="renderMatrix">The render matrix to update the light's transformation with.</param>
        private void UpdateLightMatrix(Matrix4x4 renderMatrix)
        {
            _lightMatrix = MeshCenterAdjustMatrix * renderMatrix;

            if (World is not null)
                _shadowVolumeRC.WorldMatrix = _lightMatrix;
        }

        /// <summary>
        /// Called when a property of the light component changes.
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

        /// <summary>
        /// Called when the light component is activated in the scene.
        /// Ensures that the shadow map is set up for active dynamic lights and synchronizes the dynamic world registration.
        /// </summary>
        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            EnsureShadowMapForActiveDynamicLight();
            SyncDynamicWorldRegistration();
        }

        /// <summary>
        /// Determines whether the light component uses only the atlas-based shadow map resource.
        /// </summary>
        /// <returns>True if the light component uses only the atlas-based shadow map resource; otherwise, false.</returns>
        protected virtual bool UsesAtlasOnlyShadowMapResource => false;

        /// <summary>
        /// Ensures that the shadow map is allocated and set up for the active dynamic light.
        /// If the light is not dynamic, does not cast shadows, already has a shadow map, or uses only the atlas-based shadow map resource, this method does nothing.
        /// </summary>
        protected void EnsureShadowMapForActiveDynamicLight()
        {
            if (Type != ELightType.Dynamic || !CastsShadows || ShadowMap is not null || UsesAtlasOnlyShadowMapResource)
                return;

            SetShadowMapResolution(
                Math.Max(1u, ShadowMapResolutionWidth),
                Math.Max(1u, ShadowMapResolutionHeight));
        }

        /// <summary>
        /// Registers the dynamic light with the specified render world.
        /// </summary>
        /// <param name="world">The render world with which to register the dynamic light.</param>
        protected virtual void RegisterDynamicLight(IRuntimeRenderWorld world)
        {
            // Base implementation does nothing. Override this method to register the dynamic light with the render world.
        }

        /// <summary>
        /// Unregisters the dynamic light from the specified render world.
        /// </summary>
        /// <param name="world">The render world from which to unregister the dynamic light.</param>
        protected virtual void UnregisterDynamicLight(IRuntimeRenderWorld world)
        {
            // Base implementation does nothing. Override this method to unregister the dynamic light from the render world.
        }

        /// <summary>
        /// Synchronizes the dynamic light's registration with the current render world.
        /// If the light is no longer active or the render world has changed,
        /// it will unregister the light from the previous world and register it with the current one if applicable.
        /// </summary>
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

        /// <summary>
        /// Clears the dynamic light's registration from the current render world, if it is registered.
        /// </summary>
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
        /// <param name="base">The base material for the shadow map.</param>
        /// <param name="program">The render program used for the shadow map.</param>
        protected virtual void SetShadowMapUniforms(XRMaterialBase @base, XRRenderProgram program)
        {
            // Base implementation does nothing. Override this method in derived classes to set additional uniforms for the shadow map material.
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LightComponent"/> class with the specified shadow map storage format.
        /// </summary>
        /// <param name="shadowMapStorageFormat">The storage format to use for the shadow map.</param>
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

        /// <summary>
        /// Ensures that the preview volume mesh is created and assigned to the shadow volume renderer.
        /// </summary>
        private void EnsurePreviewVolumeMesh()
        {
            if (_shadowVolumeRC.Mesh is not null)
                return;

            XRMaterial mat = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.0f, 1.0f, 0.0f, 0.0f));
            mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
            mat.RenderOptions.DepthTest.Enabled = Rendering.Models.Materials.ERenderParamUsage.Disabled;
            _shadowVolumeRC.Mesh = new XRMeshRenderer(GetWireframeMesh(), mat);
        }

        /// <summary>
        /// Gets the wireframe mesh used for the preview volume.
        /// </summary>
        /// <returns>The wireframe mesh.</returns>
        protected abstract XRMesh GetWireframeMesh();

        /// <summary>
        /// Called when the transform's render world matrix has changed. Updates the light's internal state accordingly.
        /// </summary>
        /// <param name="transform">The transform whose render world matrix has changed.</param>
        /// <param name="renderMatrix">The new render world matrix of the transform.</param>
        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            if (World is not null)
                _lastMovedTicks = RuntimeEngine.ElapsedTicks;
            unchecked { _movementVersion++; }
            UpdateLightMatrix(renderMatrix);
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        /// <summary>
        /// Gets or sets the shadow map associated with this light.
        /// This is the render target used for shadow mapping.
        /// </summary>
        public XRMaterialFrameBuffer? ShadowMap
        {
            get => _shadowMap;
            protected set => SetField(ref _shadowMap, value);
        }

        /// <summary>
        /// Latest atlas allocation diagnostics for this light's shadow request.
        /// </summary>
        [Browsable(false)]
        public ShadowRequestDiagnostic ShadowAtlasDiagnostic => _shadowAtlasDiagnostic;

        /// <summary>
        /// Sets the latest atlas allocation diagnostics for this light's shadow request.
        /// </summary>
        /// <param name="diagnostic">The diagnostic information to set for the shadow atlas allocation.</param>
        internal void SetShadowAtlasDiagnostic(ShadowRequestDiagnostic diagnostic)
            => SetField(ref _shadowAtlasDiagnostic, diagnostic, nameof(ShadowAtlasDiagnostic));

        /// <summary>
        /// Creates a key for requesting a shadow map for this light with the specified parameters.
        /// </summary>
        /// <param name="projectionType">The type of shadow projection to use.</param>
        /// <param name="faceOrCascadeIndex">The index of the face or cascade for the shadow map.</param>
        /// <param name="encoding">The encoding format for the shadow map.</param>
        /// <param name="domain">The domain of the shadow request.</param>
        /// <param name="source">The source of the shadow request.</param>
        /// <returns>A key representing the shadow request for this light.</returns>
        public ShadowRequestKey CreateShadowRequestKey(
            EShadowProjectionType projectionType,
            int faceOrCascadeIndex,
            EShadowMapEncoding encoding = EShadowMapEncoding.Depth,
            ShadowRequestDomain domain = ShadowRequestDomain.Live,
            ShadowRequestSource source = ShadowRequestSource.Default)
            => new(ID, domain, source, projectionType, faceOrCascadeIndex, encoding);

        /// <summary>
        /// Enables live shadow-map creation and rendering for this light.
        /// </summary>
        [Category("Shadows")]
        public bool CastsShadows
        {
            get => _castsShadows;
            set => SetField(ref _castsShadows, value);
        }

        /// <summary>
        /// Gets or sets the GPU storage format used by this light's sampled shadow map.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Shadow Map Format")]
        [Description("GPU storage format used by this light's sampled shadow map.")]
        public EShadowMapStorageFormat ShadowMapStorageFormat
        {
            get => _shadowMapStorageFormat;
            set => SetField(ref _shadowMapStorageFormat, NormalizeShadowMapStorageFormat(value));
        }

        /// <summary>
        /// Gets or sets the encoding format for this light's shadow map.
        /// </summary>
        [Category("Shadow Filtering")]
        [DisplayName("Shadow Map Encoding")]
        [Description("Controls what the sampled shadow map stores. Depth preserves the existing depth-compare filter path; VSM/EVSM moment encodings use moment filtering settings.")]
        public EShadowMapEncoding ShadowMapEncoding
        {
            get => _shadowMapEncoding;
            set => SetField(ref _shadowMapEncoding, value);
        }

        /// <summary>
        /// Gets or sets the minimum variance used by VSM/EVSM moment visibility to reduce shadow acne.
        /// </summary>
        [Category("Shadow Filtering")]
        [DisplayName("Moment Min Variance")]
        [Description("Minimum variance floor used by VSM/EVSM moment visibility to reduce acne.")]
        public float ShadowMomentMinVariance
        {
            get => _shadowMomentMinVariance;
            set => SetField(ref _shadowMomentMinVariance, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the light bleed reduction factor used by VSM/EVSM moment visibility.
        /// </summary>
        [Category("Shadow Filtering")]
        [DisplayName("Moment Light Bleed Reduction")]
        [Description("Reduces VSM/EVSM light bleeding by remapping low Chebyshev probabilities toward shadow.")]
        public float ShadowMomentLightBleedReduction
        {
            get => _shadowMomentLightBleedReduction;
            set => SetField(ref _shadowMomentLightBleedReduction, Math.Clamp(value, 0.0f, 0.999f));
        }

        /// <summary>
        /// Gets or sets the positive exponent used by EVSM moment visibility.
        /// </summary>
        [Category("Shadow Filtering")]
        [DisplayName("Moment Positive Exponent")]
        [Description("Positive EVSM exponent. It is clamped by the selected moment texture format.")]
        public float ShadowMomentPositiveExponent
        {
            get => _shadowMomentPositiveExponent;
            set => SetField(ref _shadowMomentPositiveExponent, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the negative exponent used by EVSM moment visibility.
        /// </summary>
        [Category("Shadow Filtering")]
        [DisplayName("Moment Negative Exponent")]
        [Description("Negative EVSM exponent. It is clamped by the selected moment texture format.")]
        public float ShadowMomentNegativeExponent
        {
            get => _shadowMomentNegativeExponent;
            set => SetField(ref _shadowMomentNegativeExponent, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the approximate moment prefilter radius in texels.
        /// </summary>
        [Category("Shadow Filtering")]
        [DisplayName("Moment Blur Radius Texels")]
        [Description("Approximate moment prefilter radius in texels. Standalone mipmapped spot moment maps convert this to an explicit mip-level contribution; separable blur passes are planned separately.")]
        public int ShadowMomentBlurRadiusTexels
        {
            get => _shadowMomentBlurRadiusTexels;
            set => SetField(ref _shadowMomentBlurRadiusTexels, Math.Clamp(value, 0, 64));
        }

        /// <summary>
        /// Gets or sets the number of separable moment blur passes.
        /// </summary>
        [Category("Shadow Filtering")]
        [DisplayName("Moment Blur Passes")]
        [Description("Reserved for separable moment blur pass pairs. The current standalone spot path uses mipmapped prefiltering.")]
        public int ShadowMomentBlurPasses
        {
            get => _shadowMomentBlurPasses;
            set => SetField(ref _shadowMomentBlurPasses, Math.Clamp(value, 0, 8));
        }

        /// <summary>
        /// Gets or sets the additional explicit mip level used when sampling standalone mipmapped moment maps.
        /// </summary>
        [Category("Shadow Filtering")]
        [DisplayName("Moment Mip Level")]
        [Description("Additional explicit mip level used when sampling standalone mipmapped moment maps. Higher values select wider prefiltered mips.")]
        public float ShadowMomentMipBias
        {
            get => _shadowMomentMipBias;
            set => SetField(ref _shadowMomentMipBias, float.IsFinite(value) ? value : 0.0f);
        }

        /// <summary>
        /// Gets or sets a value indicating whether explicit mipmapped sampling is used for standalone moment maps.
        /// </summary>
        [Category("Shadow Filtering")]
        [DisplayName("Moment Use Mipmaps")]
        [Description("Enables explicit mipmapped sampling for standalone moment maps. Spot moment maps regenerate mips after each shadow render; atlas moment mips remain disabled until gutters are implemented.")]
        public bool ShadowMomentUseMipmaps
        {
            get => _shadowMomentUseMipmaps;
            set => SetField(ref _shadowMomentUseMipmaps, value);
        }

        /// <summary>
        /// Gets or sets the base exponent used for shadow EVSM calculations.
        /// </summary>
        [Category("Shadows")]
        [Browsable(false)]
        public float ShadowExponentBase
        {
            get => _shadowExponentBase;
            set => SetField(ref _shadowExponentBase, value);
        }

        /// <summary>
        /// Gets or sets the exponent used for shadow EVSM calculations.
        /// </summary>
        [Category("Shadows")]
        [Browsable(false)]
        public float ShadowExponent
        {
            get => _shadowExponent;
            set => SetField(ref _shadowExponent, value);
        }

        /// <summary>
        /// Gets or sets the minimum shadow bias.
        /// </summary>
        [Category("Shadows")]
        [Browsable(false)]
        public float ShadowMinBias
        {
            get => _shadowMinBias;
            set => SetField(ref _shadowMinBias, value);
        }

        /// <summary>
        /// Gets or sets the maximum shadow bias.
        /// </summary>
        [Category("Shadows")]
        [Browsable(false)]
        public float ShadowMaxBias
        {
            get => _shadowMaxBias;
            set => SetField(ref _shadowMaxBias, value);
        }

        /// <summary>
        /// Gets or sets the shadow depth bias in texels.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Shadow Depth Bias Texels")]
        [Description("Constant compare-bias floor expressed in shadow-map texels. Live forward/deferred shadow receivers convert this to the active map's depth scale.")]
        public float ShadowDepthBiasTexels
        {
            get => _shadowDepthBiasTexels;
            set => SetField(ref _shadowDepthBiasTexels, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the shadow slope bias in texels.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Shadow Slope Bias Texels")]
        [Description("Receiver-plane slope bias scale, in shadow-map texels. Higher values reduce grazing-angle acne at the cost of more separation.")]
        public float ShadowSlopeBiasTexels
        {
            get => _shadowSlopeBiasTexels;
            set => SetField(ref _shadowSlopeBiasTexels, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the shadow normal offset bias in texels.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Shadow Normal Offset Texels")]
        [Description("Normal-offset distance expressed in shadow-map texels. The live renderer scales this by the active map's world-space texel footprint.")]
        public float ShadowNormalBiasTexels
        {
            get => _shadowNormalBiasTexels;
            set => SetField(ref _shadowNormalBiasTexels, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets the shadow bias parameters as a Vector4, containing depth, slope, and normal bias values.
        /// </summary>
        [Browsable(false)]
        public Vector4 ShadowBiasParameters
            => new(ShadowDepthBiasTexels, ShadowSlopeBiasTexels, ShadowNormalBiasTexels, 0.0f);

        /// <summary>
        /// Gets the shadow bias projection parameters as a Vector4.
        /// </summary>
        [Browsable(false)]
        public virtual Vector4 ShadowBiasProjectionParameters => Vector4.Zero;

        /// <summary>
        /// Gets the desired shadow atlas resolution for this light.
        /// </summary>
        /// <remarks>
        /// The desired resolution is clamped between the minimum and maximum shadow atlas tile resolutions,
        /// and cannot exceed the shadow atlas page size.
        /// </remarks>
        internal uint GetDesiredShadowAtlasResolution()
        {
            uint desired = Math.Max(ShadowMapResolutionWidth, ShadowMapResolutionHeight);
            desired = Math.Max(desired, RuntimeEngine.Rendering.Settings.MinShadowAtlasTileResolution);
            desired = Math.Min(desired, RuntimeEngine.Rendering.Settings.MaxShadowAtlasTileResolution);
            desired = Math.Min(desired, RuntimeEngine.Rendering.Settings.ShadowAtlasPageSize);
            return Math.Max(1u, desired);
        }

        /// <summary>
        /// Gets the scale factor between the desired shadow atlas resolution and the provided sample resolution.
        /// </summary>
        /// <param name="sampleResolution">The sample resolution to compare against the desired shadow atlas resolution.</param>
        /// <returns>The scale factor between the desired shadow atlas resolution and the provided sample resolution.</returns>
        internal float GetShadowAtlasResolutionScale(uint sampleResolution)
            => sampleResolution == 0u ? 1.0f : MathF.Max(1.0f, GetDesiredShadowAtlasResolution() / (float)sampleResolution);

        /// <summary>
        /// Gets the sample resolution for the shadow atlas based on the provided allocation.
        /// </summary>
        /// <param name="allocation">The shadow atlas allocation to determine the sample resolution for.</param>
        /// <returns>The sample resolution for the shadow atlas based on the provided allocation.</returns>
        internal static uint GetShadowAtlasSampleResolution(in ShadowAtlasAllocation allocation)
        {
            int width = allocation.InnerPixelRect.Width > 0 ? allocation.InnerPixelRect.Width : (int)allocation.Resolution;
            int height = allocation.InnerPixelRect.Height > 0 ? allocation.InnerPixelRect.Height : (int)allocation.Resolution;
            return (uint)Math.Max(1, Math.Max(width, height));
        }

        private uint _shadowMapResolutionWidth = 1024u;
        /// <summary>
        /// Width of this light's standalone shadow map. Cubemap lights use the larger width/height as the face size.
        /// </summary>
        [Category("Shadows")]
        public uint ShadowMapResolutionWidth
        {
            get => _shadowMapResolutionWidth;
            set => SetShadowMapResolution(value, ShadowMapResolutionHeight);
        }

        private uint _shadowMapResolutionHeight = 1024u;
        /// <summary>
        /// Height of this light's standalone shadow map. Cubemap lights use the larger width/height as the face size.
        /// </summary>
        [Category("Shadows")]
        public uint ShadowMapResolutionHeight
        {
            get => _shadowMapResolutionHeight;
            set => SetShadowMapResolution(ShadowMapResolutionWidth, value);
        }

        /// <summary>
        /// Linear RGB light color applied before intensity.
        /// </summary>
        [Category("Lighting")]
        public ColorF3 Color
        {
            get => _color;
            set => SetField(ref _color, value);
        }

        /// <summary>
        /// Diffuse lighting multiplier used by forward/deferred light shaders.
        /// </summary>
        [Category("Lighting")]
        public float DiffuseIntensity
        {
            get => _diffuseIntensity;
            set => SetField(ref _diffuseIntensity, value);
        }

        /// <summary>
        /// Controls whether the light is registered as live dynamic lighting or treated as cacheable/static.
        /// </summary>
        [Category("Lighting")]
        public ELightType Type
        {
            get => _type;
            set => SetField(ref _type, value);
        }

        /// <summary>
        /// Render info for the optional wireframe preview volume.
        /// </summary>
        [Category("Debug")]
        public RenderInfo3D RenderInfo { get; }

        /// <summary>
        /// IRenderable payload containing only the preview-volume render info.
        /// </summary>
        [Category("Debug")]
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
                    RenderInfo.IsVisible = value || (World is not null && RuntimeEngine.EditorPreferences.Debug.VisualizeDirectionalLightVolumes);
            }
        }

        /// <summary>
        /// Most recent intersections between the active player camera frustum and this light's shadow frusta.
        /// </summary>
        [Category("Debug")]
        public IReadOnlyList<FrustumIntersectionAabb> CameraIntersections => _cameraIntersections;

        /// <summary>
        /// True when the active player camera intersects at least one of this light's shadow frusta.
        /// </summary>
        [Category("Debug")]
        public bool IntersectsActiveCamera => _cameraIntersections.Count > 0;

        /// <summary>
        /// Creates or resizes the light's standalone shadow map resources.
        /// </summary>
        public virtual void SetShadowMapResolution(uint width, uint height)
        {
            SetField(ref _shadowMapResolutionWidth, width, nameof(ShadowMapResolutionWidth));
            SetField(ref _shadowMapResolutionHeight, height, nameof(ShadowMapResolutionHeight));

            if (ShadowMap is null)
                ShadowMap = new XRMaterialFrameBuffer(GetShadowMapMaterial(width, height))
                {
                    // Named so GPU frame dumps attribute shadow passes to this light
                    // instead of falling back to the null-target "Swapchain" label.
                    Name = $"{GetType().Name}.{ID:N}.ShadowMapFbo",
                };
            else
                ShadowMap.Resize(width, height);
        }

        /// <summary>
        /// Gets the default storage format for the light's shadow map.
        /// </summary>
        protected virtual EShadowMapStorageFormat DefaultShadowMapStorageFormat => EShadowMapStorageFormat.R16Float;

        /// <summary>
        /// Returns true when the light can allocate and sample the requested shadow-map storage format.
        /// </summary>
        public virtual bool SupportsShadowMapStorageFormat(EShadowMapStorageFormat format)
            => IsColorShadowMapStorageFormat(format) || IsMomentShadowMapStorageFormat(format);

        /// <summary>
        /// Determines whether the light supports the specified shadow-map storage format.
        /// </summary>
        /// <param name="format">The shadow-map storage format to check for support.</param>
        /// <returns>True if the light supports the specified shadow-map storage format; otherwise, false.</returns>
        protected EShadowMapStorageFormat NormalizeShadowMapStorageFormat(EShadowMapStorageFormat format)
            => SupportsShadowMapStorageFormat(format) ? format : DefaultShadowMapStorageFormat;

        /// <summary>
        /// Recreates the light's shadow map by destroying the existing one and allocating a new one with the current resolution.
        /// </summary>
        protected virtual void RecreateShadowMap()
        {
            ShadowMap?.Destroy();
            ShadowMap = null;
            SetShadowMapResolution(
                Math.Max(1u, ShadowMapResolutionWidth),
                Math.Max(1u, ShadowMapResolutionHeight));
        }

        /// <summary>
        /// Called when the light component is deactivated, performing cleanup tasks such as clearing dynamic world registration and destroying the shadow map.
        /// </summary>
        protected override void OnComponentDeactivated()
        {
            ClearDynamicWorldRegistration();
            base.OnComponentDeactivated();
            ShadowMap?.Destroy();
            ShadowMap = null;
        }

        /// <summary>
        /// Gets or sets the number of samples used for the light's shadow filtering.
        /// </summary>
        [Category("Shadows")]
        public int Samples
        {
            get => FilterSamples;
            set => FilterSamples = value;
        }

        /// <summary>
        /// Gets or sets the number of filter taps used by soft-shadow sampling paths.
        /// </summary>
        [Category("Shadows")]
        public int FilterSamples
        {
            get => _filterSamples;
            set => SetField(ref _filterSamples, Math.Max(1, value));
        }

        /// <summary>
        /// Gets or sets the number of blocker-search taps used by contact-hardening PCSS.
        /// </summary>
        [Category("Shadows")]
        public int BlockerSamples
        {
            get => _blockerSamples;
            set => SetField(ref _blockerSamples, Math.Max(1, value));
        }

        /// <summary>
        /// Gets or sets the tap count used by <see cref="ESoftShadowMode.VogelDisk"/>.
        /// </summary>
        [Category("Shadows")]
        public int VogelTapCount
        {
            get => _vogelTapCount;
            set => SetField(ref _vogelTapCount, Math.Clamp(value, 1, MaxVogelTapCount));
        }

        /// <summary>
        /// Gets or sets the radius used for filtering soft shadows.
        /// </summary>
        [Category("Shadows")]
        public float FilterRadius
        {
            get => _filterRadius;
            set => SetField(ref _filterRadius, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the search radius used while estimating blockers for PCSS shadows.
        /// </summary>
        [Category("Shadows")]
        public float BlockerSearchRadius
        {
            get => _blockerSearchRadius;
            set => SetField(ref _blockerSearchRadius, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the minimum penumbra size for contact-hardening shadows.
        /// </summary>
        [Category("Shadows")]
        public float MinPenumbra
        {
            get => _minPenumbra;
            set => SetField(ref _minPenumbra, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the maximum penumbra size for contact-hardening shadows.
        /// </summary>
        [Category("Shadows")]
        public float MaxPenumbra
        {
            get => _maxPenumbra;
            set => SetField(ref _maxPenumbra, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Selects the soft shadow technique: Hard (PCF fallback), FixedPoisson (fixed-radius Poisson disk),
        /// VogelDisk (fixed-radius Vogel disk), or ContactHardeningPcss (blocker-search variable penumbra).
        /// </summary>
        [Category("Shadows")]
        public ESoftShadowMode SoftShadowMode
        {
            get => _softShadowMode;
            set => SetField(ref _softShadowMode, value);
        }

        /// <summary>
        /// Indicates whether the light supports using its radius for contact-hardening shadows.
        /// </summary>
        [Browsable(false)]
        public virtual bool SupportsLightRadiusContactHardening => false;

        /// <summary>
        /// Gets the effective light radius used for contact-hardening shadows.
        /// </summary>
        [Browsable(false)]
        protected virtual float ContactHardeningLightRadius => LightSourceRadius;

        /// <summary>
        /// Gets the effective light source radius, taking into account whether the light radius is used for contact-hardening shadows.
        /// </summary>
        [Browsable(false)]
        public float EffectiveLightSourceRadius
            => UseLightRadiusForContactHardening
                ? ClampedAutomaticContactHardeningLightRadius()
                : LightSourceRadius;

        /// <summary>
        /// Clamps the automatic contact-hardening light radius to a valid range.
        /// </summary>
        private float ClampedAutomaticContactHardeningLightRadius()
        {
            float radius = ContactHardeningLightRadius;
            return !float.IsFinite(radius) ? 0.0f : Math.Clamp(radius, 0.0f, MaxAutomaticContactHardeningLightRadius);
        }

        /// <summary>
        /// Gets or sets a value indicating whether the light should use its radius for contact-hardening shadows.
        /// </summary>
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
        [DisplayName("Light Source Radius")]
        [Description("Physical radius of the light source in world units. Used by contact-hardening shadows to compute the penumbra width. Larger values produce wider, softer penumbrae.")]
        public float LightSourceRadius
        {
            get => _lightSourceRadius;
            set => SetField(ref _lightSourceRadius, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets a value indicating whether cascaded shadows are enabled for the light.
        /// </summary>
        [Category("Shadows")]
        public bool EnableCascadedShadows
        {
            get => _enableCascadedShadows;
            set => SetField(ref _enableCascadedShadows, value);
        }

        /// <summary>
        /// Gets or sets a value indicating whether contact shadows are enabled for the light.
        /// </summary>
        [Category("Shadows")]
        public bool EnableContactShadows
        {
            get => _enableContactShadows;
            set => SetField(ref _enableContactShadows, value);
        }

        /// <summary>
        /// Gets or sets the maximum distance at which contact shadows are visible for the light.
        /// </summary>
        [Category("Shadows")]
        public float ContactShadowDistance
        {
            get => _contactShadowDistance;
            set => SetField(ref _contactShadowDistance, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the number of samples used for contact shadows.
        /// Higher values produce smoother shadows but may impact performance.
        /// </summary>
        [Category("Shadows")]
        public int ContactShadowSamples
        {
            get => _contactShadowSamples;
            set => SetField(ref _contactShadowSamples, Math.Clamp(value, 1, 32));
        }

        /// <summary>
        /// Gets or sets the thickness of the contact shadows for the light.
        /// </summary>
        [Category("Shadows")]
        public float ContactShadowThickness
        {
            get => _contactShadowThickness;
            set => SetField(ref _contactShadowThickness, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the distance at which contact shadows start to fade for the light.
        /// </summary>
        [Category("Shadows")]
        public float ContactShadowFadeStart
        {
            get => _contactShadowFadeStart;
            set => SetField(ref _contactShadowFadeStart, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the distance at which contact shadows completely fade for the light.
        /// </summary>
        [Category("Shadows")]
        public float ContactShadowFadeEnd
        {
            get => _contactShadowFadeEnd;
            set => SetField(ref _contactShadowFadeEnd, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the normal offset for contact shadows for the light.
        /// </summary>
        [Category("Shadows")]
        public float ContactShadowNormalOffset
        {
            get => _contactShadowNormalOffset;
            set => SetField(ref _contactShadowNormalOffset, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Gets or sets the contact shadow jitter strength for the light.
        /// </summary>
        [Category("Shadows")]
        public float ContactShadowJitterStrength
        {
            get => _contactShadowJitterStrength;
            set => SetField(ref _contactShadowJitterStrength, Math.Clamp(value, 0.0f, 1.0f));
        }

        /// <summary>
        /// Gets or sets the shadow debug visualisation mode for the light.
        /// 0 = off, 1 = shadow-only, 2 = margin heatmap.
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
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowExponentBaseUniform, ShadowExponentBase);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowExponentUniform, ShadowExponent);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowBiasMinUniform, ShadowMinBias);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowBiasMaxUniform, ShadowMaxBias);
            program.Uniform("ShadowBiasParams", ShadowBiasParameters);
            program.Uniform("ShadowBiasProjectionParams", ShadowBiasProjectionParameters);

            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowSamples, FilterSamples);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowBlockerSamples, BlockerSamples);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowFilterSamples, FilterSamples);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowVogelTapCount, VogelTapCount);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowFilterRadius, FilterRadius);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowBlockerSearchRadius, BlockerSearchRadius);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowMinPenumbra, MinPenumbra);
            program.Uniform(RuntimeEngine.Rendering.Constants.ShadowMaxPenumbra, MaxPenumbra);
            program.Uniform(RuntimeEngine.Rendering.Constants.SoftShadowMode, (int)SoftShadowMode);
            program.Uniform(RuntimeEngine.Rendering.Constants.LightSourceRadius, EffectiveLightSourceRadius);
            program.Uniform("ShadowDepthMode", 0);

            program.Uniform(RuntimeEngine.Rendering.Constants.EnableCascadedShadows, EnableCascadedShadows);
            program.Uniform(RuntimeEngine.Rendering.Constants.EnableContactShadows, EnableContactShadows);
            program.Uniform(RuntimeEngine.Rendering.Constants.ContactShadowDistance, ContactShadowDistance);
            program.Uniform(RuntimeEngine.Rendering.Constants.ContactShadowSamples, ContactShadowSamples);
            program.Uniform(RuntimeEngine.Rendering.Constants.ContactShadowThickness, ContactShadowThickness);
            program.Uniform(RuntimeEngine.Rendering.Constants.ContactShadowFadeStart, ContactShadowFadeStart);
            program.Uniform(RuntimeEngine.Rendering.Constants.ContactShadowFadeEnd, ContactShadowFadeEnd);
            program.Uniform(RuntimeEngine.Rendering.Constants.ContactShadowNormalOffset, ContactShadowNormalOffset);
            program.Uniform(RuntimeEngine.Rendering.Constants.ContactShadowJitterStrength, ContactShadowJitterStrength);

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
