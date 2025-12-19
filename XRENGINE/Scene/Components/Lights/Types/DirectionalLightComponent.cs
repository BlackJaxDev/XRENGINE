using System;
using System.ComponentModel;
using System.Numerics;
using System.Linq;
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
    public class DirectionalLightComponent : OneViewLightComponent
    {
        public readonly record struct CascadedShadowAabb(
            int FrustumIndex,
            int CascadeIndex,
            Vector3 Center,
            Vector3 HalfExtents,
            Quaternion Orientation);

        private const float NearZ = 0.01f;

        private Vector3 _scale = Vector3.One;
        private int _cascadeCount = 4;
        private float[] _cascadePercentages = [0.1f, 0.2f, 0.3f, 0.4f];
        private float _cascadeOverlapPercent = 0.1f;
        private readonly List<CascadedShadowAabb> _cascadeAabbs = new(4);
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
        /// Number of cascaded shadow map splits to generate within the camera/light intersection AABB.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Count")]
        public int CascadeCount
        {
            get => _cascadeCount;
            set
            {
                int clamped = Math.Clamp(value, 1, 8);
                if (SetField(ref _cascadeCount, clamped))
                    NormalizeCascadePercentages();
            }
        }

        /// <summary>
        /// Symmetric overlap applied to each cascade slice along the forward axis (0-1 of slice length).
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Overlap %")]
        public float CascadeOverlapPercent
        {
            get => _cascadeOverlapPercent;
            set => SetField(ref _cascadeOverlapPercent, Math.Clamp(value, 0.0f, 1.0f));
        }

        /// <summary>
        /// Percentages (should sum to 1) allocated to each cascade along the camera forward axis.
        /// Length is clamped/expanded to match CascadeCount and normalized on assignment.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Cascade Percentages")]
        public float[] CascadePercentages
        {
            get => [.. _cascadePercentages];
            set => SetCascadePercentages(value);
        }

        /// <summary>
        /// Cascaded shadow AABBs derived from the current camera/light intersection.
        /// </summary>
        public IReadOnlyList<CascadedShadowAabb> CascadedShadowAabbs => _cascadeAabbs;

        public static XRMesh GetVolumeMesh()
            => XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f));
        protected override XRMesh GetWireframeMesh()
            => XRMesh.Shapes.WireframeBox(new Vector3(-0.5f), new Vector3(0.5f));

        private static float[] CreateUniformPercentages(int count)
        {
            if (count <= 0)
                return [];

            float uniform = 1.0f / count;
            float[] result = new float[count];
            for (int i = 0; i < count; i++)
                result[i] = uniform;
            return result;
        }

        private void SetCascadePercentages(float[]? value)
        {
            float[] next;
            if (value is null || value.Length == 0)
            {
                next = CreateUniformPercentages(_cascadeCount);
            }
            else
            {
                next = [.. value];
            }

            if (next.Length != _cascadeCount)
                Array.Resize(ref next, _cascadeCount);

            float sum = next.Take(_cascadeCount).Select(MathF.Abs).Sum();
            if (sum <= float.Epsilon)
                next = CreateUniformPercentages(_cascadeCount);
            else
            {
                for (int i = 0; i < _cascadeCount; i++)
                    next[i] = MathF.Abs(next[i]) / sum;
            }

            SetField(ref _cascadePercentages, next, nameof(CascadePercentages));
        }

        private void NormalizeCascadePercentages()
        {
            if (_cascadePercentages.Length != _cascadeCount)
                Array.Resize(ref _cascadePercentages, _cascadeCount);

            float sum = _cascadePercentages.Take(_cascadeCount).Select(MathF.Abs).Sum();
            if (sum <= float.Epsilon)
            {
                _cascadePercentages = CreateUniformPercentages(_cascadeCount);
                return;
            }

            for (int i = 0; i < _cascadeCount; i++)
                _cascadePercentages[i] = MathF.Abs(_cascadePercentages[i]) / sum;
        }

        private float[] GetEffectiveCascadePercentages()
        {
            if (_cascadePercentages.Length != _cascadeCount)
                NormalizeCascadePercentages();

            float sum = _cascadePercentages.Take(_cascadeCount).Sum();
            if (sum <= float.Epsilon)
                return CreateUniformPercentages(_cascadeCount);

            float[] result = new float[_cascadeCount];
            for (int i = 0; i < _cascadeCount; i++)
                result[i] = _cascadePercentages[i] / sum;
            return result;
        }

        internal void UpdateCascadeAabbs(Vector3 cameraForward)
        {
            _cascadeAabbs.Clear();

            if (!CastsShadows || CameraIntersections.Count == 0)
                return;

            Vector3 fwd = cameraForward;
            if (fwd.LengthSquared() < 1e-6f)
                fwd = Vector3.UnitZ;
            fwd = Vector3.Normalize(fwd);

            // Build light-space basis: Z = light direction, Y = world up (fallback X if nearly parallel).
            Vector3 lightDir = Transform.WorldForward;
            if (lightDir.LengthSquared() < 1e-6f)
                lightDir = Vector3.UnitZ;
            lightDir = Vector3.Normalize(lightDir);

            Vector3 up = Transform.WorldUp;
            if (MathF.Abs(Vector3.Dot(lightDir, up)) > 0.99f)
                up = Vector3.UnitX;

            Vector3 right = Vector3.Normalize(Vector3.Cross(up, lightDir));
            up = Vector3.Normalize(Vector3.Cross(lightDir, right));

            // World-to-light: rows are right, up, lightDir (light Z points along light direction).
            Matrix4x4 worldToLight = new(
                right.X, right.Y, right.Z, 0,
                up.X, up.Y, up.Z, 0,
                lightDir.X, lightDir.Y, lightDir.Z, 0,
                0, 0, 0, 1);

            Matrix4x4.Invert(worldToLight, out Matrix4x4 lightToWorld);
            Quaternion lightRotation = Quaternion.CreateFromRotationMatrix(lightToWorld);

            float[] percentages = GetEffectiveCascadePercentages();
            float overlap = _cascadeOverlapPercent;

            Span<Vector3> cornersWS = stackalloc Vector3[8];
            Span<Vector3> cornersLS = stackalloc Vector3[8];
            foreach (var intersection in CameraIntersections)
            {
                Vector3 min = intersection.Min;
                Vector3 max = intersection.Max;

                // Build 8 corners of intersection AABB.
                cornersWS[0] = new Vector3(min.X, min.Y, min.Z);
                cornersWS[1] = new Vector3(min.X, min.Y, max.Z);
                cornersWS[2] = new Vector3(min.X, max.Y, min.Z);
                cornersWS[3] = new Vector3(min.X, max.Y, max.Z);
                cornersWS[4] = new Vector3(max.X, min.Y, min.Z);
                cornersWS[5] = new Vector3(max.X, min.Y, max.Z);
                cornersWS[6] = new Vector3(max.X, max.Y, min.Z);
                cornersWS[7] = new Vector3(max.X, max.Y, max.Z);

                // Transform corners to light space.
                for (int i = 0; i < cornersWS.Length; i++)
                    cornersLS[i] = Vector3.Transform(cornersWS[i], worldToLight);

                // Compute tight axis-aligned bounds in light space.
                Vector3 lsMin = new(float.MaxValue);
                Vector3 lsMax = new(float.MinValue);
                for (int i = 0; i < cornersLS.Length; i++)
                {
                    lsMin = Vector3.Min(lsMin, cornersLS[i]);
                    lsMax = Vector3.Max(lsMax, cornersLS[i]);
                }

                Vector3 lsCenter = (lsMin + lsMax) * 0.5f;
                Vector3 lsHalfExtents = (lsMax - lsMin) * 0.5f;

                // Slice axis: camera forward transformed to light space, then project onto XY plane of light.
                Vector3 fwdLS = Vector3.TransformNormal(fwd, worldToLight);
                // Zero out the light-Z component so slicing is perpendicular to light direction.
                fwdLS.Z = 0;
                if (fwdLS.LengthSquared() < 1e-6f)
                    fwdLS = Vector3.UnitX; // fallback if camera looks along light
                fwdLS = Vector3.Normalize(fwdLS);

                // Project light-space corners onto slice axis to find span.
                float projMin = float.MaxValue;
                float projMax = float.MinValue;
                for (int i = 0; i < cornersLS.Length; i++)
                {
                    float p = Vector3.Dot(cornersLS[i], fwdLS);
                    projMin = MathF.Min(projMin, p);
                    projMax = MathF.Max(projMax, p);
                }

                float projLen = MathF.Max(projMax - projMin, 1e-4f);

                // Perpendicular half-extent (axis orthogonal to slice within XY).
                Vector3 perpLS = new(-fwdLS.Y, fwdLS.X, 0);
                float perpMin = float.MaxValue;
                float perpMax = float.MinValue;
                for (int i = 0; i < cornersLS.Length; i++)
                {
                    float p = Vector3.Dot(cornersLS[i], perpLS);
                    perpMin = MathF.Min(perpMin, p);
                    perpMax = MathF.Max(perpMax, p);
                }
                float halfPerp = (perpMax - perpMin) * 0.5f;
                float halfZ = lsHalfExtents.Z; // full depth along light direction

                // Center along perpendicular and Z axes.
                float centerPerp = (perpMin + perpMax) * 0.5f;
                float centerZ = lsCenter.Z;

                float cumulative = 0.0f;
                for (int cascadeIndex = 0; cascadeIndex < _cascadeCount; cascadeIndex++)
                {
                    float pct = percentages[cascadeIndex];
                    if (pct <= 0.0f)
                        continue;

                    float segStart = projMin + projLen * cumulative;
                    float segEnd = segStart + projLen * pct;
                    cumulative += pct;

                    // Apply symmetric overlap, clamped within overall span.
                    float segLen = segEnd - segStart;
                    float expand = segLen * overlap * 0.5f;
                    segStart = MathF.Max(projMin, segStart - expand);
                    segEnd = MathF.Min(projMax, segEnd + expand);

                    float centerSlice = (segStart + segEnd) * 0.5f;
                    float halfSlice = MathF.Max((segEnd - segStart) * 0.5f, 1e-6f);

                    // Reconstruct center in light space.
                    Vector3 centerLS = fwdLS * centerSlice + perpLS * centerPerp + new Vector3(0, 0, centerZ);
                    // Half-extents: slice direction, perpendicular, light depth.
                    Vector3 halfExtentsLS = new(halfSlice, halfPerp, halfZ);

                    // Convert center back to world space.
                    Vector3 centerWS = Vector3.Transform(centerLS, lightToWorld);

                    _cascadeAabbs.Add(new CascadedShadowAabb(
                        intersection.FrustumIndex,
                        cascadeIndex,
                        centerWS,
                        halfExtentsLS,
                        lightRotation));
                }
            }
        }

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

            // Debug shadow camera setup
            if (!_loggedShadowCameraOnce)
            {
                _loggedShadowCameraOnce = true;
                bool camNull = ShadowCamera is null;
                Debug.Out($"[DirLightShadow] ShadowCamera null={camNull}, CastsShadows={CastsShadows}, Scale={Scale}");
                if (!camNull)
                {
                    Debug.Out($"[DirLightShadow] lightProj diagonal: [{lightProj.M11:E3}, {lightProj.M22:E3}, {lightProj.M33:E3}, {lightProj.M44:E3}]");
                    Debug.Out($"[DirLightShadow] lightView diagonal: [{lightView.M11:E3}, {lightView.M22:E3}, {lightView.M33:E3}, {lightView.M44:E3}]");
                    Debug.Out($"[DirLightShadow] lightViewProj row0: [{lightViewProj.M11:E3}, {lightViewProj.M12:E3}, {lightViewProj.M13:E3}, {lightViewProj.M14:E3}]");
                    Debug.Out($"[DirLightShadow] lightViewProj row1: [{lightViewProj.M21:E3}, {lightViewProj.M22:E3}, {lightViewProj.M23:E3}, {lightViewProj.M24:E3}]");
                    Debug.Out($"[DirLightShadow] lightViewProj row2: [{lightViewProj.M31:E3}, {lightViewProj.M32:E3}, {lightViewProj.M33:E3}, {lightViewProj.M34:E3}]");
                    Debug.Out($"[DirLightShadow] lightViewProj row3: [{lightViewProj.M41:E3}, {lightViewProj.M42:E3}, {lightViewProj.M43:E3}, {lightViewProj.M44:E3}]");
                    if (ShadowCamera?.Parameters is XROrthographicCameraParameters ortho)
                        Debug.Out($"[DirLightShadow] OrthoParams: W={ortho.Width}, H={ortho.Height}, NearZ={ortho.NearZ}, FarZ={ortho.FarZ}");
                }
            }

            program.Uniform($"{flatPrefix}WorldToLightProjMatrix", lightProj);
            program.Uniform($"{flatPrefix}WorldToLightInvViewMatrix", ShadowCamera?.Transform.WorldMatrix ?? Matrix4x4.Identity);
            program.Uniform($"{flatPrefix}WorldToLightSpaceMatrix", lightViewProj);  // Pre-computed for deferred shadow mapping

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
