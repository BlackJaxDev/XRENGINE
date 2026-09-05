using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Scene;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    public class RenderingState : IRuntimeRenderCommandExecutionState
    {
        private static readonly Action<object?> PopMainAttributesAction =
            static state => ((RenderingState)state!).PopMainAttributes();
        private static readonly Action<object?> PopDirectionalCascadeLayeredShadowPassAction =
            static state => ((RenderingState)state!).PopDirectionalCascadeLayeredShadowPass();
        private static readonly Action<object?> PopPointLightLayeredShadowPassAction =
            static state => ((RenderingState)state!).PopPointLightLayeredShadowPass();
        private static readonly Action<object?> PopRenderingCameraAction =
            static state => ((RenderingState)state!).PopRenderingCamera();
        private static readonly Action<object?> PopRenderAreaAction =
            static state => ((RenderingState)state!).PopRenderArea();
        private static readonly Action<object?> PopCropAreaAction =
            static state => ((RenderingState)state!).PopCropArea();
        private static readonly Action<object?> PopIndexedViewportScissorsAction =
            static state => ((RenderingState)state!).PopIndexedViewportScissors();
        private static readonly Action<object?> PopRenderTargetBindingAction =
            static state => ((RenderingState)state!).PopRenderTargetBinding();
        private static readonly Action<object?> PopOverrideMaterialAction =
            static state => ((RenderingState)state!).PopOverrideMaterial();
        private static readonly Action<object?> PopTextureBindingAction =
            static state => ((RenderingState)state!).PopTextureBinding();
        private static readonly Action<object?> PopBufferBindingAction =
            static state => ((RenderingState)state!).PopBufferBinding();
        private static readonly Action<object?> PopShaderGlobalsAction =
            static state => ((RenderingState)state!).PopShaderGlobals();
        private static readonly Action<object?> PopProgramBindingsAction =
            static state => ((RenderingState)state!).PopProgramBindings();
        private static readonly Action<object?> PopUseDepthNormalMaterialVariantsAction =
            static state => ((RenderingState)state!).PopUseDepthNormalMaterialVariants();
        private static readonly Action<object?> PopUseMotionVectorMaterialVariantAction =
            static state => ((RenderingState)state!).PopUseMotionVectorMaterialVariant();
        private static readonly Action<object?> PopUnjitteredProjectionAction =
            static state => ((RenderingState)state!).PopUnjitteredProjection();
        private static readonly Action<object?> PopForceShaderPipelinesAction =
            static state => ((RenderingState)state!).PopForceShaderPipelines();
        private static readonly Action<object?> PopForceGeneratedVertexProgramAction =
            static state => ((RenderingState)state!).PopForceGeneratedVertexProgram();
        private static readonly Action<object?> PopViewportAction =
            static state => ((RenderingState)state!).PopViewport();
        private static readonly Action<object?> PopRenderingSceneAction =
            static state => ((RenderingState)state!).PopRenderingScene();

        /// <summary>
        /// The viewport being rendered to.
        /// May be null if rendering directly to a framebuffer.
        /// </summary>
        public XRViewport? WindowViewport { get; private set; }
        /// <summary>
        /// The scene being rendered.
        /// </summary>
        public VisualScene? Scene { get; private set; }
        /// <summary>
        /// The camera this render pipeline is rendering the scene through.
        /// </summary>
        public XRCamera? SceneCamera { get; private set; }
        /// <summary>
        /// The right eye camera for stereo rendering.
        /// </summary>
        public XRCamera? StereoRightEyeCamera { get; private set; }
        /// <summary>
        /// The output FBO target for the render pass.
        /// May be null if rendering to the screen.
        /// </summary>
        public XRFrameBuffer? OutputFBO { get; private set; }
        /// <summary>
        /// If this pipeline is rendering a shadow pass.
        /// Shadow passes do not need to execute all rendering commands.
        /// </summary>
        public bool ShadowPass { get; private set; } = false;
        /// <summary>
        /// If this pipeline is rendering a stereo pass.
        /// Stereo passes will inject a geometry shader into each mesh pipeline, or expect the mesh to already have a vertex or geometry shader that supports it.
        /// </summary>
        public bool StereoPass { get; private set; } = false;
        /// <summary>Output-local desktop temporal-history sequence captured by this invocation.</summary>
        public ulong ViewHistorySequenceId { get; private set; }
        /// <summary>Pipeline and resource-generation identity paired with temporal history.</summary>
        public ulong ViewHistoryPipelineIdentity { get; private set; }
        public bool ViewHistoryAuthoring { get; private set; }
        internal bool ViewHistoryCaptureAccepted { get; private set; } = true;
        internal void SetViewHistoryCaptureAccepted(bool accepted) => ViewHistoryCaptureAccepted = accepted;
        /// <summary>
        /// Immutable logical views captured when this render invocation begins.
        /// </summary>
        public RenderFrameViewSet? FrameViewSet { get; private set; }
        /// <summary>
        /// Frame-owned publication of stable render-side scene buffers and logical views.
        /// </summary>
        public RenderWorldSnapshot? WorldSnapshot { get; private set; }
        /// <summary>
        /// If true, the current shadow pass targets a layered directional cascade framebuffer.
        /// </summary>
        public bool DirectionalCascadeLayeredShadowPass { get; private set; }
        /// <summary>
        /// If true, mesh draw instancing supplies the directional cascade layer index.
        /// </summary>
        public bool DirectionalCascadeInstancedLayeredShadowPass { get; private set; }
        /// <summary>
        /// If true, the current directional cascade pass writes atlas viewport indices instead of texture-array layers.
        /// </summary>
        public bool DirectionalCascadeAtlasGroupedShadowPass { get; private set; }
        /// <summary>
        /// Number of active directional cascade layers addressed by the current layered shadow pass.
        /// </summary>
        public int DirectionalCascadeShadowLayerCount { get; private set; }
        private readonly Matrix4x4[] _directionalCascadeShadowMatrices = new Matrix4x4[8];
        /// <summary>
        /// If true, the current shadow pass targets a layered point-light cubemap framebuffer.
        /// </summary>
        public bool PointLightLayeredShadowPass { get; private set; }
        /// <summary>
        /// If true, mesh draw instancing supplies the point cubemap face layer.
        /// </summary>
        public bool PointLightInstancedLayeredShadowPass { get; private set; }
        /// <summary>
        /// If true, the current point-light pass writes atlas viewport indices instead of cubemap layers.
        /// </summary>
        public bool PointLightAtlasGroupedShadowPass { get; private set; }
        /// <summary>
        /// Number of active cubemap face layers addressed by the current point-light layered shadow pass.
        /// </summary>
        public int PointLightShadowFaceCount { get; private set; }
        private readonly Matrix4x4[] _pointLightShadowFaceMatrices = new Matrix4x4[6];
        private readonly int[] _pointLightShadowFaceIndices = new int[6];
        /// <summary>
        /// If set, this material will be used to render all objects in the scene.
        /// Typically used for shadow passes.
        /// </summary>
        public XRMaterial? GlobalMaterialOverride { get; set; }
        /// <summary>
        /// The screen-space UI to render over the scene.
        /// </summary>
        public IRuntimeScreenSpaceUserInterface? ScreenSpaceUserInterface { get; private set; }
        /// <summary>
        /// All collected render commands for the current frame.
        /// </summary>
        public RenderCommandCollection? MeshRenderCommands { get; set; }

        /// <summary>
        /// Immutable capture policy snapshot used for this render invocation.
        /// </summary>
        public RenderCapturePolicy CapturePolicy { get; private set; }

        /// <summary>
        /// Most recently selected exact-visibility batch topology for this pipeline state.
        /// </summary>
        public ViewBatchSplitDecision? LastVisibilityBatchDecision { get; private set; }

        /// <summary>
        /// Point-of-view content policy associated with the latest visibility batch decision.
        /// </summary>
        public ViewBatchContentPolicy LastVisibilityContentPolicy { get; private set; } = ViewBatchContentPolicy.Exact;

        internal void PublishVisibilityBatchDiagnostics(
            in ViewBatchSplitDecision decision,
            in ViewBatchContentPolicy contentPolicy)
        {
            LastVisibilityBatchDecision = decision;
            LastVisibilityContentPolicy = contentPolicy;
        }

        IRuntimeViewportHost? IRuntimeRenderCommandExecutionState.WindowViewport
            => WindowViewport;

        IRuntimeRenderCommandSceneContext? IRuntimeRenderCommandExecutionState.RenderingScene
            => RenderingScene;

        IRuntimeRenderCamera? IRuntimeRenderCommandExecutionState.SceneCamera
            => SceneCamera;

        IRuntimeRenderCamera? IRuntimeRenderCommandExecutionState.RenderingCamera
            => RenderingCamera;

        IRuntimeRenderCamera? IRuntimeRenderCommandExecutionState.StereoRightEyeCamera
            => StereoRightEyeCamera;

        //TODO: instead of bools for shadow and stereo passes, use an int for the pass type.

        public StateObject PushMainAttributes(
            XRViewport? viewport,
            VisualScene? scene,
            XRCamera? camera,
            XRCamera? stereoRightEyeCamera,
            XRFrameBuffer? target,
            bool shadowPass,
            bool stereoPass,
            XRMaterial? globalMaterialOverride,
            IRuntimeScreenSpaceUserInterface? screenSpaceUI,
            RenderCommandCollection? meshRenderCommands,
            bool applyRenderArea = true,
            ulong viewHistorySequenceId = 0UL,
            ulong viewHistoryPipelineIdentity = 0UL,
            bool viewHistoryAuthoring = false)
        {
            WindowViewport = viewport;
            Scene = scene;
            SceneCamera = camera;
            StereoRightEyeCamera = stereoRightEyeCamera;
            OutputFBO = target;
            ShadowPass = shadowPass;
            StereoPass = stereoPass;
            ViewHistorySequenceId = viewHistorySequenceId;
            ViewHistoryPipelineIdentity = viewHistoryPipelineIdentity;
            ViewHistoryAuthoring = viewHistoryAuthoring;
            ViewHistoryCaptureAccepted = true;
            GlobalMaterialOverride = globalMaterialOverride;
            ScreenSpaceUserInterface = screenSpaceUI?.IsScreenSpace == true ? screenSpaceUI : null;
            MeshRenderCommands = meshRenderCommands;
            CapturePolicy = viewport?.CapturePolicy ?? RenderCapturePolicy.None;
            RenderFrameViewSet? capturedViews = camera is null
                ? null
                : stereoPass && RenderFrameViewSetPublication.TryGetLatest(
                    out RenderFrameViewSet openXrViews)
                    ? openXrViews
                    : RenderFrameViewSetCapture.Capture(this);
            if (Scene is not null && capturedViews is RenderFrameViewSet views)
            {
                RenderWorldSnapshot snapshot = RenderWorldSnapshotPublication.Acquire(
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    Scene,
                    Scene.GPUCommands,
                    Scene.GPUCommands.AdvancedGlobalResources);
                WorldSnapshot = snapshot;
                FrameViewSet = views;
            }
            else
            {
                WorldSnapshot = null;
                FrameViewSet = null;
            }

            if (WindowViewport is not null)
                _renderingViewports.Push(WindowViewport);

            if (Scene is not null)
                _renderingScenes.Push(Scene);

            if (SceneCamera is not null)
                _renderingCameras.Push(SceneCamera);

            // Visibility collection must capture logical view state without touching the
            // renderer's global viewport/scissor tracker. Deferred Vulkan recording can
            // run while collection is building the next frame, so mutating that tracker
            // here would let a collection viewport leak into an unrelated mesh draw.
            _mainAttributeRenderAreaPushed.Push(applyRenderArea && PushInitialMainRenderArea(viewport, target));

            return StateObject.New(PopMainAttributesAction, this);
        }

        public void PopMainAttributes()
        {
            if (_mainAttributeRenderAreaPushed.Count > 0 && _mainAttributeRenderAreaPushed.Pop())
                PopRenderArea();

            if (WindowViewport is not null)
                _renderingViewports.Pop();

            if (Scene is not null)
                _renderingScenes.Pop();

            if (SceneCamera is not null)
                _renderingCameras.Pop();

            WindowViewport = null;
            Scene = null;
            SceneCamera = null;
            StereoRightEyeCamera = null;
            OutputFBO = null;
            ShadowPass = false;
            StereoPass = false;
            ViewHistorySequenceId = 0UL;
            ViewHistoryPipelineIdentity = 0UL;
            ViewHistoryAuthoring = false;
            ViewHistoryCaptureAccepted = true;
            DirectionalCascadeLayeredShadowPass = false;
            DirectionalCascadeInstancedLayeredShadowPass = false;
            DirectionalCascadeAtlasGroupedShadowPass = false;
            DirectionalCascadeShadowLayerCount = 0;
            PointLightLayeredShadowPass = false;
            PointLightInstancedLayeredShadowPass = false;
            PointLightAtlasGroupedShadowPass = false;
            PointLightShadowFaceCount = 0;
            GlobalMaterialOverride = null;
            ScreenSpaceUserInterface = null;
            MeshRenderCommands = null;
            CapturePolicy = RenderCapturePolicy.None;
            FrameViewSet = null;
            WorldSnapshot = null;
            LastVisibilityBatchDecision = null;
            LastVisibilityContentPolicy = ViewBatchContentPolicy.Exact;
        }

        private readonly Stack<bool> _mainAttributeRenderAreaPushed = new();

        private bool PushInitialMainRenderArea(XRViewport? viewport, XRFrameBuffer? target)
        {
            AbstractRenderer? renderer = AbstractRenderer.Current;
            bool renderingExternalSwapchainViewport =
                viewport?.RendersToExternalSwapchainTarget == true &&
                renderer?.IsRenderingExternalSwapchainTarget == true;

            if (renderingExternalSwapchainViewport &&
                renderer?.TryGetExternalSwapchainTargetRegion(out BoundingRectangle externalRegion) == true)
            {
                PushRequiredRenderArea(externalRegion, "OpenXR external swapchain target");
                return true;
            }

            if (renderingExternalSwapchainViewport)
            {
                BoundingRectangle externalViewportRegion = viewport?.InternalResolutionRegion ?? default;
                PushRequiredRenderArea(externalViewportRegion, "OpenXR external swapchain viewport");
                return true;
            }

            if (viewport?.RenderPipeline is ShadowRenderPipeline { PreserveExistingRenderArea: true } &&
                CurrentRenderRegion.Width > 0 &&
                CurrentRenderRegion.Height > 0)
            {
                return false;
            }

            if (target is not null)
            {
                BoundingRectangle targetRegion = CreateFrameBufferRenderArea(target);
                if (targetRegion.Width > 0 && targetRegion.Height > 0)
                {
                    PushRenderArea(targetRegion);
                    return true;
                }
            }

            if (viewport is null)
                return false;

            BoundingRectangle viewportRegion = viewport.InternalResolutionRegion;
            if (viewportRegion.Width <= 0 || viewportRegion.Height <= 0)
                viewportRegion = viewport.Region;

            if (viewportRegion.Width <= 0 || viewportRegion.Height <= 0)
                return false;

            PushRenderArea(viewportRegion);
            return true;
        }

        private void PushRequiredRenderArea(BoundingRectangle region, string reason)
        {
            if (region.Width <= 0 || region.Height <= 0)
            {
                throw new InvalidOperationException(
                    $"{reason} requires a non-zero render area before frame-op capture. " +
                    $"Region={region.X},{region.Y},{region.Width}x{region.Height}.");
            }

            PushRenderArea(region);
        }

        private static BoundingRectangle CreateFrameBufferRenderArea(XRFrameBuffer target)
        {
            if (target.Width == 0u || target.Height == 0u)
                return default;

            if (target.Width > int.MaxValue || target.Height > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Framebuffer render area {target.Width}x{target.Height} exceeds supported render-region dimensions.");
            }

            return new BoundingRectangle(0, 0, (int)target.Width, (int)target.Height);
        }

        private const byte DirectionalLayeredShadowScopeKind = 1;
        private const byte PointLayeredShadowScopeKind = 2;
        private const int MaxLayeredShadowScopeDepth = 8;
        private readonly LayeredShadowUniformState[] _layeredShadowScopeSnapshots =
            new LayeredShadowUniformState[MaxLayeredShadowScopeDepth];
        private readonly byte[] _layeredShadowScopeKinds =
            new byte[MaxLayeredShadowScopeDepth];
        private int _layeredShadowScopeDepth;

        public StateObject PushDirectionalCascadeLayeredShadowPass(
            bool instancedLayered,
            ReadOnlySpan<Matrix4x4> cascadeMatrices,
            bool atlasGrouped = false)
        {
            PushLayeredShadowScope(DirectionalLayeredShadowScopeKind);
            DirectionalCascadeLayeredShadowPass = true;
            DirectionalCascadeInstancedLayeredShadowPass = instancedLayered;
            DirectionalCascadeAtlasGroupedShadowPass = atlasGrouped;
            DirectionalCascadeShadowLayerCount = Math.Clamp(cascadeMatrices.Length, 0, _directionalCascadeShadowMatrices.Length);
            for (int i = 0; i < DirectionalCascadeShadowLayerCount; i++)
                _directionalCascadeShadowMatrices[i] = cascadeMatrices[i];
            // Layered matrices are program bindings just like the explicit
            // scoped binding stacks below. Advance the shared revision so a
            // per-frame binding snapshot can be reused inside this exact
            // shadow scope, but never across a different cascade publication.
            IncrementScopedBindingRevision();
            return StateObject.New(PopDirectionalCascadeLayeredShadowPassAction, this);
        }

        public bool TryGetDirectionalCascadeShadowMatrix(int index, out Matrix4x4 matrix)
        {
            if ((uint)index < (uint)DirectionalCascadeShadowLayerCount)
            {
                matrix = _directionalCascadeShadowMatrices[index];
                return true;
            }

            matrix = Matrix4x4.Identity;
            return false;
        }

        private void PopDirectionalCascadeLayeredShadowPass()
            => PopLayeredShadowScope(DirectionalLayeredShadowScopeKind);

        public StateObject PushPointLightLayeredShadowPass(
            bool instancedLayered,
            ReadOnlySpan<Matrix4x4> faceMatrices,
            ReadOnlySpan<int> faceIndices = default,
            bool atlasGrouped = false)
        {
            PushLayeredShadowScope(PointLayeredShadowScopeKind);
            PointLightLayeredShadowPass = true;
            PointLightInstancedLayeredShadowPass = instancedLayered;
            PointLightAtlasGroupedShadowPass = atlasGrouped;
            PointLightShadowFaceCount = Math.Clamp(faceMatrices.Length, 0, _pointLightShadowFaceMatrices.Length);
            for (int i = 0; i < PointLightShadowFaceCount; i++)
            {
                _pointLightShadowFaceMatrices[i] = faceMatrices[i];
                _pointLightShadowFaceIndices[i] = i < faceIndices.Length ? faceIndices[i] : i;
            }
            IncrementScopedBindingRevision();
            return StateObject.New(PopPointLightLayeredShadowPassAction, this);
        }

        public bool TryGetPointLightShadowFaceMatrix(int index, out Matrix4x4 matrix)
        {
            if ((uint)index < (uint)PointLightShadowFaceCount)
            {
                matrix = _pointLightShadowFaceMatrices[index];
                return true;
            }

            matrix = Matrix4x4.Identity;
            return false;
        }

        public bool TryGetPointLightShadowFaceIndex(int index, out int faceIndex)
        {
            if ((uint)index < (uint)PointLightShadowFaceCount)
            {
                faceIndex = _pointLightShadowFaceIndices[index];
                return true;
            }

            faceIndex = index;
            return false;
        }

        private void PopPointLightLayeredShadowPass()
            => PopLayeredShadowScope(PointLayeredShadowScopeKind);

        private void PushLayeredShadowScope(byte scopeKind)
        {
            if ((uint)_layeredShadowScopeDepth >= (uint)_layeredShadowScopeSnapshots.Length)
            {
                throw new InvalidOperationException(
                    $"Layered shadow pass nesting exceeds the supported depth of {MaxLayeredShadowScopeDepth}.");
            }

            int snapshotIndex = _layeredShadowScopeDepth++;
            _layeredShadowScopeSnapshots[snapshotIndex] =
                LayeredShadowUniformState.CaptureFromRenderingState(this);
            _layeredShadowScopeKinds[snapshotIndex] = scopeKind;
        }

        private void PopLayeredShadowScope(byte expectedScopeKind)
        {
            if (_layeredShadowScopeDepth <= 0)
                throw new InvalidOperationException("Layered shadow pass scope stack underflow.");

            int snapshotIndex = _layeredShadowScopeDepth - 1;
            byte actualScopeKind = _layeredShadowScopeKinds[snapshotIndex];
            if (actualScopeKind != expectedScopeKind)
            {
                throw new InvalidOperationException(
                    "Layered shadow pass scopes must be disposed in last-in, first-out order.");
            }

            LayeredShadowUniformState snapshot =
                _layeredShadowScopeSnapshots[snapshotIndex];
            _layeredShadowScopeSnapshots[snapshotIndex] = default;
            _layeredShadowScopeKinds[snapshotIndex] = 0;
            _layeredShadowScopeDepth = snapshotIndex;

            RestoreLayeredShadowScope(snapshot);
            IncrementScopedBindingRevision();
        }

        private void RestoreLayeredShadowScope(
            in LayeredShadowUniformState snapshot)
        {
            DirectionalCascadeLayeredShadowPass =
                snapshot.DirectionalCascadeLayeredShadowPass;
            DirectionalCascadeInstancedLayeredShadowPass =
                snapshot.DirectionalCascadeInstancedLayeredShadowPass;
            DirectionalCascadeAtlasGroupedShadowPass =
                snapshot.DirectionalCascadeAtlasGroupedShadowPass;
            DirectionalCascadeShadowLayerCount = Math.Clamp(
                snapshot.DirectionalCascadeShadowLayerCount,
                0,
                _directionalCascadeShadowMatrices.Length);
            Array.Clear(_directionalCascadeShadowMatrices);
            for (int i = 0; i < DirectionalCascadeShadowLayerCount; i++)
            {
                if (snapshot.TryGetDirectionalCascadeShadowMatrix(
                        i,
                        out Matrix4x4 matrix))
                {
                    _directionalCascadeShadowMatrices[i] = matrix;
                }
            }

            PointLightLayeredShadowPass = snapshot.PointLightLayeredShadowPass;
            PointLightInstancedLayeredShadowPass =
                snapshot.PointLightInstancedLayeredShadowPass;
            PointLightAtlasGroupedShadowPass =
                snapshot.PointLightAtlasGroupedShadowPass;
            PointLightShadowFaceCount = Math.Clamp(
                snapshot.PointLightShadowFaceCount,
                0,
                _pointLightShadowFaceMatrices.Length);
            Array.Clear(_pointLightShadowFaceMatrices);
            Array.Clear(_pointLightShadowFaceIndices);
            for (int i = 0; i < PointLightShadowFaceCount; i++)
            {
                if (snapshot.TryGetPointLightShadowFaceMatrix(
                        i,
                        out Matrix4x4 matrix))
                {
                    _pointLightShadowFaceMatrices[i] = matrix;
                }

                _pointLightShadowFaceIndices[i] =
                    snapshot.TryGetPointLightShadowFaceIndex(i, out int faceIndex)
                        ? faceIndex
                        : i;
            }
        }

        public XRCamera? RenderingCamera
            => _renderingCameras.TryPeek(out var c) ? c : null;
        public bool HasRenderingCameraScope => _renderingCameras.Count > 0;
        private readonly Stack<XRCamera?> _renderingCameras = new();
        public StateObject PushRenderingCamera(XRCamera? camera)
        {
            PushRenderingCameraState(camera);
            return StateObject.New(PopRenderingCameraAction, this);
        }
        internal void PushRenderingCameraState(XRCamera? camera)
            => _renderingCameras.Push(camera);
        public void PopRenderingCamera()
            => _renderingCameras.Pop();

        public BoundingRectangle CurrentRenderRegion
            => _renderRegionStack.TryPeek(out var area) ? area : BoundingRectangle.Empty;
        private readonly Stack<BoundingRectangle> _renderRegionStack = new();
        public StateObject PushRenderArea(int width, int height)
            => PushRenderArea(0, 0, width, height);
        public StateObject PushRenderArea(int x, int y, int width, int height)
            => PushRenderArea(new BoundingRectangle(x, y, width, height));
        public StateObject PushRenderArea(BoundingRectangle region)
        {
            PushRenderAreaState(region);
            return StateObject.New(PopRenderAreaAction, this);
        }
        internal void PushRenderAreaState(BoundingRectangle region)
        {
            _renderRegionStack.Push(region);
            AbstractRenderer.Current?.SetRenderArea(region);
        }
        public void PopRenderArea()
        {
            if (_renderRegionStack.Count <= 0)
                return;

            _renderRegionStack.Pop();
            if (_renderRegionStack.Count > 0)
                AbstractRenderer.Current?.SetRenderArea(_renderRegionStack.Peek());
            else
                AbstractRenderer.Current?.ClearRenderArea();
        }

        public BoundingRectangle CurrentCropRegion
            => _cropRegionStack.TryPeek(out var area) ? area : BoundingRectangle.Empty;
        private readonly Stack<BoundingRectangle> _cropRegionStack = new();
        public StateObject PushCropArea(int width, int height)
            => PushCropArea(0, 0, width, height);
        public StateObject PushCropArea(int x, int y, int width, int height)
            => PushCropArea(new BoundingRectangle(x, y, width, height));
        public StateObject PushCropArea(BoundingRectangle region)
        {
            PushCropAreaState(region);
            return StateObject.New(PopCropAreaAction, this);
        }
        internal void PushCropAreaState(BoundingRectangle region)
        {
            _cropRegionStack.Push(region);
            AbstractRenderer.Current?.SetCroppingEnabled(true);
            AbstractRenderer.Current?.CropRenderArea(region);
        }
        public void PopCropArea()
        {
            if (_cropRegionStack.Count <= 0)
                return;

            _cropRegionStack.Pop();
            if (_cropRegionStack.Count > 0)
                AbstractRenderer.Current?.CropRenderArea(_cropRegionStack.Peek());
            else
                AbstractRenderer.Current?.SetCroppingEnabled(false);
        }

        private readonly Stack<int> _indexedViewportScissorCounts = new();
        public StateObject PushIndexedViewportScissors(ReadOnlySpan<BoundingRectangle> viewports, ReadOnlySpan<BoundingRectangle> scissors)
        {
            int count = Math.Min(viewports.Length, scissors.Length);
            if (count <= 0 || AbstractRenderer.Current?.SetIndexedViewportScissors(viewports[..count], scissors[..count]) != true)
            {
                _indexedViewportScissorCounts.Push(0);
                return StateObject.New(PopIndexedViewportScissorsAction, this);
            }

            _indexedViewportScissorCounts.Push(count);
            return StateObject.New(PopIndexedViewportScissorsAction, this);
        }

        private void PopIndexedViewportScissors()
        {
            if (_indexedViewportScissorCounts.Count <= 0)
                return;

            int count = _indexedViewportScissorCounts.Pop();
            if (count > 0)
                AbstractRenderer.Current?.ClearIndexedViewportScissors(count);

            if (_renderRegionStack.TryPeek(out BoundingRectangle renderArea))
                AbstractRenderer.Current?.SetRenderArea(renderArea);
            else
                AbstractRenderer.Current?.ClearRenderArea();

            if (_cropRegionStack.TryPeek(out BoundingRectangle cropArea))
            {
                AbstractRenderer.Current?.SetCroppingEnabled(true);
                AbstractRenderer.Current?.CropRenderArea(cropArea);
            }
            else
            {
                AbstractRenderer.Current?.SetCroppingEnabled(false);
            }
        }

        public readonly record struct ScopedRenderTargetBinding(string Name, XRFrameBuffer? FrameBuffer, bool Write);

        public ScopedRenderTargetBinding? CurrentRenderTargetBinding
            => _renderTargetBindings.TryPeek(out var binding) ? binding : null;

        private readonly Stack<ScopedRenderTargetBinding> _renderTargetBindings = new();

        public StateObject PushRenderTargetBinding(string name, XRFrameBuffer? frameBuffer, bool write)
        {
            _renderTargetBindings.Push(new ScopedRenderTargetBinding(name, frameBuffer, write));
            return StateObject.New(PopRenderTargetBindingAction, this);
        }

        public void PopRenderTargetBinding()
        {
            if (_renderTargetBindings.Count > 0)
                _renderTargetBindings.Pop();
        }

        /// <summary>
        /// This material will be used to render all objects in the scene if set.
        /// </summary>
        public XRMaterial? OverrideMaterial
            => _overrideMaterials.TryPeek(out var m) ? m : null;
        private readonly Stack<XRMaterial> _overrideMaterials = new();
        public StateObject PushOverrideMaterial(XRMaterial material)
        {
            PushOverrideMaterialState(material);
            return StateObject.New(PopOverrideMaterialAction, this);
        }
        internal void PushOverrideMaterialState(XRMaterial material)
            => _overrideMaterials.Push(material);
        public void PopOverrideMaterial()
        {
            if (_overrideMaterials.Count > 0)
                _overrideMaterials.Pop();
        }

        public readonly record struct ScopedTextureBinding(
            string TextureName,
            string SamplerName,
            int TextureUnit)
        {
            public void Apply(XRRenderPipelineInstance pipeline, XRRenderProgram program)
            {
                if (string.IsNullOrWhiteSpace(TextureName) || string.IsNullOrWhiteSpace(SamplerName))
                    return;

                if (pipeline.TryGetTexture(TextureName, out XRTexture? texture) && texture is not null)
                    program.Sampler(SamplerName, texture, TextureUnit);
            }
        }

        public readonly record struct ScopedBufferBinding(
            string BufferName,
            uint BindingLocation)
        {
            public void Apply(XRRenderPipelineInstance pipeline, XRRenderProgram program)
            {
                if (string.IsNullOrWhiteSpace(BufferName))
                    return;

                if (pipeline.TryGetBuffer(BufferName, out XRDataBuffer? buffer) && buffer is not null)
                    program.BindBuffer(buffer, BindingLocation);
            }
        }

        public sealed class ScopedShaderGlobals
        {
            public Dictionary<string, bool> BoolUniforms { get; } = [];
            public Dictionary<string, int> IntUniforms { get; } = [];
            public Dictionary<string, uint> UIntUniforms { get; } = [];
            public Dictionary<string, float> FloatUniforms { get; } = [];
            public Dictionary<string, Vector2> Vector2Uniforms { get; } = [];
            public Dictionary<string, Vector3> Vector3Uniforms { get; } = [];
            public Dictionary<string, Vector4> Vector4Uniforms { get; } = [];
            public Dictionary<string, Matrix4x4> Matrix4Uniforms { get; } = [];

            public void Apply(XRRenderProgram program)
            {
                foreach (var pair in BoolUniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in IntUniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in UIntUniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in FloatUniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in Vector2Uniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in Vector3Uniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in Vector4Uniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in Matrix4Uniforms)
                    program.Uniform(pair.Key, pair.Value);
            }
        }

        public readonly record struct ScopedProgramBindings(Action<XRRenderProgram>? ApplyUniforms)
        {
            public void Apply(XRRenderProgram program)
                => ApplyUniforms?.Invoke(program);
        }

        private readonly Stack<ScopedTextureBinding> _textureBindings = new();
        private readonly Stack<ScopedBufferBinding> _bufferBindings = new();
        private readonly Stack<ScopedShaderGlobals> _shaderGlobals = new();
        private readonly Stack<ScopedProgramBindings> _programBindings = new();
        private ScopedTextureBinding[] _textureBindingScratch = [];
        private ScopedBufferBinding[] _bufferBindingScratch = [];
        private ScopedShaderGlobals[] _shaderGlobalsScratch = [];
        private ScopedProgramBindings[] _programBindingsScratch = [];
        private ulong _scopedBindingRevision = 1;

        /// <summary>
        /// Changes whenever a scoped texture, buffer, shader-global, or program-binding
        /// layer is pushed or popped. Queued backends use it to reuse immutable binding
        /// snapshots only while the exact pipeline binding scope remains active.
        /// </summary>
        public ulong ScopedBindingRevision
            => _scopedBindingRevision;

        /// <summary>
        /// Gets whether an action/resource layer is currently active. Backends
        /// without a generation contract must keep these scopes on their
        /// conservative per-draw binding path.
        /// </summary>
        public bool HasActiveScopedBindings
            => _textureBindings.Count != 0 ||
                _bufferBindings.Count != 0 ||
                _shaderGlobals.Count != 0 ||
                _programBindings.Count != 0;

        private void IncrementScopedBindingRevision()
        {
            unchecked
            {
                ulong next = _scopedBindingRevision + 1;
                _scopedBindingRevision = next == 0 ? 1 : next;
            }
        }

        public StateObject PushTextureBinding(ScopedTextureBinding binding)
        {
            PushTextureBindingState(binding);
            return StateObject.New(PopTextureBindingAction, this);
        }
        internal void PushTextureBindingState(ScopedTextureBinding binding)
        {
            _textureBindings.Push(binding);
            IncrementScopedBindingRevision();
        }

        public void PopTextureBinding()
        {
            if (_textureBindings.Count > 0)
            {
                _textureBindings.Pop();
                IncrementScopedBindingRevision();
            }
        }

        public StateObject PushBufferBinding(ScopedBufferBinding binding)
        {
            PushBufferBindingState(binding);
            return StateObject.New(PopBufferBindingAction, this);
        }
        internal void PushBufferBindingState(ScopedBufferBinding binding)
        {
            _bufferBindings.Push(binding);
            IncrementScopedBindingRevision();
        }

        public void PopBufferBinding()
        {
            if (_bufferBindings.Count > 0)
            {
                _bufferBindings.Pop();
                IncrementScopedBindingRevision();
            }
        }

        public StateObject PushShaderGlobals(ScopedShaderGlobals globals)
        {
            PushShaderGlobalsState(globals);
            return StateObject.New(PopShaderGlobalsAction, this);
        }
        internal void PushShaderGlobalsState(ScopedShaderGlobals globals)
        {
            _shaderGlobals.Push(globals);
            IncrementScopedBindingRevision();
        }

        public void PopShaderGlobals()
        {
            if (_shaderGlobals.Count > 0)
            {
                _shaderGlobals.Pop();
                IncrementScopedBindingRevision();
            }
        }

        public StateObject PushProgramBindings(ScopedProgramBindings bindings)
        {
            PushProgramBindingsState(bindings);
            return StateObject.New(PopProgramBindingsAction, this);
        }
        internal void PushProgramBindingsState(ScopedProgramBindings bindings)
        {
            _programBindings.Push(bindings);
            IncrementScopedBindingRevision();
        }

        public void PopProgramBindings()
        {
            if (_programBindings.Count > 0)
            {
                _programBindings.Pop();
                IncrementScopedBindingRevision();
            }
        }

        public void ApplyScopedProgramBindings(XRRenderProgram program)
        {
            XRRenderPipelineInstance? pipeline = global::XREngine.RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (pipeline is null)
                return;

            pipeline.Variables.Apply(program);

            int textureBindingCount = CopyStackToScratch(_textureBindings, ref _textureBindingScratch);
            for (int i = textureBindingCount - 1; i >= 0; i--)
                _textureBindingScratch[i].Apply(pipeline, program);
            Array.Clear(_textureBindingScratch, 0, textureBindingCount);

            int bufferBindingCount = CopyStackToScratch(_bufferBindings, ref _bufferBindingScratch);
            for (int i = bufferBindingCount - 1; i >= 0; i--)
                _bufferBindingScratch[i].Apply(pipeline, program);
            Array.Clear(_bufferBindingScratch, 0, bufferBindingCount);

            int shaderGlobalsCount = CopyStackToScratch(_shaderGlobals, ref _shaderGlobalsScratch);
            for (int i = shaderGlobalsCount - 1; i >= 0; i--)
                _shaderGlobalsScratch[i].Apply(program);
            Array.Clear(_shaderGlobalsScratch, 0, shaderGlobalsCount);

            int programBindingsCount = CopyStackToScratch(_programBindings, ref _programBindingsScratch);
            for (int i = programBindingsCount - 1; i >= 0; i--)
                _programBindingsScratch[i].Apply(program);
            Array.Clear(_programBindingsScratch, 0, programBindingsCount);
        }

        private static int CopyStackToScratch<T>(Stack<T> stack, ref T[] scratch)
        {
            int count = stack.Count;
            if (scratch.Length < count)
                Array.Resize(ref scratch, Math.Max(count, scratch.Length == 0 ? 4 : scratch.Length * 2));
            if (count > 0)
                stack.CopyTo(scratch, 0);
            return count;
        }

        /// <summary>
        /// When true, mesh renderers should prefer a cached per-material depth-normal fragment variant
        /// instead of the original forward fragment shader during the depth+normal pre-pass.
        /// </summary>
        public bool UseDepthNormalMaterialVariants { get; private set; }
        private int _useDepthNormalMaterialVariantsDepth;
        public StateObject PushUseDepthNormalMaterialVariants()
        {
            _useDepthNormalMaterialVariantsDepth++;
            UseDepthNormalMaterialVariants = true;
            return StateObject.New(PopUseDepthNormalMaterialVariantsAction, this);
        }
        private void PopUseDepthNormalMaterialVariants()
        {
            _useDepthNormalMaterialVariantsDepth--;
            if (_useDepthNormalMaterialVariantsDepth <= 0)
            {
                _useDepthNormalMaterialVariantsDepth = 0;
                UseDepthNormalMaterialVariants = false;
            }
        }

        /// <summary>
        /// Selects the GPU-resident material-table velocity fragment variant.
        /// </summary>
        public bool UseMotionVectorMaterialVariant { get; private set; }
        private int _useMotionVectorMaterialVariantDepth;
        public StateObject PushUseMotionVectorMaterialVariant()
        {
            _useMotionVectorMaterialVariantDepth++;
            UseMotionVectorMaterialVariant = true;
            return StateObject.New(PopUseMotionVectorMaterialVariantAction, this);
        }
        private void PopUseMotionVectorMaterialVariant()
        {
            _useMotionVectorMaterialVariantDepth--;
            if (_useMotionVectorMaterialVariantDepth <= 0)
            {
                _useMotionVectorMaterialVariantDepth = 0;
                UseMotionVectorMaterialVariant = false;
            }
        }

        /// <summary>
        /// When true, camera projection matrices should be returned without jitter applied.
        /// Used by motion vectors pass to ensure consistent projections between vertex and fragment stages.
        /// </summary>
        public bool UseUnjitteredProjection { get; private set; }
        private int _unjitteredProjectionDepth;
        public StateObject PushUnjitteredProjection()
        {
            _unjitteredProjectionDepth++;
            UseUnjitteredProjection = true;
            return StateObject.New(PopUnjitteredProjectionAction, this);
        }
        private void PopUnjitteredProjection()
        {
            _unjitteredProjectionDepth--;
            if (_unjitteredProjectionDepth <= 0)
            {
                _unjitteredProjectionDepth = 0;
                UseUnjitteredProjection = false;
            }
        }

        /// <summary>
        /// When true, passes request shader pipeline mode for override rendering.
        /// The OpenGL backend still honors the global AllowShaderPipelines setting and uses
        /// active-material combined programs when pipelines are disabled.
        /// </summary>
        public bool ForceShaderPipelines { get; private set; }
        private int _forceShaderPipelinesDepth;
        public StateObject PushForceShaderPipelines()
        {
            _forceShaderPipelinesDepth++;
            ForceShaderPipelines = true;
            return StateObject.New(PopForceShaderPipelinesAction, this);
        }
        private void PopForceShaderPipelines()
        {
            _forceShaderPipelinesDepth--;
            if (_forceShaderPipelinesDepth <= 0)
            {
                _forceShaderPipelinesDepth = 0;
                ForceShaderPipelines = false;
            }
        }

        /// <summary>
        /// When true, mesh renderers should bypass material-specific vertex shaders and use their generated default vertex stage.
        /// This is used by passes like motion vectors that depend on engine-defined varyings such as FragPosLocal.
        /// </summary>
        public bool ForceGeneratedVertexProgram { get; private set; }
        private int _forceGeneratedVertexProgramDepth;
        public StateObject PushForceGeneratedVertexProgram()
        {
            _forceGeneratedVertexProgramDepth++;
            ForceGeneratedVertexProgram = true;
            return StateObject.New(PopForceGeneratedVertexProgramAction, this);
        }
        private void PopForceGeneratedVertexProgram()
        {
            _forceGeneratedVertexProgramDepth--;
            if (_forceGeneratedVertexProgramDepth <= 0)
            {
                _forceGeneratedVertexProgramDepth = 0;
                ForceGeneratedVertexProgram = false;
            }
        }

        public IReadOnlyCollection<XRViewport?> ViewportStack => _renderingViewports;

        public XRViewport? RenderingViewport
            => _renderingViewports.TryPeek(out var v) ? v : null;
        private readonly Stack<XRViewport> _renderingViewports = new();
        public StateObject PushViewport(XRViewport viewport)
        {
            _renderingViewports.Push(viewport);
            PushRenderArea(viewport.Region);
            return StateObject.New(PopViewportAction, this);
        }
        public void PopViewport()
        {
            _renderingViewports.Pop();
            PopRenderArea();
        }

        public VisualScene? RenderingScene
            => _renderingScenes.TryPeek(out var s) ? s : null;

        private readonly Stack<VisualScene> _renderingScenes = new();
        public StateObject PushRenderingScene(VisualScene scene)
        {
            _renderingScenes.Push(scene);
            return StateObject.New(PopRenderingSceneAction, this);
        }
        public void PopRenderingScene()
            => _renderingScenes.Pop();

        public StateObject RequestCameraProjectionJitter(Vector2 jitterInTexels)
            => RequestCameraProjectionJitter(jitterInTexels, null);

        public StateObject RequestCameraProjectionJitter(Vector2 jitterInTexels, Vector2? renderResolutionOverride)
        {
            var camera = RenderingCamera;
            if (camera is null)
                return StateObject.New();

            Vector2 resolution = renderResolutionOverride ?? GetActiveRenderResolution();
            return camera.PushProjectionJitter(ProjectionJitterRequest.TexelSpace(jitterInTexels, resolution));
        }

        public StateObject RequestCameraProjectionJitterClipSpace(Vector2 clipSpaceOffset)
        {
            var camera = RenderingCamera;
            if (camera is null)
                return StateObject.New();

            return camera.PushProjectionJitter(ProjectionJitterRequest.ClipSpace(clipSpaceOffset));
        }

        private Vector2 GetActiveRenderResolution()
        {
            BoundingRectangle region = CurrentRenderRegion;
            if (region.Width > 0 && region.Height > 0)
                return new Vector2(region.Width, region.Height);

            var viewport = WindowViewport;
            if (viewport is not null)
            {
                int width = viewport.InternalWidth > 0 ? viewport.InternalWidth : viewport.Width;
                int height = viewport.InternalHeight > 0 ? viewport.InternalHeight : viewport.Height;
                if (width > 0 && height > 0)
                    return new Vector2(width, height);
            }

            return Vector2.One;
        }
    }
}
