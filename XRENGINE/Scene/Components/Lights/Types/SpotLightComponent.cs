using Extensions;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;
using static XREngine.Data.Core.XRMath;

namespace XREngine.Components.Capture.Lights.Types
{
    [XRComponentEditor("XREngine.Editor.ComponentEditors.SpotLightComponentEditor")]
    public class SpotLightComponent(float distance, float outerCutoffDeg, float innerCutoffDeg, float brightness, float exponent) : OneViewLightComponent()
    {
        protected override XRPerspectiveCameraParameters GetCameraParameters() => new(
            Math.Max(OuterCutoffAngleDegrees, InnerCutoffAngleDegrees) * 2.0f,
            1.0f, 1.0f,
            _distance);

        private float 
            _outerCutoff = (float)Math.Cos(DegToRad(outerCutoffDeg)),
            _innerCutoff = (float)Math.Cos(DegToRad(innerCutoffDeg)),
            _distance = distance,
            _exponent = exponent,
            _brightness = brightness;

        private Cone _outerCone = new(
            Vector3.Zero,
            Globals.Backward,
            MathF.Tan(DegToRad(outerCutoffDeg)) * distance,
            distance);

        private Cone _innerCone = new(
            Vector3.Zero,
            Globals.Backward,
            MathF.Tan(DegToRad(innerCutoffDeg)) * distance,
            distance);


        public float Distance
        {
            get => _distance;
            set => SetField(ref _distance, value);
        }
        public float Exponent
        {
            get => _exponent;
            set => SetField(ref _exponent, value);
        }
        public float Brightness
        {
            get => _brightness;
            set => SetField(ref _brightness, value);
        }
        public float OuterCutoffAngleDegrees
        {
            get => RadToDeg((float)Math.Acos(_outerCutoff));
            set => SetCutoffs(InnerCutoffAngleDegrees, value, true);
        }
        public float InnerCutoffAngleDegrees
        {
            get => RadToDeg((float)Math.Acos(_innerCutoff));
            set => SetCutoffs(value, OuterCutoffAngleDegrees, false);
        }

        public float InnerCutoff => _innerCutoff;
        public float OuterCutoff => _outerCutoff;

        public static XRMesh GetVolumeMesh()
            => XRMesh.Shapes.SolidCone(Vector3.Zero, Globals.Backward, 1.0f, 1.0f, 32, true);
        protected override XRMesh GetWireframeMesh()
            => XRMesh.Shapes.WireframeCone(Vector3.Zero, Globals.Backward, 1.0f, 1.0f, 32);

        public Cone OuterCone => _outerCone;
        public Cone InnerCone => _innerCone;

        public SpotLightComponent()
            : this(100.0f, 60.0f, 30.0f, 1.0f, 1.0f) { }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (Type == ELightType.Dynamic)
                World?.Lights.DynamicSpotLights.Add(this);
        }
        protected internal override void OnComponentDeactivated()
        {
            if (Type == ELightType.Dynamic)
                World?.Lights.DynamicSpotLights.Remove(this);
            base.OnComponentDeactivated();
        }

        public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            base.SetUniforms(program, targetStructName);

            targetStructName = $"{targetStructName ?? Engine.Rendering.Constants.LightsStructName}.";

            program.Uniform($"{targetStructName}Color", _color);
            program.Uniform($"{targetStructName}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{targetStructName}WorldToLightProjMatrix", ShadowCamera?.ProjectionMatrix ?? Matrix4x4.Identity);
            program.Uniform($"{targetStructName}WorldToLightInvViewMatrix", ShadowCamera?.Transform.RenderMatrix ?? Matrix4x4.Identity);

            program.Uniform($"{targetStructName}Position", Transform.RenderTranslation);
            program.Uniform($"{targetStructName}Direction", Transform.RenderForward);
            program.Uniform($"{targetStructName}Radius", Distance);
            program.Uniform($"{targetStructName}Brightness", Brightness);
            program.Uniform($"{targetStructName}Exponent", Exponent);

            program.Uniform($"{targetStructName}InnerCutoff", _innerCutoff);
            program.Uniform($"{targetStructName}OuterCutoff", _outerCutoff);

            var mat = ShadowMap?.Material;
            if (mat is null || mat.Textures.Count < 2)
                return;
            
            var tex = mat.Textures[1];
            if (tex is not null)
                program.Sampler("ShadowMap", tex, 4);
        }

        public override void SetShadowMapResolution(uint width, uint height)
        {
            base.SetShadowMapResolution(width, height);

            if (ShadowCamera?.Parameters is XRPerspectiveCameraParameters p)
                p.AspectRatio = width / height;
        }

        public override XRMaterial GetShadowMapMaterial(uint width, uint height, EDepthPrecision precision = EDepthPrecision.Int24)
        {
            XRTexture2D[] refs =
            [
                new XRTexture2D(width, height, GetShadowDepthMapFormat(precision), EPixelFormat.DepthComponent, EPixelType.UnsignedByte)
                {
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                },
                new XRTexture2D(width, height, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat)
                {
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
                    SamplerName = "ShadowMap"
                },
            ];

            //This material is used for rendering to the framebuffer.
            XRMaterial mat = new(refs, new XRShader(EShaderType.Fragment, ShaderHelper.Frag_DepthOutput));

            //No culling so if a light exists inside of a mesh it will shadow everything.
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;

            return mat;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Transform):
                case nameof(Distance):
                    UpdateCones(Transform.RenderMatrix);
                    break;
                case nameof(Type):
                    if (Type == ELightType.Dynamic)
                        World?.Lights.DynamicSpotLights.Add(this);
                    else
                        World?.Lights.DynamicSpotLights.Remove(this);
                    break;
            }
        }

        public void SetCutoffs(float innerDegrees, float outerDegrees, bool constrainInnerToOuter = true)
        {
            innerDegrees = innerDegrees.Clamp(0.0f, 90.0f);
            outerDegrees = outerDegrees.Clamp(0.0f, 90.0f);
            if (outerDegrees < innerDegrees)
            {
                float bias = 0.0001f;
                if (constrainInnerToOuter)
                    innerDegrees = outerDegrees - bias;
                else
                    outerDegrees = innerDegrees + bias;
            }

            SetField(ref _outerCutoff, MathF.Cos(DegToRad(outerDegrees)), nameof(OuterCutoffAngleDegrees));
            SetField(ref _innerCutoff, MathF.Cos(DegToRad(innerDegrees)), nameof(InnerCutoffAngleDegrees));

            if (ShadowCamera != null && ShadowCamera.Parameters is XRPerspectiveCameraParameters p)
                p.VerticalFieldOfView = Math.Max(outerDegrees, innerDegrees) * 2.0f;

            UpdateCones(Transform.RenderMatrix);
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            UpdateCones(renderMatrix);
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        private void UpdateCones(Matrix4x4 renderMatrix)
        {
            float d = Distance;
            Vector3 dir = Vector3.TransformNormal(Globals.Forward, renderMatrix);
            Vector3 coneOrigin = renderMatrix.Translation + dir * (d * 0.5f);

            SetField(ref _outerCone, new(coneOrigin, -dir, d, MathF.Tan(DegToRad(OuterCutoffAngleDegrees)) * d));
            SetField(ref _innerCone, new(coneOrigin, -dir, d, MathF.Tan(DegToRad(InnerCutoffAngleDegrees)) * d));

            ShadowCamera?.FarZ = d;

            MeshCenterAdjustMatrix = Matrix4x4.CreateScale(OuterCone.Radius, OuterCone.Radius, OuterCone.Height) * Matrix4x4.CreateTranslation(Globals.Forward * (Distance * 0.5f));
        }
    }
}
