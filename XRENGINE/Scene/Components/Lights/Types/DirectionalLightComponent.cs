using System.ComponentModel;
using System.Numerics;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Core.Attributes;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights
{
    [RequiresTransform(typeof(Transform))]
    [Category("Lighting")]
    [DisplayName("Directional Light")]
    [Description("Illuminates the scene with an infinite directional light that can cast cascaded shadows.")]
    public class DirectionalLightComponent : OneViewLightComponent
    {
        private const float NearZ = 0.01f;

        private Vector3 _scale = Vector3.One;
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
            ShadowCameraTransform.Parent = Transform;
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (Type == ELightType.Dynamic)
                World?.Lights.DynamicDirectionalLights.Add(this);
        }
        protected internal override void OnComponentDeactivated()
        {
            if (Type == ELightType.Dynamic)
                World?.Lights.DynamicDirectionalLights.Remove(this);
            base.OnComponentDeactivated();
        }

        public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            base.SetUniforms(program, targetStructName);

            targetStructName = $"{targetStructName ?? Engine.Rendering.Constants.LightsStructName}.";

            program.Uniform($"{targetStructName}Direction", Transform.WorldForward);
            program.Uniform($"{targetStructName}Color", _color);
            program.Uniform($"{targetStructName}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{targetStructName}WorldToLightProjMatrix", ShadowCamera?.ProjectionMatrix ?? Matrix4x4.Identity);
            program.Uniform($"{targetStructName}WorldToLightInvViewMatrix", ShadowCamera?.Transform.WorldMatrix ?? Matrix4x4.Identity);

            if (ShadowMap?.Material is null)
                return;

            var shadowMap = ShadowMap.Material.Textures[0];
            if (shadowMap != null)
                program.Sampler("ShadowMap", shadowMap, 4);
        }

        public override XRMaterial GetShadowMapMaterial(uint width, uint height, EDepthPrecision precision = EDepthPrecision.Int24)
        {
            XRTexture[] refs =
            [
                 new XRTexture2D(width, height, GetShadowDepthMapFormat(precision), EPixelFormat.DepthComponent, EPixelType.Float)
                 {
                     MinFilter = ETexMinFilter.Nearest,
                     MagFilter = ETexMagFilter.Nearest,
                     UWrap = ETexWrapMode.ClampToEdge,
                     VWrap = ETexWrapMode.ClampToEdge,
                     FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                     SamplerName = "ShadowMap"
                 }
            ];

            //This material is used for rendering to the framebuffer.
            XRMaterial mat = new(refs, new XRShader(EShaderType.Fragment, ShaderHelper.Frag_Nothing));

            //No culling so if a light exists inside of a mesh it will shadow everything.
            mat.RenderOptions.CullMode = ECullMode.None;

            return mat;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Transform):
                    if (_shadowCameraTransform is not null)
                        _shadowCameraTransform.Parent = Transform;
                    break;
                case nameof(Scale):
                    MeshCenterAdjustMatrix = Matrix4x4.CreateScale(Scale);
                    ShadowCameraTransform.Translation = Globals.Backward * Scale.Z * 0.5f;
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
                case nameof(Type):
                    if (Type == ELightType.Dynamic)
                        World?.Lights.DynamicDirectionalLights.Add(this);
                    else
                        World?.Lights.DynamicDirectionalLights.Remove(this);
                    break;
            }
        }
    }
}
