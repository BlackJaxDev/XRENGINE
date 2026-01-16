using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

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
        private float _shadowMaxBias = 0.003f;
        private float _shadowMinBias = 0.000f;
        private float _shadowExponent = 0.994f;
        private float _shadowExponentBase = 0.037f;
        private Matrix4x4 _lightMatrix = Matrix4x4.Identity;
        private Matrix4x4 _meshCenterAdjustMatrix = Matrix4x4.Identity;
        private readonly RenderCommandMesh3D _shadowVolumeRC = new((int)EDefaultRenderPass.OpaqueForward);
        private readonly List<FrustumIntersectionAabb> _cameraIntersections = new(6);
        private bool _previewBoundingVolume = false;

        private float _lastMovedTime = 0.0f;
        private uint _movementVersion = 0;

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
        public float TimeSinceLastMovement => MathF.Max(0.0f, Engine.ElapsedTime - _lastMovedTime);

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
            }
        }

        /// <summary>
        /// Override this method to set any additional uniforms needed for the shadow map material.
        /// </summary>
        /// <param name="base"></param>
        /// <param name="program"></param>
        protected virtual void SetShadowMapUniforms(XRMaterialBase @base, XRRenderProgram program) { }

        public LightComponent() : base()
        {
            _lastMovedTime = Engine.ElapsedTime;

            XRMaterial mat = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.0f, 1.0f, 0.0f, 0.0f));
            mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
            mat.RenderOptions.DepthTest.Enabled = Rendering.Models.Materials.ERenderParamUsage.Disabled;
            _shadowVolumeRC.Mesh = new XRMeshRenderer(GetWireframeMesh(), mat);

            RenderInfo = RenderInfo3D.New(this, _shadowVolumeRC);
            RenderInfo.IsVisible = Engine.EditorPreferences.Debug.VisualizeDirectionalLightVolumes;
            RenderInfo.VisibleInLightingProbes = false;
            RenderedObjects = [RenderInfo];
        }

        protected abstract XRMesh GetWireframeMesh();

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            _lastMovedTime = Engine.ElapsedTime;
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

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            ShadowMap?.Destroy();
        }

        public int Samples { get; set; } = 1;
        public float FilterRadius { get; set; } = 0.001f;
        public bool EnablePCSS { get; set; } = true;
        public bool EnableCascadedShadows { get; set; } = true;
        public bool EnableContactShadows { get; set; } = true;
        public float ContactShadowDistance { get; set; } = 0.1f;
        public int ContactShadowSamples { get; set; } = 8;

        public virtual void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            program.Uniform(Engine.Rendering.Constants.ShadowExponentBaseUniform, ShadowExponentBase);
            program.Uniform(Engine.Rendering.Constants.ShadowExponentUniform, ShadowExponent);
            program.Uniform(Engine.Rendering.Constants.ShadowBiasMinUniform, ShadowMinBias);
            program.Uniform(Engine.Rendering.Constants.ShadowBiasMaxUniform, ShadowMaxBias);

            program.Uniform(Engine.Rendering.Constants.ShadowSamples, Samples);
            program.Uniform(Engine.Rendering.Constants.ShadowFilterRadius, FilterRadius);
            program.Uniform(Engine.Rendering.Constants.EnablePCSS, EnablePCSS);

            program.Uniform(Engine.Rendering.Constants.EnableCascadedShadows, EnableCascadedShadows);
            program.Uniform(Engine.Rendering.Constants.EnableContactShadows, EnableContactShadows);
            program.Uniform(Engine.Rendering.Constants.ContactShadowDistance, ContactShadowDistance);
            program.Uniform(Engine.Rendering.Constants.ContactShadowSamples, ContactShadowSamples);
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
                if (GeoUtil.TryIntersectFrustaAabb(cameraFrustum, lightFrusta[i], out Vector3 min, out Vector3 max))
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
