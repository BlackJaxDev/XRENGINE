using System;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Core.Attributes;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights
{
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

        public DirectionalLightComponent()
        {
            _cascadeAabbView = new(this);

            // Match the tuned runtime defaults used for live shadow-map rendering.
            SetShadowMapResolution(2048u, 2048u);
            ShadowExponentBase = 0.035f;
            ShadowExponent = 1.221f;
            ShadowMinBias = 0.00001f;
            ShadowMaxBias = 0.004f;
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

        protected override void RegisterDynamicLight(XRWorldInstance world)
            => world.Lights.DynamicDirectionalLights.Add(this);

        protected override void UnregisterDynamicLight(XRWorldInstance world)
            => world.Lights.DynamicDirectionalLights.Remove(this);

        private static bool _loggedShadowCameraOnce = false;

        public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            base.SetUniforms(program, targetStructName);

            string prefix = targetStructName ?? Engine.Rendering.Constants.LightsStructName;
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
/*
            // Debug shadow camera setup
            if (!_loggedShadowCameraOnce)
            {
                _loggedShadowCameraOnce = true;
                bool camNull = ShadowCamera is null;
                Debug.Rendering($"[DirLightShadow] ShadowCamera null={camNull}, CastsShadows={CastsShadows}, Scale={Scale}");
                if (!camNull)
                {
                    Debug.Rendering($"[DirLightShadow] lightProj diagonal: [{lightProj.M11:E3}, {lightProj.M22:E3}, {lightProj.M33:E3}, {lightProj.M44:E3}]");
                    Debug.Rendering($"[DirLightShadow] lightView diagonal: [{lightView.M11:E3}, {lightView.M22:E3}, {lightView.M33:E3}, {lightView.M44:E3}]");
                    Debug.Rendering($"[DirLightShadow] lightViewProj row0: [{lightViewProj.M11:E3}, {lightViewProj.M12:E3}, {lightViewProj.M13:E3}, {lightViewProj.M14:E3}]");
                    Debug.Rendering($"[DirLightShadow] lightViewProj row1: [{lightViewProj.M21:E3}, {lightViewProj.M22:E3}, {lightViewProj.M23:E3}, {lightViewProj.M24:E3}]");
                    Debug.Rendering($"[DirLightShadow] lightViewProj row2: [{lightViewProj.M31:E3}, {lightViewProj.M32:E3}, {lightViewProj.M33:E3}, {lightViewProj.M34:E3}]");
                    Debug.Rendering($"[DirLightShadow] lightViewProj row3: [{lightViewProj.M41:E3}, {lightViewProj.M42:E3}, {lightViewProj.M43:E3}, {lightViewProj.M44:E3}]");
                    if (ShadowCamera?.Parameters is XROrthographicCameraParameters ortho)
                        Debug.Rendering($"[DirLightShadow] OrthoParams: W={ortho.Width}, H={ortho.Height}, NearZ={ortho.NearZ}, FarZ={ortho.FarZ}");
                }
            }
*/

            program.Uniform($"{flatPrefix}WorldToLightProjMatrix", lightProj);
            program.Uniform($"{flatPrefix}WorldToLightInvViewMatrix", ShadowCamera?.Transform.WorldMatrix ?? Matrix4x4.Identity);
            program.Uniform($"{flatPrefix}WorldToLightSpaceMatrix", lightViewProj);  // Pre-computed for deferred shadow mapping

            Span<float> cascadeSplits = stackalloc float[MaxCascadeRenderCount];
            Span<float> cascadeBlendWidths = stackalloc float[MaxCascadeRenderCount];
            Span<float> cascadeBiasMins = stackalloc float[MaxCascadeRenderCount];
            Span<float> cascadeBiasMaxes = stackalloc float[MaxCascadeRenderCount];
            Span<float> cascadeReceiverOffsets = stackalloc float[MaxCascadeRenderCount];
            Span<Matrix4x4> cascadeMatrices = stackalloc Matrix4x4[MaxCascadeRenderCount];
            CopyPublishedCascadeUniformData(
                cascadeSplits,
                cascadeBlendWidths,
                cascadeBiasMins,
                cascadeBiasMaxes,
                cascadeReceiverOffsets,
                cascadeMatrices,
                out int cascadeCount);

            program.Uniform($"{flatPrefix}CascadeCount", cascadeCount);
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
