using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Extensions;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shadows;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture.Lights.Types
{
    /// <summary>
    /// Local omnidirectional light backed by six cubemap shadow cameras.
    /// </summary>
    [XRComponentEditor("XREngine.Editor.ComponentEditors.PointLightComponentEditor")]
    [Category("Lighting")]
    [DisplayName("Point Light")]
    [Description("Emits omnidirectional light with optional shadow maps for local illumination.")]
    public class PointLightComponent : LightComponent
    {
        public const int ShadowFaceCount = 6;

        private XRViewport[] _viewports = [];
        private XRCamera[] _shadowCameras = [];

        private XRFrameBuffer? _perFaceFbo;
        private XRMaterial? _shadowAtlasMaterial;
        private XRMaterial? _pointGeometryShadowMaterial;
        private XRMaterial? _pointInstancedShadowMaterial;
        private XRMaterial? _pointAtlasGeometryShadowMaterial;
        private XRMaterial? _pointAtlasInstancedShadowMaterial;
        private const float PointShadowNearPlaneDistanceDefault = 0.1f;
        private readonly PositionOnlyTransform _shadowCameraParentTransform = new();
        private float _shadowNearPlaneDistance = PointShadowNearPlaneDistanceDefault;
        private readonly PointShadowAtlasFaceSlot[] _atlasFaceSlots = new PointShadowAtlasFaceSlot[ShadowFaceCount];
        private readonly BoundingRectangle[] _groupedAtlasClearRects = new BoundingRectangle[ShadowFaceCount];
        private EPointShadowRenderMode _shadowRenderMode = EPointShadowRenderMode.InstancedLayered;
        private EPointShadowRenderMode _effectiveShadowRenderMode = EPointShadowRenderMode.InstancedLayered;
        private PointShadowRenderFallbackReason _shadowRenderFallbackReason = PointShadowRenderFallbackReason.None;
        private int _shadowFaceRelevanceMask = LocalShadowFrustumRelevance.AllPointFacesMask;
        private int _lastRenderedShadowFaceMask;
        private ulong _lastShadowRelevanceFrame;
        private ulong _lastShadowRenderFrame;

        private static readonly Vector3[] ShadowFaceForwardVectors =
        [
            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitZ,
            -Vector3.UnitZ,
        ];

        private static readonly string[] ViewProjectionMatrixUniformNames =
        [
            "ViewProjectionMatrices[0]",
            "ViewProjectionMatrices[1]",
            "ViewProjectionMatrices[2]",
            "ViewProjectionMatrices[3]",
            "ViewProjectionMatrices[4]",
            "ViewProjectionMatrices[5]",
        ];

        private static readonly string[] PointShadowViewProjectionMatrixUniformNames =
        [
            "PointShadowViewProjectionMatrices[0]",
            "PointShadowViewProjectionMatrices[1]",
            "PointShadowViewProjectionMatrices[2]",
            "PointShadowViewProjectionMatrices[3]",
            "PointShadowViewProjectionMatrices[4]",
            "PointShadowViewProjectionMatrices[5]",
        ];

        private static readonly string[] PointShadowFaceIndexUniformNames =
        [
            "PointShadowFaceIndices[0]",
            "PointShadowFaceIndices[1]",
            "PointShadowFaceIndices[2]",
            "PointShadowFaceIndices[3]",
            "PointShadowFaceIndices[4]",
            "PointShadowFaceIndices[5]",
        ];

        public readonly record struct PointShadowAtlasFaceSlot(
            bool HasAllocation,
            bool IsResident,
            int PageIndex,
            int RecordIndex,
            Vector4 UvScaleBias,
            float NearPlane,
            float FarPlane,
            float TexelSize,
            float ResolutionScale,
            uint Resolution,
            ShadowFallbackMode Fallback,
            BoundingRectangle PixelRect,
            BoundingRectangle InnerPixelRect,
            ulong LastRenderedFrame);

        private enum PointShadowRenderFallbackReason
        {
            None = 0,
            SequentialRequested = 1,
            AtlasUsesSequentialTiles = 2,
            MissingShadowMap = 3,
            UnsupportedLayeredFramebuffer = 4,
            UnsupportedGeometryShader = 5,
            UnsupportedVertexStageLayerWrites = 6,
            UnsupportedViewportArray = 7,
            MissingGroupedAtlasAllocation = 8,
            UnsupportedViewportScissorArray = 9,
            UnsupportedVertexStageViewportIndexWrites = 10,
            UnsupportedGeometryStageViewportIndexWrites = 11,
        }

        private readonly struct PointShadowRenderPlan
        {
            public required EPointShadowRenderMode RequestedMode { get; init; }
            public required EPointShadowRenderMode SelectedMode { get; init; }
            public required PointShadowRenderFallbackReason FallbackReason { get; init; }

            public bool IsLayered => SelectedMode is EPointShadowRenderMode.GeometryShader or EPointShadowRenderMode.InstancedLayered;
            public bool IsInstancedLayered => SelectedMode == EPointShadowRenderMode.InstancedLayered;
        }

        /// <summary>
        /// Selects the render strategy for the legacy cubemap shadow path.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Point Shadow Render Mode")]
        [Description("Controls whether cubemap faces render as sequential passes, an instanced/layered path, or a geometry-shader layered pass.")]
        public EPointShadowRenderMode ShadowRenderMode
        {
            get => _shadowRenderMode;
            set => SetField(ref _shadowRenderMode, value);
        }

        /// <summary>
        /// Most recent render mode selected for the legacy point-light cubemap shadow path.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Effective Point Shadow Render Mode")]
        [Description("Resolved point shadow render path after backend capability checks and fallback handling.")]
        public EPointShadowRenderMode EffectiveShadowRenderMode => _effectiveShadowRenderMode;

        /// <summary>
        /// Most recent fallback reason for the legacy point-light cubemap shadow path.
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Point Shadow Render Fallback")]
        [Description("Reason the requested point shadow render mode could not be used in the most recent shadow pass.")]
        public string ShadowRenderFallbackReason => _shadowRenderFallbackReason.ToString();

        [Category("Shadows")]
        [DisplayName("Shadow Face Relevance Mask")]
        [Description("Six-bit mask of point shadow faces considered relevant to the current camera set.")]
        public int ShadowFaceRelevanceMask => _shadowFaceRelevanceMask;

        [Category("Shadows")]
        [DisplayName("Last Rendered Shadow Face Mask")]
        [Description("Six-bit mask of point shadow faces rendered by the most recent legacy cubemap shadow pass.")]
        public int LastRenderedShadowFaceMask => _lastRenderedShadowFaceMask;

        [Browsable(false)]
        public ulong LastShadowRelevanceFrame => _lastShadowRelevanceFrame;

        [Browsable(false)]
        public ulong LastShadowRenderFrame => _lastShadowRenderFrame;

        [Category("Shadows")]
        [DisplayName("Shadow Near Plane")]
        [Description("Near clipping distance used by the point-light cubemap shadow cameras.")]
        public float ShadowNearPlaneDistance
        {
            get => _shadowNearPlaneDistance;
            set
            {
                float clamped = ClampShadowNearPlaneDistance(value, _influenceVolume.Radius);
                if (!SetField(ref _shadowNearPlaneDistance, clamped))
                    return;

                foreach (XRCamera cam in _shadowCameras)
                    cam.NearZ = clamped;
            }
        }

        /// <summary>
        /// Shadow cameras for the cubemap faces, ordered by the engine cubemap face convention.
        /// </summary>
        public XRCamera[] ShadowCameras => _shadowCameras;

        [Browsable(false)]
        public bool UsesPointShadowAtlasForCurrentEncoding
        {
            get
            {
                if (!RuntimeEngine.Rendering.Settings.UsePointShadowAtlas)
                    return false;

                return ResolveShadowMapFormat(preferredStorageFormat: ShadowMapStorageFormat).Encoding == EShadowMapEncoding.Depth;
            }
        }

        protected override bool UsesAtlasOnlyShadowMapResource
            => UsesPointShadowAtlasForCurrentEncoding;

        internal static Vector3 GetShadowFaceForward(int faceIndex)
            => (uint)faceIndex < (uint)ShadowFaceForwardVectors.Length
                ? ShadowFaceForwardVectors[faceIndex]
                : Vector3.UnitZ;

        internal void SetShadowFaceRelevanceMask(int faceMask, ulong frameId)
        {
            int clampedMask = faceMask & LocalShadowFrustumRelevance.AllPointFacesMask;
            SetField(ref _shadowFaceRelevanceMask, clampedMask, nameof(ShadowFaceRelevanceMask));
            SetField(ref _lastShadowRelevanceFrame, frameId, nameof(LastShadowRelevanceFrame));
        }

        private int CurrentShadowFaceRelevanceMask
            => _shadowFaceRelevanceMask & LocalShadowFrustumRelevance.AllPointFacesMask;

        private XRViewport CreateShadowViewport(uint resolution)
            => new(null, resolution, resolution)
            {
                RenderPipeline = new ShadowRenderPipeline(),
                SetRenderPipelineFromCamera = false,
                AutomaticallyCollectVisible = false,
                AutomaticallySwapBuffers = false,
                AllowUIRender = false,
            };

        private void EnsureShadowResources()
        {
            if (_viewports.Length == ShadowFaceCount && _shadowCameras.Length == ShadowFaceCount)
                return;

            uint resolution = ShadowMapResolutionWidth > ShadowMapResolutionHeight
                ? ShadowMapResolutionWidth
                : ShadowMapResolutionHeight;
            if (resolution == 0)
                resolution = 1024u;

            _viewports = new XRViewport[ShadowFaceCount].Fill(_ => CreateShadowViewport(resolution));
            float farPlane = MathF.Max(_influenceVolume.Radius, _shadowNearPlaneDistance + 0.001f);
            _shadowCameras = XRCubeFrameBuffer.GetCamerasPerFace(_shadowNearPlaneDistance, farPlane, true, _shadowCameraParentTransform);

            if (SceneNode is not null && !SceneNode.IsTransformNull)
                _shadowCameraParentTransform.Parent = Transform;

            for (int i = 0; i < _shadowCameras.Length; i++)
            {
                XRCamera cam = _shadowCameras[i];
                cam.CullingMask = DefaultLayers.EverythingExceptGizmos;
                if (SceneNode is not null && !SceneNode.IsTransformNull)
                    cam.Transform.Parent = _shadowCameraParentTransform;

                _viewports[i].Camera = cam;

                var colorStage = cam.GetPostProcessStageState<ColorGradingSettings>();
                if (colorStage?.TryGetBacking(out ColorGradingSettings? grading) == true && grading is not null)
                {
                    grading.AutoExposure = false;
                    grading.Exposure = 1.0f;
                }
                else
                {
                    colorStage?.SetValue(nameof(ColorGradingSettings.AutoExposure), false);
                    colorStage?.SetValue(nameof(ColorGradingSettings.Exposure), 1.0f);
                }

                _viewports[i].WorldInstanceOverride = IsActiveInHierarchy
                    ? WorldAs<XREngine.Rendering.IRuntimeRenderWorld>()
                    : null;
            }
        }

        private void SyncShadowCaptureTransforms()
        {
            Vector3 lightPosition = Transform.RenderTranslation;
            if (_influenceVolume.Center != lightPosition)
                SetField(ref _influenceVolume, new Sphere(lightPosition, _influenceVolume.Radius));

            if (SceneNode is not null && !SceneNode.IsTransformNull && _shadowCameraParentTransform.Parent != Transform)
                _shadowCameraParentTransform.Parent = Transform;

            bool shadowCamerasSynced = _shadowCameras.Length == 0
                || _shadowCameras[0].Transform.RenderTranslation == lightPosition;
            if (_shadowCameraParentTransform.RenderTranslation == lightPosition && shadowCamerasSynced)
                return;

            _shadowCameraParentTransform
                .SetRenderMatrix(Matrix4x4.CreateTranslation(lightPosition), recalcAllChildRenderMatrices: true)
                .Wait();
        }

        public override void CollectVisibleItems()
        {
            if (!ShouldProcessShadowViewports())
                return;

            EnsureShadowResources();
            SyncShadowCaptureTransforms();

            int faceMask = CurrentShadowFaceRelevanceMask;
            if (faceMask == 0)
                return;

            PointShadowRenderPlan plan = CreatePointShadowRenderPlan();
            PublishShadowRenderPlan(plan);
            bool prepareAtlasGroupedCommands = ShouldPrepareAtlasGroupedFaceCollection();
            if (plan.IsLayered)
            {
                // Layered and grouped-atlas passes render all selected faces from one draw list.
                _viewports[0].CollectVisible(
                    collectMirrors: false,
                    collectionVolumeOverride: _influenceVolume);
            }
            else if (prepareAtlasGroupedCommands)
            {
                for (int i = 0; i < ShadowFaceCount; i++)
                    if ((faceMask & (1 << i)) != 0)
                        _viewports[i].CollectVisible(collectMirrors: false);
            }
            else
            {
                LogShadowRenderModeFallbackIfNeeded(plan);
                for (int i = 0; i < ShadowFaceCount; i++)
                    if ((faceMask & (1 << i)) != 0)
                        _viewports[i].CollectVisible(collectMirrors: false);
            }
        }

        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!ShouldProcessShadowViewports())
                return;

            EnsureShadowResources();

            int faceMask = CurrentShadowFaceRelevanceMask;
            if (faceMask == 0)
                return;

            PointShadowRenderPlan plan = CreatePointShadowRenderPlan();
            PublishShadowRenderPlan(plan);
            bool prepareAtlasGroupedCommands = ShouldPrepareAtlasGroupedFaceCollection();
            if (plan.IsLayered && !prepareAtlasGroupedCommands)
            {
                _viewports[0].SwapBuffers();
            }
            else
            {
                LogShadowRenderModeFallbackIfNeeded(plan);
                for (int i = 0; i < ShadowFaceCount; i++)
                    if ((faceMask & (1 << i)) != 0)
                        _viewports[i].SwapBuffers();
            }
            lightmapBaker?.ProcessDynamicCachedAutoBake(this);
        }

        public override void RenderShadowMap(bool collectVisibleNow = false)
        {
            if (!CastsShadows || UsesPointShadowAtlasForCurrentEncoding)
                return;

            EnsureShadowMapForActiveDynamicLight();
            if (ShadowMap is null)
                return;

            EnsureShadowResources();
            SyncShadowCaptureTransforms();

            int faceMask = CurrentShadowFaceRelevanceMask;
            if (faceMask == 0)
                return;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            PointShadowRenderPlan plan = CreatePointShadowRenderPlan();
            PublishShadowRenderPlan(plan);
            ApplyShadowMapClearColor();
            if (plan.IsLayered)
            {
                Span<Matrix4x4> faceMatrices = stackalloc Matrix4x4[ShadowFaceCount];
                Span<int> faceIndices = stackalloc int[ShadowFaceCount];
                int faceCount = CopyShadowFaceMatrices(faceMatrices, faceIndices, faceMask);
                if (faceCount == 0)
                    return;

                XRMaterial layeredMaterial = plan.IsInstancedLayered
                    ? PointInstancedShadowMaterial
                    : PointGeometryShadowMaterial;
                using var pointShadowPass = _viewports[0].RenderPipelineInstance.RenderState
                    .PushPointLightLayeredShadowPass(plan.IsInstancedLayered, faceMatrices[..faceCount], faceIndices[..faceCount]);
                _viewports[0].Render(ShadowMap, null, null, true, layeredMaterial);
                SetField(ref _lastRenderedShadowFaceMask, faceMask, nameof(LastRenderedShadowFaceMask));
                SetField(ref _lastShadowRenderFrame, RuntimeEngine.Rendering.State.RenderFrameId, nameof(LastShadowRenderFrame));
                GenerateMomentShadowMipmapsIfNeeded();
                return;
            }

            LogShadowRenderModeFallbackIfNeeded(plan);
            RenderSequentialShadowFaces(faceMask);
            GenerateMomentShadowMipmapsIfNeeded();
        }

        private PointShadowRenderPlan CreatePointShadowRenderPlan()
        {
            EPointShadowRenderMode requestedMode = _shadowRenderMode;
            if (UsesPointShadowAtlasForCurrentEncoding)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.AtlasUsesSequentialTiles);

            if (ShadowMap is null)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.MissingShadowMap);

            if (requestedMode == EPointShadowRenderMode.Sequential)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.SequentialRequested);

            if (!RuntimeEngine.Rendering.State.SupportsOpenGLLayeredFramebuffers)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.UnsupportedLayeredFramebuffer);

            return requestedMode switch
            {
                EPointShadowRenderMode.InstancedLayered => CreateInstancedShadowRenderPlan(requestedMode),
                EPointShadowRenderMode.GeometryShader => CreateGeometryShadowRenderPlan(requestedMode),
                _ => CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.SequentialRequested),
            };
        }

        private PointShadowRenderPlan CreatePointAtlasShadowRenderPlan(int faceCount, bool hasGroupedAtlasAllocation)
        {
            EPointShadowRenderMode requestedMode = _shadowRenderMode;
            if (faceCount <= 0)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.MissingGroupedAtlasAllocation);

            if (requestedMode == EPointShadowRenderMode.Sequential)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.SequentialRequested);

            if (!hasGroupedAtlasAllocation)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.MissingGroupedAtlasAllocation);

            if (!RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray ||
                faceCount > RuntimeEngine.Rendering.State.MaxOpenGLViewports)
            {
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.UnsupportedViewportScissorArray);
            }

            return requestedMode switch
            {
                EPointShadowRenderMode.InstancedLayered => CreateAtlasInstancedShadowRenderPlan(requestedMode),
                EPointShadowRenderMode.GeometryShader => CreateAtlasGeometryShadowRenderPlan(requestedMode),
                _ => CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.SequentialRequested),
            };
        }

        private static PointShadowRenderPlan CreateAtlasInstancedShadowRenderPlan(EPointShadowRenderMode requestedMode)
        {
            if (!RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.UnsupportedVertexStageViewportIndexWrites);

            return new PointShadowRenderPlan
            {
                RequestedMode = requestedMode,
                SelectedMode = EPointShadowRenderMode.InstancedLayered,
                FallbackReason = PointShadowRenderFallbackReason.None,
            };
        }

        private static PointShadowRenderPlan CreateAtlasGeometryShadowRenderPlan(EPointShadowRenderMode requestedMode)
        {
            if (!RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.UnsupportedGeometryStageViewportIndexWrites);

            return new PointShadowRenderPlan
            {
                RequestedMode = requestedMode,
                SelectedMode = EPointShadowRenderMode.GeometryShader,
                FallbackReason = PointShadowRenderFallbackReason.None,
            };
        }

        private static PointShadowRenderPlan CreateInstancedShadowRenderPlan(EPointShadowRenderMode requestedMode)
        {
            if (!RuntimeEngine.Rendering.State.SupportsOpenGLViewportArray)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.UnsupportedViewportArray);

            if (!RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.UnsupportedVertexStageLayerWrites);

            return new PointShadowRenderPlan
            {
                RequestedMode = requestedMode,
                SelectedMode = EPointShadowRenderMode.InstancedLayered,
                FallbackReason = PointShadowRenderFallbackReason.None,
            };
        }

        private static PointShadowRenderPlan CreateGeometryShadowRenderPlan(EPointShadowRenderMode requestedMode)
        {
            if (!RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderLayeredRendering)
                return CreateSequentialShadowRenderPlan(requestedMode, PointShadowRenderFallbackReason.UnsupportedGeometryShader);

            return new PointShadowRenderPlan
            {
                RequestedMode = requestedMode,
                SelectedMode = EPointShadowRenderMode.GeometryShader,
                FallbackReason = PointShadowRenderFallbackReason.None,
            };
        }

        private static PointShadowRenderPlan CreateSequentialShadowRenderPlan(
            EPointShadowRenderMode requestedMode,
            PointShadowRenderFallbackReason fallbackReason)
            => new()
            {
                RequestedMode = requestedMode,
                SelectedMode = EPointShadowRenderMode.Sequential,
                FallbackReason = fallbackReason,
            };

        private void PublishShadowRenderPlan(in PointShadowRenderPlan plan)
        {
            if (_effectiveShadowRenderMode != plan.SelectedMode)
                _effectiveShadowRenderMode = plan.SelectedMode;
            if (_shadowRenderFallbackReason != plan.FallbackReason)
                _shadowRenderFallbackReason = plan.FallbackReason;
        }

        private int CopyShadowFaceMatrices(Span<Matrix4x4> matrices)
        {
            int count = Math.Min(ShadowFaceCount, Math.Min(_shadowCameras.Length, matrices.Length));
            for (int i = 0; i < count; ++i)
            {
                XRCamera cam = _shadowCameras[i];
                Matrix4x4.Invert(cam.Transform.RenderMatrix, out Matrix4x4 viewMatrix);
                matrices[i] = viewMatrix * cam.ProjectionMatrix;
            }

            return count;
        }

        private int CopyShadowFaceMatrices(Span<Matrix4x4> matrices, Span<int> faceIndices, int faceMask)
        {
            int count = 0;
            int cameraCount = Math.Min(ShadowFaceCount, _shadowCameras.Length);
            for (int faceIndex = 0; faceIndex < cameraCount && count < matrices.Length && count < faceIndices.Length; ++faceIndex)
            {
                if ((faceMask & (1 << faceIndex)) == 0)
                    continue;

                XRCamera cam = _shadowCameras[faceIndex];
                Matrix4x4.Invert(cam.Transform.RenderMatrix, out Matrix4x4 viewMatrix);
                matrices[count] = viewMatrix * cam.ProjectionMatrix;
                faceIndices[count] = faceIndex;
                count++;
            }

            return count;
        }

        private void RenderSequentialShadowFaces(int faceMask)
        {
            _perFaceFbo ??= new XRFrameBuffer();
            var mat = ShadowMap!.Material!;
            var depthCube = (IFrameBufferAttachement)mat.Textures[0]!;
            var shadowCube = (IFrameBufferAttachement)mat.Textures[1]!;
            int renderedMask = 0;
            for (int i = 0; i < ShadowFaceCount; i++)
            {
                if ((faceMask & (1 << i)) == 0)
                    continue;

                _perFaceFbo.SetRenderTargets(
                    (depthCube, EFrameBufferAttachment.DepthAttachment, 0, i),
                    (shadowCube, EFrameBufferAttachment.ColorAttachment0, 0, i));
                _viewports[i].Render(_perFaceFbo, null, null, true, mat);
                renderedMask |= 1 << i;
            }

            SetField(ref _lastRenderedShadowFaceMask, renderedMask, nameof(LastRenderedShadowFaceMask));
            if (renderedMask != 0)
                SetField(ref _lastShadowRenderFrame, RuntimeEngine.Rendering.State.RenderFrameId, nameof(LastShadowRenderFrame));
        }

        private void LogShadowRenderModeFallbackIfNeeded(in PointShadowRenderPlan plan)
        {
            if (plan.FallbackReason is PointShadowRenderFallbackReason.None
                or PointShadowRenderFallbackReason.SequentialRequested
                or PointShadowRenderFallbackReason.AtlasUsesSequentialTiles)
            {
                return;
            }

            Debug.LightingWarningEvery(
                $"PointShadowRenderModeFallback.{GetHashCode()}",
                TimeSpan.FromSeconds(2.0),
                "[PointShadowAudit] Point shadow render mode fallback for '{0}': requested={1}, effective={2}, reason={3}.",
                SceneNode?.Name ?? Name ?? GetType().Name,
                plan.RequestedMode,
                plan.SelectedMode,
                plan.FallbackReason);
        }

        private bool ShouldProcessShadowViewports()
            => CastsShadows && (ShadowMap is not null || UsesPointShadowAtlasForCurrentEncoding);

        private bool ShouldPrepareAtlasGroupedFaceCollection()
        {
            if (!UsesPointShadowAtlasForCurrentEncoding ||
                _shadowRenderMode == EPointShadowRenderMode.Sequential ||
                !RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray ||
                ShadowFaceCount > RuntimeEngine.Rendering.State.MaxOpenGLViewports)
            {
                return false;
            }

            return _shadowRenderMode switch
            {
                EPointShadowRenderMode.InstancedLayered => RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex,
                EPointShadowRenderMode.GeometryShader => RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex,
                _ => false,
            };
        }

        /// <summary>
        /// The distance beyond which this light has no visible effect.
        /// </summary>
        [Category("Attenuation")]
        [DisplayName("Radius")]
        [Description("Distance beyond which the light has no effect.")]
        public float Radius
        {
            get => _influenceVolume.Radius;
            set
            {
                SetField(ref _influenceVolume, new Sphere(_influenceVolume.Center, value));

                float clampedNear = ClampShadowNearPlaneDistance(_shadowNearPlaneDistance, value);
                if (clampedNear != _shadowNearPlaneDistance)
                    SetField(ref _shadowNearPlaneDistance, clampedNear, nameof(ShadowNearPlaneDistance));

                foreach (XRCamera cam in _shadowCameras)
                {
                    cam.NearZ = _shadowNearPlaneDistance;
                    cam.FarZ = MathF.Max(value, _shadowNearPlaneDistance + 0.001f);
                }

                if (SceneNode is not null && !SceneNode.IsTransformNull)
                    MeshCenterAdjustMatrix = Matrix4x4.CreateScale(value);
            }
        }

        private static float ClampShadowNearPlaneDistance(float value, float radius)
        {
            if (!float.IsFinite(value))
                value = PointShadowNearPlaneDistanceDefault;

            if (radius <= 0.001f)
                return MathF.Max(0.0001f, value);

            float maxNear = MathF.Max(0.0001f, radius - 0.001f);
            return Math.Clamp(value, 0.0001f, maxNear);
        }

        public override void SetShadowMapResolution(uint width, uint height)
        {
            uint max = Math.Max(width, height);

            // Cubemap textures use immutable storage (Resizable=false) and cannot
            // be resized in place. Destroy the old FBO so the base recreates it
            // with fresh textures of the new size.
            ShadowMap?.Destroy();
            ShadowMap = null;

            base.SetShadowMapResolution(max, max);

            foreach (XRViewport vp in _viewports)
                vp.Resize(max, max);
        }

        private float _brightness = 1.0f;
        /// <summary>
        /// Intensity multiplier for this light.
        /// </summary>
        [Category("Attenuation")]
        [DisplayName("Brightness")]
        [Description("Intensity multiplier applied to the light output.")]
        public float Brightness
        {
            get => _brightness;
            set => SetField(ref _brightness, value);
        }

        public static XRMesh GetVolumeMesh()
            => XRMesh.Shapes.SolidSphere(Vector3.Zero, 1.0f, 32);
        protected override XRMesh GetWireframeMesh()
            => XRMesh.Shapes.WireframeSphere(Vector3.Zero, Radius, 32);

        private Sphere _influenceVolume;

        public override bool SupportsLightRadiusContactHardening => true;

        protected override float ContactHardeningLightRadius => Radius;

        /// <summary>
        /// Creates a point light with the default radius and brightness.
        /// </summary>
        public PointLightComponent()
            : this(100.0f, 1.0f) { }

        /// <summary>
        /// Creates a point light with the requested attenuation radius and brightness.
        /// </summary>
        public PointLightComponent(float radius, float brightness)
            : base()
        {
            // Cooked reflection deserialization constructs the component before it is
            // attached to an owning SceneNode, so Transform is not available here.
            _influenceVolume = new Sphere(Vector3.Zero, radius);
            ShadowDepthBiasTexels = 1.0f;
            ShadowSlopeBiasTexels = 2.0f;
            ShadowNormalBiasTexels = 1.0f;
            Brightness = brightness;
        }

        protected override void OnTransformChanged()
        {
            if (_shadowCameras.Length > 0)
            {
                _shadowCameraParentTransform.Parent = Transform;
                foreach (XRCamera cam in _shadowCameras)
                    cam.Transform.Parent = _shadowCameraParentTransform;
            }

            MeshCenterAdjustMatrix = Matrix4x4.CreateScale(_influenceVolume.Radius);
            base.OnTransformChanged();
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            SetField(ref _influenceVolume, new Sphere(renderMatrix.Translation, _influenceVolume.Radius));
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            EnsureShadowResources();

            for (int i = 0; i < _viewports.Length; i++)
                _viewports[i].WorldInstanceOverride = WorldAs<XREngine.Rendering.IRuntimeRenderWorld>();
        }

        private XRMaterial ShadowAtlasMaterial => _shadowAtlasMaterial ??= CreateShadowAtlasMaterial();

        private XRMaterial PointGeometryShadowMaterial
            => _pointGeometryShadowMaterial ??= CreatePointGeometryShadowMaterial();

        private XRMaterial PointInstancedShadowMaterial
            => _pointInstancedShadowMaterial ??= CreatePointInstancedShadowMaterial();

        private XRMaterial PointAtlasGeometryShadowMaterial
            => _pointAtlasGeometryShadowMaterial ??= CreatePointAtlasGeometryShadowMaterial();

        private XRMaterial PointAtlasInstancedShadowMaterial
            => _pointAtlasInstancedShadowMaterial ??= CreatePointAtlasInstancedShadowMaterial();

        private XRMaterial CreateShadowAtlasMaterial()
        {
            XRMaterial mat = new(XRShader.EngineShader("PointLightShadowDepth.fs", EShaderType.Fragment));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        private XRMaterial CreatePointGeometryShadowMaterial()
        {
            XRMaterial mat = new(
                XRShader.EngineShader("PointLightShadowDepth.gs", EShaderType.Geometry),
                XRShader.EngineShader("PointLightShadowDepth.fs", EShaderType.Fragment));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.PointShadowMaterialKind = EPointShadowMaterialKind.GeometryShader;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        private XRMaterial CreatePointInstancedShadowMaterial()
        {
            XRMaterial mat = new(XRShader.EngineShader("PointLightShadowDepth.fs", EShaderType.Fragment));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.PointShadowMaterialKind = EPointShadowMaterialKind.InstancedLayered;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        private XRMaterial CreatePointAtlasGeometryShadowMaterial()
        {
            XRMaterial mat = new(
                XRShader.EngineShader("PointLightAtlasShadowDepth.gs", EShaderType.Geometry),
                XRShader.EngineShader("PointLightShadowDepth.fs", EShaderType.Fragment));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.PointShadowMaterialKind = EPointShadowMaterialKind.AtlasGeometryShader;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        private XRMaterial CreatePointAtlasInstancedShadowMaterial()
        {
            XRMaterial mat = new(XRShader.EngineShader("PointLightShadowDepth.fs", EShaderType.Fragment));
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            mat.PointShadowMaterialKind = EPointShadowMaterialKind.AtlasInstancedLayered;
            mat.SettingShadowUniforms += SetShadowMapUniforms;
            return mat;
        }

        internal bool TryGetShadowFaceCamera(int faceIndex, out XRCamera camera)
        {
            if ((uint)faceIndex >= ShadowFaceCount)
            {
                camera = null!;
                return false;
            }

            EnsureShadowResources();
            SyncShadowCaptureTransforms();
            camera = _shadowCameras[faceIndex];
            return true;
        }

        internal bool RenderShadowAtlasFaceTile(int faceIndex, XRFrameBuffer atlasFbo, BoundingRectangle renderRect, bool collectVisibleNow)
        {
            if (!CastsShadows ||
                World is null ||
                (uint)faceIndex >= ShadowFaceCount ||
                renderRect.Width <= 0 ||
                renderRect.Height <= 0)
                return false;

            EnsureShadowResources();
            SyncShadowCaptureTransforms();

            XRViewport viewport = _viewports[faceIndex];
            if (viewport.RenderPipeline is not ShadowRenderPipeline shadowPipeline)
                return false;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            bool previousPreserveArea = shadowPipeline.PreserveExistingRenderArea;
            shadowPipeline.PreserveExistingRenderArea = true;
            shadowPipeline.ClearColor = GetShadowMapClearColor();
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

        internal bool RenderGroupedShadowAtlasFaceTiles(
            in ShadowAtlasGroupedPointFaceAllocation group,
            XRFrameBuffer atlasFbo,
            bool collectVisibleNow)
        {
            if (!CastsShadows ||
                World is null ||
                group.FaceCount <= 1 ||
                group.Members is null ||
                group.Members.Length < group.FaceCount ||
                atlasFbo.Width <= 0 ||
                atlasFbo.Height <= 0)
            {
                return false;
            }

            EnsureShadowResources();
            SyncShadowCaptureTransforms();

            int groupedCount = Math.Min(group.FaceCount, ShadowFaceCount);
            PointShadowRenderPlan plan = CreatePointAtlasShadowRenderPlan(groupedCount, hasGroupedAtlasAllocation: true);
            PublishShadowRenderPlan(plan);
            if (!plan.IsLayered)
            {
                LogShadowRenderModeFallbackIfNeeded(plan);
                return false;
            }

            XRViewport viewport = _viewports[0];
            if (viewport.RenderPipeline is not ShadowRenderPipeline shadowPipeline)
                return false;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            Span<Matrix4x4> publishedMatrices = stackalloc Matrix4x4[ShadowFaceCount];
            int publishedMatrixCount = CopyShadowFaceMatrices(publishedMatrices);
            Span<Matrix4x4> groupedMatrices = stackalloc Matrix4x4[ShadowFaceCount];
            Span<int> faceIndices = stackalloc int[ShadowFaceCount];
            Span<BoundingRectangle> indexedRects = stackalloc BoundingRectangle[ShadowFaceCount];

            for (int i = 0; i < groupedCount; i++)
            {
                ShadowAtlasGroupedAllocationMember member = group.Members[i];
                int faceIndex = member.CascadeIndex;
                if ((uint)member.ViewportScissorIndex >= (uint)groupedCount ||
                    (uint)faceIndex >= (uint)publishedMatrixCount ||
                    member.InnerPixelRect.Width <= 0 ||
                    member.InnerPixelRect.Height <= 0)
                {
                    return false;
                }

                groupedMatrices[member.ViewportScissorIndex] = publishedMatrices[faceIndex];
                faceIndices[member.ViewportScissorIndex] = faceIndex;
                indexedRects[member.ViewportScissorIndex] = member.InnerPixelRect;
                _groupedAtlasClearRects[member.ViewportScissorIndex] = member.InnerPixelRect;
            }

            bool previousPreserveArea = shadowPipeline.PreserveExistingRenderArea;
            BoundingRectangle[]? previousIndexedClearRegions = shadowPipeline.IndexedClearRegions;
            int previousIndexedClearRegionCount = shadowPipeline.IndexedClearRegionCount;
            shadowPipeline.PreserveExistingRenderArea = true;
            shadowPipeline.IndexedClearRegions = _groupedAtlasClearRects;
            shadowPipeline.IndexedClearRegionCount = groupedCount;
            shadowPipeline.ClearColor = GetShadowMapClearColor();
            try
            {
                int pageWidth = checked((int)atlasFbo.Width);
                int pageHeight = checked((int)atlasFbo.Height);
                BoundingRectangle pageRect = new(0, 0, pageWidth, pageHeight);
                var state = viewport.RenderPipelineInstance.RenderState;
                using var renderArea = state.PushRenderArea(pageRect);
                using var cropArea = state.PushCropArea(pageRect);
                using var indexedState = state.PushIndexedViewportScissors(indexedRects[..groupedCount], indexedRects[..groupedCount]);
                using var pointShadowPass = state.PushPointLightLayeredShadowPass(
                    plan.IsInstancedLayered,
                    groupedMatrices[..groupedCount],
                    faceIndices[..groupedCount],
                    atlasGrouped: true);

                XRMaterial groupedMaterial = plan.IsInstancedLayered
                    ? PointAtlasInstancedShadowMaterial
                    : PointAtlasGeometryShadowMaterial;
                viewport.Render(atlasFbo, null, null, true, groupedMaterial);
            }
            finally
            {
                shadowPipeline.PreserveExistingRenderArea = previousPreserveArea;
                shadowPipeline.IndexedClearRegions = previousIndexedClearRegions;
                shadowPipeline.IndexedClearRegionCount = previousIndexedClearRegionCount;
            }

            return true;
        }

        internal void ClearShadowAtlasFaceSlots()
            => Array.Clear(_atlasFaceSlots);

        internal void SetShadowAtlasFaceSlot(
            int faceIndex,
            ShadowAtlasAllocation allocation,
            int recordIndex,
            float nearPlane,
            float farPlane,
            uint desiredResolution)
        {
            if ((uint)faceIndex >= ShadowFaceCount)
                return;

            uint sampleResolution = LightComponent.GetShadowAtlasSampleResolution(allocation);
            float texelSize = sampleResolution > 0u ? 1.0f / sampleResolution : 0.0f;
            float resolutionScale = sampleResolution > 0u
                ? MathF.Max(1.0f, Math.Max(1u, desiredResolution) / (float)sampleResolution)
                : 1.0f;

            _atlasFaceSlots[faceIndex] = new PointShadowAtlasFaceSlot(
                HasAllocation: true,
                IsResident: allocation.IsResident,
                PageIndex: allocation.PageIndex,
                RecordIndex: recordIndex,
                UvScaleBias: allocation.UvScaleBias,
                NearPlane: nearPlane,
                FarPlane: farPlane,
                TexelSize: texelSize,
                ResolutionScale: resolutionScale,
                Resolution: allocation.Resolution,
                Fallback: allocation.ActiveFallback,
                PixelRect: allocation.PixelRect,
                InnerPixelRect: allocation.InnerPixelRect,
                LastRenderedFrame: allocation.LastRenderedFrame);
        }

        internal bool TryGetShadowAtlasFaceSlot(int faceIndex, out PointShadowAtlasFaceSlot slot)
        {
            if ((uint)faceIndex < ShadowFaceCount)
            {
                slot = _atlasFaceSlots[faceIndex];
                return slot.HasAllocation;
            }

            slot = default;
            return false;
        }
        protected override void OnComponentDeactivated()
        {
            for (int i = 0; i < _viewports.Length; i++)
                _viewports[i].WorldInstanceOverride = null;

            base.OnComponentDeactivated();
        }

        protected override void RegisterDynamicLight(IRuntimeRenderWorld world)
            => world.Lights.DynamicPointLights.Add(this);

        protected override void UnregisterDynamicLight(IRuntimeRenderWorld world)
            => world.Lights.DynamicPointLights.Remove(this);

        /// <summary>
        /// This is to set uniforms in the GBuffer lighting shader 
        /// or in a forward shader that requests lighting uniforms.
        /// </summary>
        public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            base.SetUniforms(program, targetStructName);

            string prefix = targetStructName ?? RuntimeEngine.Rendering.Constants.LightsStructName;
            string flatPrefix = $"{prefix}.";
            string basePrefix = $"{prefix}.Base.";
            Vector3 lightPosition = Transform.RenderTranslation;

            // Legacy flat uniforms.
            program.Uniform($"{flatPrefix}Color", _color);
            program.Uniform($"{flatPrefix}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{flatPrefix}Position", lightPosition);
            program.Uniform($"{flatPrefix}Radius", _influenceVolume.Radius);
            program.Uniform($"{flatPrefix}Brightness", _brightness);
            program.Uniform("ShadowNearPlaneDist", ShadowNearPlaneDistance);

            // Structured Base.* uniforms for ForwardLighting snippet compatibility.
            program.Uniform($"{basePrefix}Color", _color);
            program.Uniform($"{basePrefix}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{basePrefix}AmbientIntensity", 0.0f);
            program.Uniform($"{basePrefix}WorldToLightSpaceProjMatrix", Matrix4x4.Identity);
            program.Uniform($"{prefix}.Position", lightPosition);
            program.Uniform($"{prefix}.Radius", _influenceVolume.Radius);
            program.Uniform($"{prefix}.Brightness", _brightness);
            // Note: Shadow map sampler and LightHasShadowMap are bound by the caller (deferred pass)
            // to avoid overwriting material texture units.
        }

        /// <summary>
        /// This is to set special uniforms each time any mesh is rendered 
        /// with the shadow depth shader during the shadow pass.
        /// </summary>
        protected override void SetShadowMapUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            ShadowMapFormatSelection selection = ResolveShadowMapFormat(preferredStorageFormat: ShadowMapStorageFormat);

            // The shadow pass binds whatever program the mesh's material owns. Most material
            // programs do not declare these names (or declare them with different types), so
            // pushing them unconditionally produced a GL_INVALID_OPERATION storm. Gate every
            // upload on whether the program actually has a matching uniform.
            if (program.HasUniform("FarPlaneDist"))
                program.Uniform("FarPlaneDist", _influenceVolume.Radius);
            if (program.HasUniform("LightPos"))
                program.Uniform("LightPos", Transform.RenderTranslation);
            if (program.HasUniform("ShadowMapEncoding"))
                program.Uniform("ShadowMapEncoding", (int)selection.Encoding);
            if (program.HasUniform("ShadowMomentMinVariance"))
                program.Uniform("ShadowMomentMinVariance", ShadowMomentMinVariance);
            if (program.HasUniform("ShadowMomentLightBleedReduction"))
                program.Uniform("ShadowMomentLightBleedReduction", ShadowMomentLightBleedReduction);
            if (program.HasUniform("ShadowMomentPositiveExponent"))
                program.Uniform("ShadowMomentPositiveExponent", selection.PositiveExponent);
            if (program.HasUniform("ShadowMomentNegativeExponent"))
                program.Uniform("ShadowMomentNegativeExponent", selection.NegativeExponent);
            if (program.HasUniform("ShadowMomentMipBias"))
                program.Uniform("ShadowMomentMipBias", ShadowMomentMipBias);
            var state = RuntimeEngine.Rendering.State.RenderingPipelineState;
            if (state?.PointLightLayeredShadowPass == true && state.PointLightShadowFaceCount > 0)
            {
                int layeredFaceCount = Math.Min(ShadowFaceCount, state.PointLightShadowFaceCount);
                if (program.HasUniform("PointShadowFaceCount"))
                    program.Uniform("PointShadowFaceCount", layeredFaceCount);
                int faceMask = 0;
                for (int i = 0; i < layeredFaceCount; ++i)
                {
                    if (state.TryGetPointLightShadowFaceMatrix(i, out Matrix4x4 vp))
                    {
                        if (program.HasUniform(ViewProjectionMatrixUniformNames[i]))
                            program.Uniform(ViewProjectionMatrixUniformNames[i], vp);
                        if (program.HasUniform(PointShadowViewProjectionMatrixUniformNames[i]))
                            program.Uniform(PointShadowViewProjectionMatrixUniformNames[i], vp);
                    }

                    if (state.TryGetPointLightShadowFaceIndex(i, out int faceIndex))
                    {
                        if (program.HasUniform(PointShadowFaceIndexUniformNames[i]))
                            program.Uniform(PointShadowFaceIndexUniformNames[i], faceIndex);
                        if ((uint)faceIndex < ShadowFaceCount)
                            faceMask |= 1 << faceIndex;
                    }
                }

                if (program.HasUniform("PointShadowFaceMask"))
                    program.Uniform("PointShadowFaceMask", faceMask);
                return;
            }

            int faceCount = Math.Min(ShadowFaceCount, _shadowCameras.Length);
            if (program.HasUniform("PointShadowFaceCount"))
                program.Uniform("PointShadowFaceCount", faceCount);
            if (program.HasUniform("PointShadowFaceMask"))
                program.Uniform("PointShadowFaceMask", CurrentShadowFaceRelevanceMask);
            for (int i = 0; i < faceCount; ++i)
            {
                XRCamera cam = _shadowCameras[i];
                Matrix4x4.Invert(cam.Transform.RenderMatrix, out Matrix4x4 viewMatrix);
                Matrix4x4 vp = viewMatrix * cam.ProjectionMatrix;
                if (program.HasUniform(ViewProjectionMatrixUniformNames[i]))
                    program.Uniform(ViewProjectionMatrixUniformNames[i], vp);
                if (program.HasUniform(PointShadowViewProjectionMatrixUniformNames[i]))
                    program.Uniform(PointShadowViewProjectionMatrixUniformNames[i], vp);
                if (program.HasUniform(PointShadowFaceIndexUniformNames[i]))
                    program.Uniform(PointShadowFaceIndexUniformNames[i], i);
            }
        }

        public override XRMaterial GetShadowMapMaterial(uint width, uint height, EDepthPrecision precision = EDepthPrecision.Int24)
        {
            uint cubeExtent = Math.Max(width, height);
            ShadowMapFormatSelection selection = ResolveShadowMapFormat(preferredStorageFormat: ShadowMapStorageFormat);
            ShadowMapTextureFormat shadowFormat = GetShadowMapTextureFormat(selection.Format.StorageFormat);
            bool momentEncoding = selection.Encoding != EShadowMapEncoding.Depth;
            ETexMinFilter minFilter = selection.Format.RequiresLinearFiltering
                ? (ShadowMomentUseMipmaps ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear)
                : ETexMinFilter.Nearest;
            ETexMagFilter magFilter = selection.Format.RequiresLinearFiltering ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
            XRTexture[] refs =
            [
                new XRTextureCube(cubeExtent, GetShadowDepthMapFormat(precision), EPixelFormat.DepthComponent, EPixelType.UnsignedInt, false)
                {
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
                    SmallestAllowedMipmapLevel = 0,
                    FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                    Resizable = false,
                },
                new XRTextureCube(cubeExtent, shadowFormat.InternalFormat, shadowFormat.PixelFormat, shadowFormat.PixelType, false)
                {
                    MinFilter = minFilter,
                    MagFilter = magFilter,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
                    SmallestAllowedMipmapLevel = 0,
                    FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
                    SamplerName = "ShadowMap",
                    Resizable = false,
                    AutoGenerateMipmaps = momentEncoding && ShadowMomentUseMipmaps,
                },
            ];

            XRShader fragShader = XRShader.EngineShader("PointLightShadowDepth.fs", EShaderType.Fragment);
            XRMaterial mat = new(refs, fragShader);

            // No culling so a light inside geometry still shadows everything around it.
            mat.RenderOptions.CullMode = ECullMode.None;
            mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;

            return mat;
        }

        private void ApplyShadowMapClearColor()
        {
            ColorF4 clearColor = GetShadowMapClearColor();
            for (int i = 0; i < _viewports.Length; i++)
                if (_viewports[i].RenderPipeline is ShadowRenderPipeline shadowPipeline)
                    shadowPipeline.ClearColor = clearColor;
        }

        private void GenerateMomentShadowMipmapsIfNeeded()
        {
            if (!CastsShadows ||
                !ShadowMomentUseMipmaps ||
                ShadowMapEncoding == EShadowMapEncoding.Depth ||
                ShadowMap?.Material?.Textures is not { } textures)
            {
                return;
            }

            ShadowMapFormatSelection selection = ResolveShadowMapFormat(preferredStorageFormat: ShadowMapStorageFormat);
            if (selection.Encoding == EShadowMapEncoding.Depth)
                return;

            for (int i = 0; i < textures.Count; i++)
            {
                if (textures[i] is XRTextureCube texture && texture.SamplerName == "ShadowMap")
                {
                    texture.GenerateMipmapsGPU();
                    return;
                }
            }
        }

        private ColorF4 GetShadowMapClearColor()
        {
            ShadowMapFormatSelection selection = ResolveShadowMapFormat(preferredStorageFormat: ShadowMapStorageFormat);
            Vector4 clear = selection.ClearSentinel.Value;
            return new ColorF4(clear.X, clear.Y, clear.Z, clear.W);
        }

        internal override void BuildShadowFrusta(List<PreparedFrustum> output)
        {
            output.Clear();

            if (_shadowCameras.Length == 0)
                return;

            for (int i = 0; i < _shadowCameras.Length; i++)
            {
                XRCamera cam = _shadowCameras[i];
                output.Add(cam.WorldFrustum().Prepare());
            }
        }
    }
}
