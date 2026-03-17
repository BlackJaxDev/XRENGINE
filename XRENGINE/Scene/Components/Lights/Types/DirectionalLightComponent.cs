using System;
using System.ComponentModel;
using System.Numerics;
using System.Linq;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Core.Attributes;
using XREngine.Data.Geometry;
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
        private sealed class CascadeShadowSlice
        {
            public required int CascadeIndex { get; init; }
            public required float SplitFarDistance { get; init; }
            public required Vector3 Center { get; init; }
            public required Vector3 HalfExtents { get; init; }
            public required Quaternion Orientation { get; init; }
            public required Matrix4x4 WorldToLightSpaceMatrix { get; init; }
        }

        public readonly record struct CascadedShadowAabb(
            int FrustumIndex,
            int CascadeIndex,
            Vector3 Center,
            Vector3 HalfExtents,
            Quaternion Orientation);

        private const float NearZ = 0.01f;
        private const int MaxCascadeRenderCount = 8;
        private const float CascadeBoundsPadding = 0.05f;

        private Vector3 _scale = Vector3.One;
        private int _cascadeCount = 4;
        private float[] _cascadePercentages = [0.1f, 0.2f, 0.3f, 0.4f];
        private float _cascadeOverlapPercent = 0.1f;
        private bool _debugCascadeColors;
        private readonly List<CascadedShadowAabb> _cascadeAabbs = new(4);
        private readonly List<CascadeShadowSlice> _cascadeShadowSlices = new(MaxCascadeRenderCount);
        private XRTexture2DArray? _cascadeShadowMapTexture;
        private XRFrameBuffer[] _cascadeShadowFrameBuffers = [];
        private XRViewport[] _cascadeShadowViewports = [];
        private Transform[] _cascadeShadowTransforms = [];
        private XRCamera[] _cascadeShadowCameras = [];
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
        /// When true, the shader replaces lighting output with a per-cascade color overlay for debugging.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Debug Cascade Colors")]
        [Description("When enabled, each cascade is tinted with a distinct color in the viewport for visual debugging.")]
        public bool DebugCascadeColors
        {
            get => _debugCascadeColors;
            set => SetField(ref _debugCascadeColors, value);
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

        public XRTexture2DArray? CascadedShadowMapTexture => _cascadeShadowMapTexture;
        public int ActiveCascadeCount => _cascadeShadowSlices.Count;

        public float GetCascadeSplit(int index)
            => index >= 0 && index < _cascadeShadowSlices.Count
                ? _cascadeShadowSlices[index].SplitFarDistance
                : float.MaxValue;

        public Matrix4x4 GetCascadeMatrix(int index)
            => index >= 0 && index < _cascadeShadowSlices.Count
                ? _cascadeShadowSlices[index].WorldToLightSpaceMatrix
                : Matrix4x4.Identity;

        public Vector3 GetCascadeCenter(int index)
            => index >= 0 && index < _cascadeShadowSlices.Count
                ? _cascadeShadowSlices[index].Center
                : Vector3.Zero;

        public Vector3 GetCascadeHalfExtents(int index)
            => index >= 0 && index < _cascadeShadowSlices.Count
                ? _cascadeShadowSlices[index].HalfExtents
                : Vector3.Zero;

        public XRCamera? GetCascadeCamera(int index)
            => index >= 0 && index < _cascadeShadowCameras.Length
                ? _cascadeShadowCameras[index]
                : null;

        public XRViewport? GetCascadeViewport(int index)
            => index >= 0 && index < _cascadeShadowViewports.Length
                ? _cascadeShadowViewports[index]
                : null;

        public XRFrameBuffer? GetCascadeFrameBuffer(int index)
            => index >= 0 && index < _cascadeShadowFrameBuffers.Length
                ? _cascadeShadowFrameBuffers[index]
                : null;

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

        private static Frustum CreateCascadeViewFrustum(Frustum cameraFrustum, float cameraNear, float cameraFar, float sliceNear, float sliceFar)
        {
            float totalDepth = MathF.Max(cameraFar - cameraNear, 1e-4f);
            float nearT = Math.Clamp((sliceNear - cameraNear) / totalDepth, 0.0f, 1.0f);
            float farT = Math.Clamp((sliceFar - cameraNear) / totalDepth, 0.0f, 1.0f);

            Vector3 Lerp(Vector3 nearCorner, Vector3 farCorner, float t)
                => Vector3.Lerp(nearCorner, farCorner, t);

            return new Frustum(
                Lerp(cameraFrustum.LeftBottomNear, cameraFrustum.LeftBottomFar, nearT),
                Lerp(cameraFrustum.RightBottomNear, cameraFrustum.RightBottomFar, nearT),
                Lerp(cameraFrustum.LeftTopNear, cameraFrustum.LeftTopFar, nearT),
                Lerp(cameraFrustum.RightTopNear, cameraFrustum.RightTopFar, nearT),
                Lerp(cameraFrustum.LeftBottomNear, cameraFrustum.LeftBottomFar, farT),
                Lerp(cameraFrustum.RightBottomNear, cameraFrustum.RightBottomFar, farT),
                Lerp(cameraFrustum.LeftTopNear, cameraFrustum.LeftTopFar, farT),
                Lerp(cameraFrustum.RightTopNear, cameraFrustum.RightTopFar, farT));
        }

        private void BuildLightSpaceBasis(out Matrix4x4 worldToLight, out Matrix4x4 lightToWorld, out Quaternion lightRotation, out Vector3 lightDir)
        {
            lightDir = Transform.WorldForward;
            if (lightDir.LengthSquared() < 1e-6f)
                lightDir = Vector3.UnitZ;
            lightDir = Vector3.Normalize(lightDir);

            Vector3 up = Transform.WorldUp;
            if (MathF.Abs(Vector3.Dot(lightDir, up)) > 0.99f)
                up = Vector3.UnitX;

            Vector3 right = Vector3.Normalize(Vector3.Cross(up, lightDir));
            up = Vector3.Normalize(Vector3.Cross(lightDir, right));

            // World-to-light: rows are right, up, lightDir (light Z points along light direction).
            worldToLight = new(
                right.X, right.Y, right.Z, 0,
                up.X, up.Y, up.Z, 0,
                lightDir.X, lightDir.Y, lightDir.Z, 0,
                0, 0, 0, 1);

            Matrix4x4.Invert(worldToLight, out lightToWorld);
            lightRotation = Quaternion.CreateFromRotationMatrix(lightToWorld);
        }

        private void EnsureCascadeShadowResources()
        {
            if (!CastsShadows)
                return;

            int requiredCascades = Math.Clamp(_cascadeCount, 1, MaxCascadeRenderCount);
            uint width = Math.Max(1u, ShadowMapResolutionWidth);
            uint height = Math.Max(1u, ShadowMapResolutionHeight);

            bool recreateTexture = _cascadeShadowMapTexture is null ||
                _cascadeShadowMapTexture.Depth != (uint)requiredCascades ||
                _cascadeShadowMapTexture.Width != width ||
                _cascadeShadowMapTexture.Height != height;

            if (recreateTexture)
            {
                _cascadeShadowMapTexture?.Destroy();
                _cascadeShadowMapTexture = XRTexture2DArray.CreateFrameBufferTexture(
                    (uint)requiredCascades,
                    width,
                    height,
                    GetShadowDepthMapFormat(EDepthPrecision.Int24),
                    EPixelFormat.DepthComponent,
                    EPixelType.Float,
                    EFrameBufferAttachment.DepthAttachment);
                _cascadeShadowMapTexture.SamplerName = "ShadowMapArray";
            }

            if (_cascadeShadowFrameBuffers.Length == requiredCascades && !recreateTexture)
                return;

            XRWorldInstance? world = WorldAs<XRWorldInstance>();
            _cascadeShadowFrameBuffers = new XRFrameBuffer[requiredCascades];
            _cascadeShadowViewports = new XRViewport[requiredCascades];
            _cascadeShadowTransforms = new Transform[requiredCascades];
            _cascadeShadowCameras = new XRCamera[requiredCascades];

            for (int i = 0; i < requiredCascades; i++)
            {
                var transform = new Transform
                {
                    Order = XREngine.Animation.ETransformOrder.TRS,
                };

                XROrthographicCameraParameters parameters = new(1.0f, 1.0f, NearZ, 1.0f);
                parameters.SetOriginPercentages(0.5f, 0.5f);
                var camera = new XRCamera(transform, parameters);
                var viewport = new XRViewport(null, width, height)
                {
                    RenderPipeline = new ShadowRenderPipeline(),
                    SetRenderPipelineFromCamera = false,
                    AutomaticallyCollectVisible = false,
                    AutomaticallySwapBuffers = false,
                    AllowUIRender = false,
                    CullWithFrustum = true,
                    WorldInstanceOverride = world,
                    Camera = camera,
                };

                _cascadeShadowTransforms[i] = transform;
                _cascadeShadowCameras[i] = camera;
                _cascadeShadowViewports[i] = viewport;
                _cascadeShadowFrameBuffers[i] = new XRFrameBuffer((_cascadeShadowMapTexture!, EFrameBufferAttachment.DepthAttachment, 0, i));
            }
        }

        private void ReleaseCascadeShadowResources()
        {
            _cascadeShadowSlices.Clear();
            _cascadeAabbs.Clear();

            for (int i = 0; i < _cascadeShadowViewports.Length; i++)
            {
                _cascadeShadowViewports[i].WorldInstanceOverride = null;
                _cascadeShadowViewports[i].Camera = null;
            }

            _cascadeShadowMapTexture?.Destroy();
            _cascadeShadowMapTexture = null;
            _cascadeShadowFrameBuffers = [];
            _cascadeShadowViewports = [];
            _cascadeShadowTransforms = [];
            _cascadeShadowCameras = [];
        }

        private void UpdateCascadeShadowCamera(int slot, Vector3 center, Vector3 halfExtents, Quaternion orientation, Vector3 lightDirection)
        {
            Transform transform = _cascadeShadowTransforms[slot];
            transform.Translation = center - lightDirection * halfExtents.Z;
            transform.Rotation = orientation;

            XRCamera camera = _cascadeShadowCameras[slot];
            float width = MathF.Max(halfExtents.X * 2.0f, 1e-3f);
            float height = MathF.Max(halfExtents.Y * 2.0f, 1e-3f);
            float depth = MathF.Max(halfExtents.Z * 2.0f, NearZ + 1e-3f);
            if (camera.Parameters is not XROrthographicCameraParameters ortho)
            {
                ortho = new XROrthographicCameraParameters(width, height, NearZ, depth - NearZ);
                ortho.SetOriginPercentages(0.5f, 0.5f);
                camera.Parameters = ortho;
            }
            else
            {
                ortho.Width = width;
                ortho.Height = height;
                ortho.NearZ = NearZ;
                ortho.FarZ = depth - NearZ;
            }
        }

        internal void UpdateCascadeShadows(XRCamera playerCamera)
        {
            _cascadeAabbs.Clear();
            _cascadeShadowSlices.Clear();

            if (!CastsShadows || !EnableCascadedShadows || ShadowCamera is null)
                return;

            EnsureCascadeShadowResources();
            if (_cascadeShadowMapTexture is null || _cascadeShadowCameras.Length == 0)
                return;

            Frustum playerFrustum = playerCamera.WorldFrustum();

            BuildLightSpaceBasis(out Matrix4x4 worldToLight, out Matrix4x4 lightToWorld, out Quaternion lightRotation, out Vector3 lightDirection);

            // Use Scale.Z as the shadow depth: how far backward along the light direction
            // to extend each cascade so we capture shadow casters behind the visible slice.
            float shadowDepth = MathF.Max(Scale.Z, 1.0f);

            float[] percentages = GetEffectiveCascadePercentages();
            float cameraNear = playerCamera.NearZ;
            float cameraFar = playerCamera.FarZ;
            float totalDepth = MathF.Max(cameraFar - cameraNear, 1e-4f);
            float cumulative = 0.0f;
            int resourceSlot = 0;

            for (int cascadeIndex = 0; cascadeIndex < Math.Min(percentages.Length, _cascadeShadowCameras.Length); cascadeIndex++)
            {
                float pct = percentages[cascadeIndex];
                if (pct <= 0.0f)
                    continue;

                float splitStart = cameraNear + totalDepth * cumulative;
                float splitEnd = splitStart + totalDepth * pct;
                cumulative += pct;

                float sliceDepth = splitEnd - splitStart;
                float expand = sliceDepth * _cascadeOverlapPercent * 0.5f;
                float expandedStart = MathF.Max(cameraNear, splitStart - expand);
                float expandedEnd = MathF.Min(cameraFar, splitEnd + expand);

                // Create the camera frustum slice for this cascade
                Frustum cascadeFrustum = CreateCascadeViewFrustum(playerFrustum, cameraNear, cameraFar, expandedStart, expandedEnd);

                // Project all 8 corners of the slice into light space to compute a tight ortho bounding box
                Vector3 min = new(float.MaxValue);
                Vector3 max = new(float.MinValue);
                for (int i = 0; i < cascadeFrustum.Corners.Count; i++)
                {
                    Vector3 point = Vector3.Transform(cascadeFrustum.Corners[i], worldToLight);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }

                // Extend Z backward along the light direction to capture shadow casters
                // that are behind the visible slice but still cast into it.
                min.Z -= shadowDepth;

                if (max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z)
                    continue;

                Vector3 centerLS = (min + max) * 0.5f;
                Vector3 halfExtents = Vector3.Max((max - min) * 0.5f, new Vector3(1e-3f, 1e-3f, NearZ + 1e-3f));
                Vector3 centerWS = Vector3.Transform(centerLS, lightToWorld);

                UpdateCascadeShadowCamera(resourceSlot, centerWS, halfExtents, lightRotation, lightDirection);

                Matrix4x4 cascadeView = _cascadeShadowCameras[resourceSlot].Transform.InverseRenderMatrix;
                Matrix4x4 cascadeProj = _cascadeShadowCameras[resourceSlot].ProjectionMatrix;
                Matrix4x4 viewProj = cascadeView * cascadeProj;

                _cascadeShadowSlices.Add(new CascadeShadowSlice
                {
                    CascadeIndex = cascadeIndex,
                    SplitFarDistance = splitEnd,
                    Center = centerWS,
                    HalfExtents = halfExtents,
                    Orientation = lightRotation,
                    WorldToLightSpaceMatrix = viewProj,
                });

                _cascadeAabbs.Add(new CascadedShadowAabb(0, cascadeIndex, centerWS, halfExtents, lightRotation));
                resourceSlot++;
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

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            EnsureCascadeShadowResources();
            for (int i = 0; i < _cascadeShadowViewports.Length; i++)
                _cascadeShadowViewports[i].WorldInstanceOverride = WorldAs<XREngine.Rendering.XRWorldInstance>();
            if (Type == ELightType.Dynamic)
                WorldAs<XREngine.Rendering.XRWorldInstance>()?.Lights.DynamicDirectionalLights.Add(this);
        }
        protected override void OnComponentDeactivated()
        {
            ReleaseCascadeShadowResources();
            if (Type == ELightType.Dynamic)
                WorldAs<XREngine.Rendering.XRWorldInstance>()?.Lights.DynamicDirectionalLights.Remove(this);
            base.OnComponentDeactivated();
        }

        public override void SetShadowMapResolution(uint width, uint height)
        {
            base.SetShadowMapResolution(width, height);
            EnsureCascadeShadowResources();
        }

        public override void CollectVisibleItems()
        {
            if (!CastsShadows)
                return;

            if (ShadowMap is not null)
                _viewport.CollectVisible(false);

            for (int i = 0; i < _cascadeShadowSlices.Count && i < _cascadeShadowViewports.Length; i++)
                _cascadeShadowViewports[i].CollectVisible(false);
        }

        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!CastsShadows)
                return;

            if (ShadowMap is not null)
                _viewport.SwapBuffers();

            for (int i = 0; i < _cascadeShadowSlices.Count && i < _cascadeShadowViewports.Length; i++)
                _cascadeShadowViewports[i].SwapBuffers();

            lightmapBaker?.ProcessDynamicCachedAutoBake(this);
        }

        public override void RenderShadowMap(bool collectVisibleNow = false)
        {
            if (!CastsShadows)
                return;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            if (ShadowMap is not null)
                _viewport.Render(ShadowMap, null, null, true, ShadowMap.Material);

            if (ShadowMap?.Material is null)
                return;

            for (int i = 0; i < _cascadeShadowSlices.Count && i < _cascadeShadowViewports.Length && i < _cascadeShadowFrameBuffers.Length; i++)
                _cascadeShadowViewports[i].Render(_cascadeShadowFrameBuffers[i], null, null, true, ShadowMap.Material);
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

            program.Uniform($"{flatPrefix}WorldToLightProjMatrix", lightProj);
            program.Uniform($"{flatPrefix}WorldToLightInvViewMatrix", ShadowCamera?.Transform.WorldMatrix ?? Matrix4x4.Identity);
            program.Uniform($"{flatPrefix}WorldToLightSpaceMatrix", lightViewProj);  // Pre-computed for deferred shadow mapping

            program.Uniform($"{flatPrefix}CascadeCount", _cascadeShadowSlices.Count);
            for (int i = 0; i < MaxCascadeRenderCount; i++)
            {
                float split = i < _cascadeShadowSlices.Count ? _cascadeShadowSlices[i].SplitFarDistance : float.MaxValue;
                Matrix4x4 cascadeMatrix = i < _cascadeShadowSlices.Count ? _cascadeShadowSlices[i].WorldToLightSpaceMatrix : Matrix4x4.Identity;
                program.Uniform($"{flatPrefix}CascadeSplits[{i}]", split);
                program.Uniform($"{flatPrefix}CascadeMatrices[{i}]", cascadeMatrix);
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
                case nameof(CascadeCount):
                    EnsureCascadeShadowResources();
                    break;
                case nameof(CastsShadows):
                    if (CastsShadows)
                        EnsureCascadeShadowResources();
                    else
                        ReleaseCascadeShadowResources();
                    break;
                case nameof(Type):
                    if (Type == ELightType.Dynamic)
                        WorldAs<XREngine.Rendering.XRWorldInstance>()?.Lights.DynamicDirectionalLights.Add(this);
                    else
                        WorldAs<XREngine.Rendering.XRWorldInstance>()?.Lights.DynamicDirectionalLights.Remove(this);
                    break;
            }
        }
    }
}
