using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine.Components.Capture.Lights.Types
{
    public abstract class LightComponent : XRComponent, IRenderable
    {
        public readonly record struct FrustumIntersectionAabb(int FrustumIndex, Vector3 Min, Vector3 Max);

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
        private XRWorldInstance? _registeredDynamicWorld;
        private int _samples = 4;
        private float _filterRadius = 0.0012f;
        private ESoftShadowMode _softShadowMode = ESoftShadowMode.PCSS;
        private float _lightSourceRadius = 0.01f;
        private bool _enableCascadedShadows = true;
        private bool _enableContactShadows = true;
        private float _contactShadowDistance = 0.1f;
        private int _contactShadowSamples = 4;
        private int _shadowDebugMode = 0;

        private long _lastMovedTicks;
        private uint _movementVersion = 0;

        internal static float TimeSinceLastMovementSeconds(long currentTicks, long lastMovedTicks)
            => EngineTimer.TicksToSeconds(Math.Max(0L, currentTicks - lastMovedTicks));

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
        public float TimeSinceLastMovement => TimeSinceLastMovementSeconds(Engine.ElapsedTicks, _lastMovedTicks);

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
                case nameof(MeshCenterAdjustMatrix):
                    _lightMatrix = MeshCenterAdjustMatrix * Transform.RenderMatrix;
                    break;
                case nameof(ShadowMap):
                    if (ShadowMap?.Material is not null)
                        ShadowMap.Material.SettingUniforms += SetShadowMapUniforms;
                    break;
                case nameof(World):
                    SyncDynamicWorldRegistration();
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
            SyncDynamicWorldRegistration();
        }

        protected virtual void RegisterDynamicLight(XRWorldInstance world)
        {
        }

        protected virtual void UnregisterDynamicLight(XRWorldInstance world)
        {
        }

        private void SyncDynamicWorldRegistration()
        {
            XRWorldInstance? currentWorld = WorldAs<XRWorldInstance>();

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

        public LightComponent() : base()
        {
            _lastMovedTicks = Engine.ElapsedTicks;

            XRMaterial mat = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.0f, 1.0f, 0.0f, 0.0f));
            mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
            mat.RenderOptions.DepthTest.Enabled = Rendering.Models.Materials.ERenderParamUsage.Disabled;
            _shadowVolumeRC.Mesh = new XRMeshRenderer(GetWireframeMesh(), mat);

            RenderInfo = RenderInfo3D.New(this, _shadowVolumeRC);
            RenderInfo.IsVisible = Engine.EditorPreferences.Debug.VisualizeDirectionalLightVolumes;
            RenderInfo.CastsShadows = false;
            RenderInfo.ReceivesShadows = false;
            RenderInfo.VisibleInLightingProbes = false;
            RenderedObjects = [RenderInfo];
        }

        protected abstract XRMesh GetWireframeMesh();

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            _lastMovedTicks = Engine.ElapsedTicks;
            unchecked { _movementVersion++; }
            _shadowVolumeRC.WorldMatrix = _lightMatrix = MeshCenterAdjustMatrix * renderMatrix;
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        public XRMaterialFrameBuffer? ShadowMap
        {
            get => _shadowMap;
            protected set => SetField(ref _shadowMap, value);
        }

        [Category("Shadows")]
        public bool CastsShadows
        {
            get => _castsShadows;
            set => SetField(ref _castsShadows, value);
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
                if (SetField(ref _previewBoundingVolume, value))
                    RenderInfo.IsVisible = value || Engine.EditorPreferences.Debug.VisualizeDirectionalLightVolumes;
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

        protected override void OnComponentDeactivated()
        {
            ClearDynamicWorldRegistration();
            base.OnComponentDeactivated();
            ShadowMap?.Destroy();
            ShadowMap = null;
        }

        public int Samples
        {
            get => _samples;
            set => SetField(ref _samples, Math.Max(1, value));
        }

        public float FilterRadius
        {
            get => _filterRadius;
            set => SetField(ref _filterRadius, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Selects the soft shadow technique: Hard (PCF fallback), PCSS (fixed-radius Poisson disk),
        /// or ContactHardening (blocker-search variable penumbra).
        /// </summary>
        public ESoftShadowMode SoftShadowMode
        {
            get => _softShadowMode;
            set => SetField(ref _softShadowMode, value);
        }

        /// <summary>
        /// Physical radius of the light source in world units. Used by <see cref="ESoftShadowMode.ContactHardening"/>
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
            set => SetField(ref _contactShadowSamples, Math.Max(1, value));
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

            program.Uniform(Engine.Rendering.Constants.ShadowSamples, Samples);
            program.Uniform(Engine.Rendering.Constants.ShadowFilterRadius, FilterRadius);
            program.Uniform(Engine.Rendering.Constants.SoftShadowMode, (int)SoftShadowMode);
            program.Uniform(Engine.Rendering.Constants.LightSourceRadius, LightSourceRadius);

            program.Uniform(Engine.Rendering.Constants.EnableCascadedShadows, EnableCascadedShadows);
            program.Uniform(Engine.Rendering.Constants.EnableContactShadows, EnableContactShadows);
            program.Uniform(Engine.Rendering.Constants.ContactShadowDistance, ContactShadowDistance);
            program.Uniform(Engine.Rendering.Constants.ContactShadowSamples, ContactShadowSamples);

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
    }
}
