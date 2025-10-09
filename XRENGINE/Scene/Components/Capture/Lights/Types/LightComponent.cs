using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture.Lights.Types
{
    public abstract class LightComponent : XRComponent, IRenderable
    {
        protected ColorF3 _color = new(1.0f, 1.0f, 1.0f);
        protected float _diffuseIntensity = 1.0f;
        private XRMaterialFrameBuffer? _shadowMap = null;
        private ELightType _type = ELightType.Dynamic;
        private bool _castsShadows = true;
        private float _shadowMaxBias = 0.4f;
        private float _shadowMinBias = 0.0001f;
        private float _shadowExponent = 1.0f;
        private float _shadowExponentBase = 0.04f;
        private Matrix4x4 _lightMatrix = Matrix4x4.Identity;
        private Matrix4x4 _meshCenterAdjustMatrix = Matrix4x4.Identity;
        private readonly RenderCommandMesh3D _shadowVolumeRC = new((int)EDefaultRenderPass.OpaqueForward);

        /// <summary>
        /// This matrix is the location of the center of the light source. Used for rendering the light mesh.
        /// </summary>
        public Matrix4x4 MeshCenterAdjustMatrix
        {
            get => _meshCenterAdjustMatrix;
            protected set => SetField(ref _meshCenterAdjustMatrix, value);
        }
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
            XRMaterial mat = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.0f, 1.0f, 0.0f, 0.0f));
            mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
            mat.RenderOptions.DepthTest.Enabled = Rendering.Models.Materials.ERenderParamUsage.Disabled;
            _shadowVolumeRC.Mesh = new XRMeshRenderer(GetWireframeMesh(), mat);

            RenderInfo = RenderInfo3D.New(this, _shadowVolumeRC);
            RenderInfo.IsVisible = Engine.Rendering.Settings.VisualizeDirectionalLightVolumes;
            RenderInfo.VisibleInLightingProbes = false;
            RenderedObjects = [RenderInfo];
        }

        protected abstract XRMesh GetWireframeMesh();

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            _shadowVolumeRC.WorldMatrix = _lightMatrix = MeshCenterAdjustMatrix * renderMatrix;
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        public XRMaterialFrameBuffer? ShadowMap
        {
            get => _shadowMap;
            protected set => SetField(ref _shadowMap, value);
        }

        public bool CastsShadows
        {
            get => _castsShadows;
            set => SetField(ref _castsShadows, value);
        }

        public float ShadowExponentBase 
        {
            get => _shadowExponentBase;
            set => SetField(ref _shadowExponentBase, value);
        }

        public float ShadowExponent
        {
            get => _shadowExponent;
            set => SetField(ref _shadowExponent, value);
        }

        public float ShadowMinBias
        {
            get => _shadowMinBias;
            set => SetField(ref _shadowMinBias, value);
        }

        public float ShadowMaxBias
        {
            get => _shadowMaxBias;
            set => SetField(ref _shadowMaxBias, value);
        }

        private uint _shadowMapResolutionWidth = 4096u;
        public uint ShadowMapResolutionWidth
        {
            get => _shadowMapResolutionWidth;
            set => SetShadowMapResolution(value, ShadowMapResolutionHeight);
        }
        private uint _shadowMapResolutionHeight = 4096u;
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

        public abstract void SwapBuffers();
        public abstract void CollectVisibleItems();
        public abstract void RenderShadowMap(bool collectVisibleNow = false);

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
