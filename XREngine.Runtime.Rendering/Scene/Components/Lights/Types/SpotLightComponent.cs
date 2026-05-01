using XREngine.Extensions;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shadows;
using XREngine.Scene.Transforms;
using static XREngine.Data.Core.XRMath;

namespace XREngine.Components.Capture.Lights.Types
{
    [XRComponentEditor("XREngine.Editor.ComponentEditors.SpotLightComponentEditor")]
    public class SpotLightComponent : OneViewLightComponent
    {
        public SpotLightComponent(float distance, float outerCutoffDeg, float innerCutoffDeg, float brightness, float exponent)
        {
            _outerCutoff = (float)Math.Cos(DegToRad(outerCutoffDeg));
            _innerCutoff = (float)Math.Cos(DegToRad(innerCutoffDeg));
            _distance = distance;
            _shadowNearPlaneDistance = ClampShadowNearPlaneDistance(_shadowNearPlaneDistance, _distance);
            _exponent = exponent;
            _brightness = brightness;

            float outerConeRadius = CalculateOuterConeRadius(distance, outerCutoffDeg);
            float innerConeRadius = CalculateOuterConeRadius(distance, innerCutoffDeg);

            _outerCone = new(
                Vector3.Zero,
                Globals.Backward,
                distance,
                outerConeRadius);

            _innerCone = new(
                Vector3.Zero,
                Globals.Backward,
                distance,
                innerConeRadius);

            SetShadowMapResolution(512u, 512u);
            ShadowMinBias = 0.0001f;
            ShadowMaxBias = 0.07f;
            ShadowExponentBase = 0.2f;
            ShadowExponent = 1.0f;
            BlockerSamples = 8;
            FilterSamples = 8;
            FilterRadius = 0.0012f;
            BlockerSearchRadius = 0.1f;
            MinPenumbra = 0.0002f;
            MaxPenumbra = 0.05f;
            SoftShadowMode = ESoftShadowMode.ContactHardeningPcss;
            LightSourceRadius = 0.1f;
            UseLightRadiusForContactHardening = false;
            EnableContactShadows = true;
            ContactShadowDistance = 3.0f;
            ContactShadowSamples = 16;
            ContactShadowThickness = 2.0f;
            ContactShadowFadeStart = 10.0f;
            ContactShadowFadeEnd = 40.0f;
            ContactShadowNormalOffset = 0.0f;
            ContactShadowJitterStrength = 1.0f;
        }

        protected override XRPerspectiveCameraParameters GetCameraParameters() => new(
            Math.Max(OuterCutoffAngleDegrees, InnerCutoffAngleDegrees) * 2.0f,
            1.0f,
            _shadowNearPlaneDistance,
            MathF.Max(_distance, _shadowNearPlaneDistance + 0.001f));

        private float 
            _outerCutoff,
            _innerCutoff,
            _distance,
            _shadowNearPlaneDistance = 1.0f,
            _exponent,
            _brightness;

        private Cone _outerCone;

        private Cone _innerCone;

        private XRMaterial? _shadowAtlasMaterial;


        public float Distance
        {
            get => _distance;
            set => SetField(ref _distance, value);
        }
        public float ShadowNearPlaneDistance
        {
            get => _shadowNearPlaneDistance;
            set => SetField(ref _shadowNearPlaneDistance, ClampShadowNearPlaneDistance(value, _distance));
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

        public override bool SupportsLightRadiusContactHardening => true;

        protected override bool UsesAtlasShadowViewport
            => Engine.Rendering.Settings.UseSpotShadowAtlas;

        protected override float ContactHardeningLightRadius
            => CalculateOuterConeRadius(Distance, OuterCutoffAngleDegrees);

        public SpotLightComponent()
            : this(100.0f, 60.0f, 30.0f, 1.0f, 1.0f) { }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
        }

        protected override void RegisterDynamicLight(IRuntimeRenderWorld world)
            => world.Lights.DynamicSpotLights.Add(this);

        protected override void UnregisterDynamicLight(IRuntimeRenderWorld world)
            => world.Lights.DynamicSpotLights.Remove(this);

        public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            base.SetUniforms(program, targetStructName);

            string prefix = targetStructName ?? Engine.Rendering.Constants.LightsStructName;
            string flatPrefix = $"{prefix}.";
            string pointBasePrefix = $"{prefix}.Base.";
            string lightBasePrefix = $"{prefix}.Base.Base.";

            // Legacy flat uniforms.
            program.Uniform($"{flatPrefix}Color", _color);
            program.Uniform($"{flatPrefix}DiffuseIntensity", _diffuseIntensity);
            Matrix4x4 lightView = ShadowCamera?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 lightProj = ShadowCamera?.ProjectionMatrix ?? Matrix4x4.Identity;
            // C# Matrix4x4 is row-major but OpenGL expects column-major.
            // When uploading with transpose=false, the matrix gets transposed.
            // For GLSL's (mat * vec) convention to work, we need to reverse the multiplication order:
            // CPU: View * Proj (which becomes (Proj * View)^T when uploaded)
            Matrix4x4 lightViewProj = lightView * lightProj;

            program.Uniform($"{flatPrefix}WorldToLightProjMatrix", lightProj);
            program.Uniform($"{flatPrefix}WorldToLightInvViewMatrix", ShadowCamera?.Transform.RenderMatrix ?? Matrix4x4.Identity);
            program.Uniform($"{flatPrefix}WorldToLightSpaceMatrix", lightViewProj);  // Pre-computed for deferred shadow mapping
            program.Uniform($"{flatPrefix}Position", Transform.RenderTranslation);
            program.Uniform($"{flatPrefix}Direction", Transform.RenderForward);
            program.Uniform($"{flatPrefix}Radius", Distance);
            program.Uniform($"{flatPrefix}Brightness", Brightness);
            program.Uniform($"{flatPrefix}Exponent", Exponent);
            program.Uniform($"{flatPrefix}InnerCutoff", _innerCutoff);
            program.Uniform($"{flatPrefix}OuterCutoff", _outerCutoff);

            // Structured Base.* uniforms for ForwardLighting snippet compatibility.
            program.Uniform($"{lightBasePrefix}Color", _color);
            program.Uniform($"{lightBasePrefix}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{lightBasePrefix}AmbientIntensity", 0.05f);
            program.Uniform($"{lightBasePrefix}WorldToLightSpaceProjMatrix", lightViewProj);
            program.Uniform($"{pointBasePrefix}Position", Transform.RenderTranslation);
            program.Uniform($"{pointBasePrefix}Radius", Distance);
            program.Uniform($"{pointBasePrefix}Brightness", Brightness);
            program.Uniform($"{prefix}.Direction", Transform.RenderForward);
            program.Uniform($"{prefix}.InnerCutoff", _innerCutoff);
            program.Uniform($"{prefix}.OuterCutoff", _outerCutoff);
            program.Uniform($"{prefix}.Exponent", Exponent);
            // Note: Shadow map sampler is bound by the caller (deferred pass or forward lighting collection)
            // to avoid overwriting material texture units.
        }

        public override void SetShadowMapResolution(uint width, uint height)
        {
            base.SetShadowMapResolution(width, height);

            if (ShadowCamera?.Parameters is XRPerspectiveCameraParameters p)
                p.AspectRatio = width / height;
        }

        protected override void SetShadowMapUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            ShadowMapFormatSelection selection = ResolveShadowMapFormat(preferredStorageFormat: ShadowMapStorageFormat);
            float nearZ = ShadowCamera?.NearZ ?? ShadowNearPlaneDistance;
            float farZ = ShadowCamera?.FarZ ?? MathF.Max(nearZ + 0.001f, Distance);

            program.Uniform("ShadowMapEncoding", (int)selection.Encoding);
            program.Uniform("ShadowMomentMinVariance", ShadowMomentMinVariance);
            program.Uniform("ShadowMomentLightBleedReduction", ShadowMomentLightBleedReduction);
            program.Uniform("ShadowMomentPositiveExponent", selection.PositiveExponent);
            program.Uniform("ShadowMomentNegativeExponent", selection.NegativeExponent);
            program.Uniform("ShadowMomentMipBias", ShadowMomentMipBias);
            program.Uniform("CameraNearZ", nearZ);
            program.Uniform("CameraFarZ", farZ);
        }

        public override XRMaterial GetShadowMapMaterial(uint width, uint height, EDepthPrecision precision = EDepthPrecision.Int24)
        {
            ShadowMapFormatSelection selection = ResolveShadowMapFormat(preferredStorageFormat: ShadowMapStorageFormat);
            ShadowMapTextureFormat shadowFormat = GetShadowMapTextureFormat(selection.Format.StorageFormat);
            bool momentEncoding = selection.Encoding != EShadowMapEncoding.Depth;
            ETexMinFilter minFilter = selection.Format.RequiresLinearFiltering
                ? (ShadowMomentUseMipmaps ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear)
                : ETexMinFilter.Nearest;
            ETexMagFilter magFilter = selection.Format.RequiresLinearFiltering ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
            XRTexture2D[] refs =
            [
                new XRTexture2D(width, height, GetShadowDepthMapFormat(precision), EPixelFormat.DepthComponent, EPixelType.UnsignedInt)
                {
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                },
                new XRTexture2D(width, height, shadowFormat.InternalFormat, shadowFormat.PixelFormat, shadowFormat.PixelType)
                {
                    MinFilter = minFilter,
                    MagFilter = magFilter,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
                    SamplerName = "ShadowMap",
                    AutoGenerateMipmaps = momentEncoding && ShadowMomentUseMipmaps,
                },
            ];

            //This material is used for rendering to the framebuffer.
            XRMaterial mat = new(refs, new XRShader(EShaderType.Fragment, momentEncoding ? ShaderHelper.Frag_ShadowMomentOutput : ShaderHelper.Frag_DepthOutput));

            //No culling so if a light exists inside of a mesh it will shadow everything.
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;

            return mat;
        }

        protected override XREngine.Data.Colors.ColorF4 GetShadowMapClearColor()
        {
            ShadowMapFormatSelection selection = ResolveShadowMapFormat(preferredStorageFormat: ShadowMapStorageFormat);
            Vector4 clear = selection.ClearSentinel.Value;
            return new XREngine.Data.Colors.ColorF4(clear.X, clear.Y, clear.Z, clear.W);
        }

        private XRMaterial ShadowAtlasMaterial => _shadowAtlasMaterial ??= CreateShadowAtlasMaterial();

        private static XRMaterial CreateShadowAtlasMaterial()
        {
            XRMaterial mat = new(new XRShader(EShaderType.Fragment, ShaderHelper.Frag_LinearDepthOutput));

            // Match the legacy spot shadow caster state while rendering into atlas color pages.
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;

            return mat;
        }

        internal bool RenderShadowAtlasTile(XRFrameBuffer atlasFbo, BoundingRectangle renderRect, bool collectVisibleNow)
        {
            if (!CastsShadows ||
                ShadowCamera is null ||
                World is null ||
                renderRect.Width <= 0 ||
                renderRect.Height <= 0)
            {
                return false;
            }

            XRViewport viewport = PrimaryShadowViewport;
            if (viewport.RenderPipeline is not ShadowRenderPipeline shadowPipeline)
                return false;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            bool previousPreserveArea = shadowPipeline.PreserveExistingRenderArea;
            shadowPipeline.PreserveExistingRenderArea = true;
            try
            {
                var state = viewport.RenderPipelineInstance.RenderState;
                using var renderArea = state.PushRenderArea(renderRect);
                using var cropArea = state.PushCropArea(renderRect);
                viewport.Render(atlasFbo, null, null, true, ShadowAtlasMaterial);
            }
            finally
            {
                shadowPipeline.PreserveExistingRenderArea = previousPreserveArea;
            }

            return true;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Transform):
                case nameof(Distance):
                    ClampShadowNearPlaneToDistance();
                    UpdateShadowCameraClipPlanes();
                    UpdateConesFromAttachedTransform();
                    break;
                case nameof(ShadowNearPlaneDistance):
                    UpdateShadowCameraClipPlanes();
                    UpdateConesFromAttachedTransform();
                    break;
            }
        }

        private void ClampShadowNearPlaneToDistance()
        {
            float clamped = ClampShadowNearPlaneDistance(_shadowNearPlaneDistance, _distance);
            if (clamped != _shadowNearPlaneDistance)
                SetField(ref _shadowNearPlaneDistance, clamped, nameof(ShadowNearPlaneDistance));
        }

        private void UpdateShadowCameraClipPlanes()
        {
            if (ShadowCamera is null)
                return;

            ShadowCamera.NearZ = _shadowNearPlaneDistance;
            ShadowCamera.FarZ = MathF.Max(_distance, _shadowNearPlaneDistance + 0.001f);
        }

        private static float ClampShadowNearPlaneDistance(float value, float distance)
        {
            if (!float.IsFinite(value))
                value = 1.0f;

            if (distance <= 0.001f)
                return MathF.Max(0.0001f, value);

            float maxNear = MathF.Max(0.0001f, distance - 0.001f);
            return Math.Clamp(value, 0.0001f, maxNear);
        }

        private void UpdateConesFromAttachedTransform()
        {
            if (SceneNode is null || SceneNode.IsTransformNull)
                return;

            UpdateCones(Transform.RenderMatrix);
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

            UpdateConesFromAttachedTransform();
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

            SetField(ref _outerCone, new(coneOrigin, -dir, d, CalculateOuterConeRadius(d, OuterCutoffAngleDegrees)));
            SetField(ref _innerCone, new(coneOrigin, -dir, d, CalculateOuterConeRadius(d, InnerCutoffAngleDegrees)));

            UpdateShadowCameraClipPlanes();

            MeshCenterAdjustMatrix = Matrix4x4.CreateScale(OuterCone.Radius, OuterCone.Radius, OuterCone.Height) * Matrix4x4.CreateTranslation(Globals.Forward * (Distance * 0.5f));
        }

        private static float CalculateOuterConeRadius(float distance, float cutoffAngleDegrees)
        {
            if (!float.IsFinite(distance) || distance <= 0.0f)
                return 0.0f;

            float clampedDegrees = Math.Clamp(cutoffAngleDegrees, 0.0f, 89.9f);
            return MathF.Tan(DegToRad(clampedDegrees)) * distance;
        }
    }
}
