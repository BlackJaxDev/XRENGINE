using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components
{
    /// <summary>
    /// Renders a 2D canvas on top of the screen, in world space, or in camera space.
    /// </summary>
    [RequiresTransform(typeof(UICanvasTransform))]
    public class UICanvasComponent : XRComponent, IRenderable
    {
        private const float DefaultAutoWorldCanvasDistance = 1500.0f;

        public UICanvasTransform CanvasTransform => TransformAs<UICanvasTransform>(true)!;

        public RenderInfo[] RenderedObjects { get; }

        /// <summary>
        /// Batch collector for instanced UI rendering.
        /// Visible UI components register with this during collect-visible so their draws
        /// can be dispatched as 1-2 instanced calls per render pass instead of N individual calls.
        /// </summary>
        public UIBatchCollector BatchCollector { get; } = new();

        private readonly RenderCommandMesh3D _worldSpaceQuadCommand;
        private readonly RenderCommandMethod3D _worldSpacePreRenderCommand;
        private readonly RenderInfo3D _worldSpaceQuadRenderInfo;
        private readonly XRMaterial _offscreenMaterial;
        private readonly XRMaterialFrameBuffer _offscreenFbo;
        private bool _timerHooksInstalled = false;
        private int _collectGeneration = 0;
        private int _lastSwappedGeneration = -1;
        private int _lastRenderObservedSwapGeneration = -1;
        private bool _forceDirectRenderingForBackdropBlur = false;
        private int _renderModeDiagCount = 0;
        private bool _autoDisableOffscreenForBackdropBlur = true;
        private int _cameraBindingDiagCount = 0;

        private bool _preferOffscreenRenderingForNonScreenSpaces = true;
        /// <summary>
        /// When true, Camera/World-space canvases render to an offscreen texture and display that texture on a world quad.
        /// This preserves 2D clipping/scissoring behavior for nested UI.
        /// </summary>
        public bool PreferOffscreenRenderingForNonScreenSpaces
        {
            get => _preferOffscreenRenderingForNonScreenSpaces;
            set => SetField(ref _preferOffscreenRenderingForNonScreenSpaces, value);
        }

        public bool UseOffscreenRenderingForNonScreenSpaces()
            => _preferOffscreenRenderingForNonScreenSpaces && !_forceDirectRenderingForBackdropBlur;

        /// <summary>
        /// When true, non-screen canvases using viewport/front-buffer grab-pass materials
        /// automatically disable offscreen rendering so backdrop blur samples the real scene.
        /// Set to false when explicit offscreen mode is requested.
        /// </summary>
        public bool AutoDisableOffscreenForBackdropBlur
        {
            get => _autoDisableOffscreenForBackdropBlur;
            set => SetField(ref _autoDisableOffscreenForBackdropBlur, value);
        }

        public UICanvasComponent()
        {
            var offscreenTexture = XRTexture2D.CreateFrameBufferTexture(
                1u,
                1u,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                EPixelType.UnsignedByte,
                EFrameBufferAttachment.ColorAttachment0);

            _offscreenMaterial = XRMaterial.CreateUnlitTextureMaterialForward(offscreenTexture);
            _offscreenMaterial.EnableTransparency();
            _offscreenMaterial.RenderOptions.CullMode = ECullMode.None;

            var quadMesh = XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, false));
            var quadRenderer = new XRMeshRenderer(quadMesh, _offscreenMaterial);

            _worldSpaceQuadCommand = new RenderCommandMesh3D((int)EDefaultRenderPass.TransparentForward, quadRenderer, Matrix4x4.Identity);
            _worldSpacePreRenderCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, RenderNonScreenCanvasToTexture);
            _worldSpaceQuadRenderInfo = RenderInfo3D.New(this, _worldSpaceQuadCommand, _worldSpacePreRenderCommand);
            _worldSpaceQuadRenderInfo.PreCollectCommandsCallback = ShouldRenderWorldSpaceQuad;
            _worldSpaceQuadRenderInfo.CastsShadows = false;
            _worldSpaceQuadRenderInfo.ReceivesShadows = false;
            _worldSpaceQuadRenderInfo.VisibleInLightingProbes = false;

            _offscreenFbo = new XRMaterialFrameBuffer(_offscreenMaterial);

            RenderedObjects = new[] { _worldSpaceQuadRenderInfo };
        }

        private bool _strictOneByOneRenderCalls = false;
        /// <summary>
        /// When enabled, disables UI batching and forces strict per-item CPU render commands (1x1 draw behavior).
        /// </summary>
        public bool StrictOneByOneRenderCalls
        {
            get => _strictOneByOneRenderCalls;
            set
            {
                if (!SetField(ref _strictOneByOneRenderCalls, value))
                    return;

                BatchCollector.Enabled = !value;
                BatchCollector.Clear();
            }
        }

        public const float DefaultNearZ = -0.5f;
        public const float DefaultFarZ = 0.5f;

        public float NearZ
        {
            get => Camera2D.Parameters.NearZ;
            set => Camera2D.Parameters.NearZ = value;
        }
        public float FarZ
        {
            get => Camera2D.Parameters.FarZ;
            set => Camera2D.Parameters.FarZ = value;
        }

        protected override void OnTransformChanging()
        {
            base.OnTransformChanging();

            if (!SceneNode.IsTransformNull && Transform is UICanvasTransform tfm)
                tfm.PropertyChanged -= OnCanvasTransformPropertyChanged;
        }
        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();

            if (!SceneNode.IsTransformNull && Transform is UICanvasTransform tfm)
                tfm.PropertyChanged += OnCanvasTransformPropertyChanged;
        }

        private void OnCanvasTransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(UICanvasTransform.ActualLocalBottomLeftTranslation):
                case nameof(UICanvasTransform.ActualSize):
                case nameof(UICanvasTransform.DrawSpace):
                    ResizeScreenSpace(CanvasTransform.GetActualBounds());
                    UpdateWorldSpaceQuadData();
                    break;
            }
        }

        private void ResizeScreenSpace(BoundingRectangleF bounds)
        {
            //Recreate the size of the render tree to match the new size.
            VisualScene2D.SetBounds(bounds);

            //Update the camera parameters to match the new size.
            if (Camera2D.Parameters is XROrthographicCameraParameters orthoParams)
            {
                orthoParams.SetOriginBottomLeft();
                orthoParams.Resize(bounds.Width, bounds.Height);
            }
            else
                Camera2D.Parameters = new XROrthographicCameraParameters(bounds.Width, bounds.Height, DefaultNearZ, DefaultFarZ);

            if (Transform is UICanvasTransform tfm)
                _renderPipeline.ViewportResized(tfm.ActualSize);

            uint width = Math.Max(1u, (uint)MathF.Ceiling(bounds.Width));
            uint height = Math.Max(1u, (uint)MathF.Ceiling(bounds.Height));
            _offscreenFbo.Resize(width, height);
        }

        private XRCamera? _camera2D;
        /// <summary>
        /// This is the camera used to render the 2D canvas.
        /// </summary>
        public XRCamera Camera2D
        {
            get => _camera2D ??= new(new Transform());
            private set => SetField(ref _camera2D, value);
        }

        private VisualScene2D? _visualScene2D;
        /// <summary>
        /// This is the scene that contains all the 2D renderables.
        /// </summary>
        [YamlIgnore]
        public VisualScene2D VisualScene2D
        {
            get => _visualScene2D ??= new();
            private set => SetField(ref _visualScene2D, value);
        }

        /// <summary>
        /// Gets the user input component. Not a necessary component, so may be null.
        /// </summary>
        /// <returns></returns>
        public UICanvasInputComponent? GetInputComponent() => GetSiblingComponent<UICanvasInputComponent>();

        private int _screenRenderDiagCount = 0;
        public void RenderScreenSpace(XRViewport? viewport, XRFrameBuffer? outputFBO)
        {
            if (!IsActive)
                return;

            EnsureScreenCanvasSize(viewport);

            if (_screenRenderDiagCount < 20 || _screenRenderDiagCount % 300 == 0)
            {
                int renderingCount = _renderPipeline.MeshRenderCommands.GetRenderingCommandCount();
                Debug.Out($"[UICanvas:ScreenRender] frame={_screenRenderDiagCount} renderingCmds={renderingCount} viewport={viewport?.GetHashCode()} fbo={outputFBO?.GetHashCode()} pipelineType={_renderPipeline.Pipeline?.GetType().Name}");
            }
            _screenRenderDiagCount++;

            _renderPipeline.Render(
                VisualScene2D,
                Camera2D,
                null,
                viewport,
                outputFBO,
                null,
                false,
                false);
        }

        private int _screenSwapDiagCount = 0;
        public void SwapBuffersScreenSpace()
        {
            if (!IsActive)
                return;

            using var sample = Engine.Profiler.Start("UICanvasComponent.SwapBuffersScreenSpace");
            int updatingCount = _renderPipeline.MeshRenderCommands.GetUpdatingCommandCount();
            BatchCollector.SwapBuffers();
            _renderPipeline.MeshRenderCommands.SwapBuffers();
            VisualScene2D.GlobalSwapBuffers();
            int renderingCount = _renderPipeline.MeshRenderCommands.GetRenderingCommandCount();
            if (_screenSwapDiagCount < 20 || _screenSwapDiagCount % 300 == 0)
                Debug.Out($"[UICanvas:ScreenSwap] frame={_screenSwapDiagCount} updatingBefore={updatingCount} renderingAfter={renderingCount}");
            _screenSwapDiagCount++;
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            // Layout runs on UpdateFrame, BEFORE XRWorldInstance.PostUpdate (PostUpdateFrame).
            // This way, layout marks dirty transforms via MarkLocalModified, and PostUpdate
            // processes them in the same frame — no 1-frame lag.
            EnsureTimerHooksInstalled();

            ResizeScreenSpace(CanvasTransform.GetActualBounds());
            UpdateWorldSpaceQuadData();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            RemoveTimerHooks();
        }

        private void EnsureTimerHooksInstalled()
        {
            if (_timerHooksInstalled)
                return;

            Engine.Time.Timer.UpdateFrame -= UpdateLayout;
            Engine.Time.Timer.CollectVisible -= CollectVisibleItemsNonScreen;
            Engine.Time.Timer.SwapBuffers -= SwapBuffersNonScreen;

            Engine.Time.Timer.UpdateFrame += UpdateLayout;
            Engine.Time.Timer.CollectVisible += CollectVisibleItemsNonScreen;
            Engine.Time.Timer.SwapBuffers += SwapBuffersNonScreen;
            _timerHooksInstalled = true;
        }

        private void RemoveTimerHooks()
        {
            if (!_timerHooksInstalled)
                return;

            Engine.Time.Timer.UpdateFrame -= UpdateLayout;
            Engine.Time.Timer.CollectVisible -= CollectVisibleItemsNonScreen;
            Engine.Time.Timer.SwapBuffers -= SwapBuffersNonScreen;
            _timerHooksInstalled = false;
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
            UpdateWorldSpaceQuadData();
        }

        /// <summary>
        /// Runs the full layout pass (measure + arrange) for this canvas on the update thread.
        /// Transforms that changed during layout have MarkLocalModified called on them,
        /// which adds them to the world's dirty queue. XRWorldInstance.PostUpdate (on PostUpdateFrame)
        /// then recalculates their matrices and enqueues render matrix changes.
        /// No expensive per-frame recursive recalculation needed.
        /// </summary>
        private void UpdateLayout()
        {
            if (!IsActive)
                return;

            RefreshNonScreenRenderingMode();
            EnsureCameraSpaceBinding();

            using var sample = Engine.Profiler.Start("UICanvasComponent.UpdateLayout");
            bool wasInvalidated = CanvasTransform.IsLayoutInvalidated;
            CanvasTransform.UpdateLayout();

            var canvasTransform = CanvasTransform;
            if (canvasTransform.DrawSpace == ECanvasDrawSpace.World && ShouldAutoPlaceWorldSpaceCanvas(canvasTransform))
                UpdateWorldSpaceQuadData();
            else if (canvasTransform.DrawSpace == ECanvasDrawSpace.Camera)
                UpdateWorldSpaceQuadData();

            if (!wasInvalidated)
                return;

            // Always recalculate dirty matrices after layout, regardless of draw space.
            // The Update thread and CollectVisible thread run independently — there is no
            // guarantee that XRWorldInstance.PostUpdate will process dirty transforms before
            // CollectVisible reads the quadtree. Walking the tree here ensures the world
            // matrices (and therefore quadtree positions via RemakeAxisAlignedRegion) are
            // correct before the next CollectVisible pass.
            RecalculateDirtyMatrices(canvasTransform);
        }

        /// <summary>
        /// Walks the UI tree and recalculates only transforms whose local or world matrix
        /// was marked dirty during the layout pass. Much cheaper than RecalculateMatrixHierarchy
        /// with forceWorldRecalc=true, which recalculates EVERY node regardless.
        /// </summary>
        private static void RecalculateDirtyMatrices(TransformBase root)
        {
            using var sample = Engine.Profiler.Start("UICanvasComponent.RecalculateDirtyMatrices");

            // If this node is dirty, recalculate it and all children.
            if (root.IsLocalMatrixDirty || root.IsWorldMatrixDirty)
            {
                // RecalculateMatrixHierarchy with forceWorldRecalc=true propagates
                // to all children — correct because a parent matrix change means
                // all child world matrices are stale.
                root.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential);
                return; // Children already handled by the recursive call above
            }

            // This node is clean, but children might be dirty. Walk them.
            foreach (var child in root.Children)
                RecalculateDirtyMatrices(child);
        }

        private int _screenCollectDiagCount = 0;
        public void CollectVisibleItemsScreenSpace(XRViewport? viewport = null)
        {
            if (!IsActive)
                return;

            using var sample = Engine.Profiler.Start("UICanvasComponent.CollectVisibleItemsScreenSpace");

            // Some editor-scene attachment paths can reach screen-space rendering before
            // UpdateFrame hooks are installed. Ensure hooks are present so layout keeps
            // running every frame on the update thread.
            EnsureTimerHooksInstalled();

            // Keep layout authoritative for screen-space: if invalidated, run measure/arrange
            // before collecting render commands so anchored elements are in the correct place.
            if (CanvasTransform.IsLayoutInvalidated)
                UpdateLayout();

            EnsureScreenCanvasSize(viewport);

            // Ensure batch collector is wired to the pipeline
            EnsureBatchCollectorWired();

            // Layout has already been applied during PostUpdateFrame by UpdateLayout().
            // Just collect the rendered items from the quadtree.
            // Components that support batching will register with BatchCollector
            // instead of adding individual render commands.
            if (_renderPipeline.Pipeline is not null)
            {
                _renderPipeline.MeshRenderCommands.GetCommandsAddedCount(); // reset counter
                using var collectSample = Engine.Profiler.Start("UICanvasComponent.CollectVisibleItemsScreenSpace.CollectRenderedItems");
                VisualScene2D.CollectRenderedItems(_renderPipeline.MeshRenderCommands, Camera2D, false, null, null, false);
                int addedCount = _renderPipeline.MeshRenderCommands.GetCommandsAddedCount();
                if (_screenCollectDiagCount < 20 || _screenCollectDiagCount % 300 == 0)
                {
                    var proj = Camera2D.ProjectionMatrix;
                    var bounds = CanvasTransform.GetActualBounds();
                    Debug.Out($"[UICanvas:ScreenCollect] frame={_screenCollectDiagCount} addedCmds={addedCount} sceneItems={VisualScene2D.Renderables.Count} treeBounds={VisualScene2D.RenderTree.Bounds} canvasBounds={bounds} cam2DProj=({proj.M11:F4},{proj.M22:F4},{proj.M41:F4},{proj.M42:F4})");
                }
                _screenCollectDiagCount++;
            }
        }

        private int _collectDiagCount = 0;
        private void CollectVisibleItemsNonScreen()
        {
            if (!IsActive)
                return;

            RefreshNonScreenRenderingMode();

            var canvasTransform = CanvasTransform;
            if (canvasTransform.DrawSpace == ECanvasDrawSpace.Screen || !UseOffscreenRenderingForNonScreenSpaces())
                return;

            EnsureBatchCollectorWired();
            if (_renderPipeline.Pipeline is not null)
            {
                _collectGeneration++;
                _renderPipeline.MeshRenderCommands.GetCommandsAddedCount(); // reset counter
                VisualScene2D.CollectRenderedItems(_renderPipeline.MeshRenderCommands, Camera2D, false, null, null, false);
                int addedCount = _renderPipeline.MeshRenderCommands.GetCommandsAddedCount();
                if (_collectDiagCount < 10 || _collectDiagCount % 300 == 0)
                {
                    var proj = Camera2D.ProjectionMatrix;
                    Debug.Out($"[UICanvas:Collect] frame={_collectDiagCount} drawSpace={canvasTransform.DrawSpace} addedCmds={addedCount} cam2DProj=({proj.M11:F4},{proj.M22:F4},{proj.M41:F4},{proj.M42:F4}) sceneItems={VisualScene2D.Renderables.Count} fboSize={_offscreenFbo.Width}x{_offscreenFbo.Height}");
                }
                _collectDiagCount++;
            }
        }

        private int _swapDiagCount = 0;
        private void SwapBuffersNonScreen()
        {
            if (!IsActive)
                return;

            var canvasTransform = CanvasTransform;
            if (canvasTransform.DrawSpace == ECanvasDrawSpace.Screen || !UseOffscreenRenderingForNonScreenSpaces())
                return;

            if (_lastSwappedGeneration == _collectGeneration)
                return;
            _lastSwappedGeneration = _collectGeneration;

            int updatingCount = _renderPipeline.MeshRenderCommands.GetUpdatingCommandCount();
            BatchCollector.SwapBuffers();
            _renderPipeline.MeshRenderCommands.SwapBuffers();
            VisualScene2D.GlobalSwapBuffers();
            int renderingCount = _renderPipeline.MeshRenderCommands.GetRenderingCommandCount();

            if (_swapDiagCount < 10 || _swapDiagCount % 300 == 0)
                Debug.Out($"[UICanvas:Swap] frame={_swapDiagCount} updatingBeforeSwap={updatingCount} renderingAfterSwap={renderingCount}");
            _swapDiagCount++;
        }

        private bool ShouldRenderWorldSpaceQuad(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            if (!IsActive)
                return false;

            EnsureCameraSpaceBinding();

            var canvasTransform = CanvasTransform;
            if (canvasTransform.DrawSpace == ECanvasDrawSpace.Screen || !UseOffscreenRenderingForNonScreenSpaces())
                return false;

            if (canvasTransform.DrawSpace == ECanvasDrawSpace.World && ShouldAutoPlaceWorldSpaceCanvas(canvasTransform))
                UpdateWorldSpaceQuadData();

            return _worldSpaceQuadCommand.Mesh is not null;
        }

        private int _renderDiagCount = 0;
        private void RenderNonScreenCanvasToTexture()
        {
            if (!IsActive)
                return;

            EnsureCameraSpaceBinding();

            var canvasTransform = CanvasTransform;
            if (canvasTransform.DrawSpace == ECanvasDrawSpace.Screen || !UseOffscreenRenderingForNonScreenSpaces())
                return;

            EnsureNonScreenCanvasSize(canvasTransform);

            // Fallback path: if timer-driven collect/swap did not advance since the previous
            // render, refresh commands locally on the render thread to avoid frozen world-space UI.
            bool swapAdvancedSinceLastRender = _lastSwappedGeneration != _lastRenderObservedSwapGeneration;
            if (!swapAdvancedSinceLastRender)
            {
                if (canvasTransform.IsLayoutInvalidated)
                    UpdateLayout();

                CollectVisibleItemsNonScreen();
                SwapBuffersNonScreen();
            }
            _lastRenderObservedSwapGeneration = _lastSwappedGeneration;

            if (_renderDiagCount < 10)
            {
                var bounds = canvasTransform.GetActualBounds();
                int renderCmds = _renderPipeline.MeshRenderCommands.GetRenderingCommandCount();
                var batchCol = (_renderPipeline.Pipeline as UserInterfaceRenderPipeline)?.BatchCollector;
                Debug.Out($"[UICanvas:Render] frame={_renderDiagCount} fboSize={_offscreenFbo.Width}x{_offscreenFbo.Height} renderCmds={renderCmds} batchEnabled={batchCol?.Enabled} bounds={bounds} pipeline={_renderPipeline.Pipeline?.GetType().Name}");
                _renderDiagCount++;
            }

            _renderPipeline.Render(
                VisualScene2D,
                Camera2D,
                null,
                null,
                _offscreenFbo,
                null,
                false,
                false);
        }

        private void EnsureCameraSpaceBinding()
        {
            var canvasTransform = CanvasTransform;
            if (canvasTransform.DrawSpace != ECanvasDrawSpace.Camera)
                return;

            if (canvasTransform.CameraSpaceCamera is not null)
                return;

            XRCamera? fallbackCamera =
                Engine.State.MainPlayer?.ControlledPawn?.CameraComponent?.Camera
                ?? Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One)?.ControlledPawn?.CameraComponent?.Camera;

            if (fallbackCamera is null)
                return;

            canvasTransform.CameraSpaceCamera = fallbackCamera;
            if (_cameraBindingDiagCount < 20)
            {
                Debug.UI($"[UICanvas:CameraBinding] Assigned fallback camera for camera draw-space canvas '{SceneNode?.Name ?? "<unnamed>"}'.");
                _cameraBindingDiagCount++;
            }
        }

        private void EnsureNonScreenCanvasSize(UICanvasTransform canvasTransform)
        {
            var bounds = canvasTransform.GetActualBounds();

            // If layout hasn't produced actual bounds yet, use root canvas requested size.
            if (bounds.Width <= 1.0f || bounds.Height <= 1.0f)
                bounds = canvasTransform.GetRootCanvasBounds();

            // Final safety net for startup frames.
            float width = Math.Max(1.0f, bounds.Width);
            float height = Math.Max(1.0f, bounds.Height);
            if (width <= 1.0f && height <= 1.0f)
            {
                width = 1920.0f;
                height = 1080.0f;
            }

            bool needsResize =
                MathF.Abs(width - _offscreenFbo.Width) > 0.5f ||
                MathF.Abs(height - _offscreenFbo.Height) > 0.5f;

            var proj = Camera2D.ProjectionMatrix;
            bool invalidProjection =
                float.IsNaN(proj.M11) || float.IsInfinity(proj.M11) ||
                float.IsNaN(proj.M22) || float.IsInfinity(proj.M22);

            if (needsResize || invalidProjection)
                ResizeScreenSpace(new BoundingRectangleF(Vector2.Zero, new Vector2(width, height)));
        }

        private void RefreshNonScreenRenderingMode()
        {
            var canvasTransform = CanvasTransform;
            bool forceDirect = false;

            if (_autoDisableOffscreenForBackdropBlur && canvasTransform.DrawSpace != ECanvasDrawSpace.Screen)
                forceDirect = HasViewportGrabPassUiMaterials();

            if (_forceDirectRenderingForBackdropBlur == forceDirect)
                return;

            _forceDirectRenderingForBackdropBlur = forceDirect;
            if (_renderModeDiagCount < 20)
            {
                Debug.UI($"[UICanvas:RenderMode] drawSpace={canvasTransform.DrawSpace} preferOffscreen={_preferOffscreenRenderingForNonScreenSpaces} forceDirectForBackdropBlur={_forceDirectRenderingForBackdropBlur} effectiveOffscreen={UseOffscreenRenderingForNonScreenSpaces()}");
                _renderModeDiagCount++;
            }
        }

        private bool HasViewportGrabPassUiMaterials()
        {
            var root = SceneNode;
            if (root is null)
                return false;

            var renderables = root.FindAllDescendantComponents<UIRenderableComponent>();
            foreach (var renderable in renderables)
            {
                var material = renderable.Material;
                if (material is null)
                    continue;

                foreach (var texture in material.Textures)
                {
                    if (texture is XRTexture2D tex &&
                        tex.GrabPass is { } grab &&
                        grab.ReadBuffer < EReadBufferMode.ColorAttachment0)
                        return true;
                }
            }

            return false;
        }

        private void EnsureScreenCanvasSize(XRViewport? viewport)
        {
            var bounds = CanvasTransform.GetActualBounds();
            float width = bounds.Width;
            float height = bounds.Height;

            if ((width <= 1.0f || height <= 1.0f) && viewport is not null)
            {
                var viewportSize = viewport.Region.Size;
                if (viewportSize.X > 1 && viewportSize.Y > 1)
                {
                    width = viewportSize.X;
                    height = viewportSize.Y;
                }
            }

            if (width <= 1.0f || height <= 1.0f)
            {
                var rootBounds = CanvasTransform.GetRootCanvasBounds();
                width = Math.Max(width, rootBounds.Width);
                height = Math.Max(height, rootBounds.Height);
            }

            width = Math.Max(1.0f, width);
            height = Math.Max(1.0f, height);
            if (width <= 1.0f && height <= 1.0f)
            {
                width = 1920.0f;
                height = 1080.0f;
            }

            bool needsResize =
                MathF.Abs(width - _offscreenFbo.Width) > 0.5f ||
                MathF.Abs(height - _offscreenFbo.Height) > 0.5f;

            var proj = Camera2D.ProjectionMatrix;
            bool invalidProjection =
                float.IsNaN(proj.M11) || float.IsInfinity(proj.M11) ||
                float.IsNaN(proj.M22) || float.IsInfinity(proj.M22);

            if (needsResize || invalidProjection)
                ResizeScreenSpace(new BoundingRectangleF(Vector2.Zero, new Vector2(width, height)));
        }

        private void UpdateWorldSpaceQuadData()
        {
            var tfm = CanvasTransform;
            var bounds = tfm.GetActualBounds();
            if (bounds.Width <= 1.0f || bounds.Height <= 1.0f)
                bounds = tfm.GetRootCanvasBounds();

            float width = Math.Max(1.0f, bounds.Width);
            float height = Math.Max(1.0f, bounds.Height);
            if (width <= 1.0f && height <= 1.0f)
            {
                width = 1920.0f;
                height = 1080.0f;
            }

            var world = ResolveWorldSpaceCanvasMatrix(tfm, width, height);
            _worldSpaceQuadCommand.WorldMatrix = Matrix4x4.CreateScale(width, height, 1.0f) * world;

            _worldSpaceQuadRenderInfo.LocalCullingVolume = AABB.FromSize(new Vector3(width, height, 0.05f));
            _worldSpaceQuadRenderInfo.CullingOffsetMatrix = Matrix4x4.CreateTranslation(width * 0.5f, height * 0.5f, 0.0f) * world;
        }

        private Matrix4x4 ResolveWorldSpaceCanvasMatrix(UICanvasTransform transform, float width, float height)
        {
            var world = transform.RenderMatrix;
            if (transform.DrawSpace == ECanvasDrawSpace.Camera)
            {
                if (TryGetCameraSpaceBasis(transform, out var csCameraPosition, out var csCameraForward, out var csCameraUp, out var csCameraRight))
                {
                    float distance = Math.Max(0.1f, transform.CameraDrawSpaceDistance);
                    var csBottomLeft =
                        csCameraPosition +
                        csCameraForward * distance -
                        csCameraRight * (width * 0.5f) -
                        csCameraUp * (height * 0.5f);

                    return Matrix4x4.CreateWorld(csBottomLeft, -csCameraForward, csCameraUp);
                }

                return world;
            }

            if (transform.DrawSpace != ECanvasDrawSpace.World)
                return world;

            if (!ShouldAutoPlaceWorldSpaceCanvas(transform))
                return world;

            if (!TryGetPrimaryCameraBasis(out var cameraPosition, out var cameraForward, out var cameraUp, out var cameraRight))
                return world;

            var bottomLeft =
                cameraPosition +
                cameraForward * DefaultAutoWorldCanvasDistance -
                cameraRight * (width * 0.5f) -
                cameraUp * (height * 0.5f);

            return Matrix4x4.CreateWorld(bottomLeft, -cameraForward, cameraUp);
        }

        private static bool TryGetCameraSpaceBasis(UICanvasTransform transform, out Vector3 position, out Vector3 forward, out Vector3 up, out Vector3 right)
        {
            position = Vector3.Zero;
            forward = Globals.Forward;
            up = Globals.Up;
            right = Globals.Right;

            var camera = transform.CameraSpaceCamera
                ?? Engine.State.MainPlayer?.ControlledPawn?.CameraComponent?.Camera
                ?? Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One)?.ControlledPawn?.CameraComponent?.Camera;

            var cameraTransform = camera?.Transform;
            if (cameraTransform is null)
                return false;

            position = cameraTransform.WorldTranslation;
            forward = cameraTransform.WorldForward;
            up = cameraTransform.WorldUp;
            right = cameraTransform.WorldRight;
            return true;
        }

        private static bool ShouldAutoPlaceWorldSpaceCanvas(UICanvasTransform transform)
        {
            if (transform.Parent is not null)
                return false;

            if (transform.Translation.LengthSquared() > 0.0001f)
                return false;

            if (MathF.Abs(transform.DepthTranslation) > 0.0001f)
                return false;

            if (MathF.Abs(transform.RotationRadians) > 0.0001f)
                return false;

            if (Vector3.DistanceSquared(transform.Scale, Vector3.One) > 0.0001f)
                return false;

            return true;
        }

        private static bool TryGetPrimaryCameraBasis(out Vector3 position, out Vector3 forward, out Vector3 up, out Vector3 right)
        {
            position = Vector3.Zero;
            forward = Globals.Forward;
            up = Globals.Up;
            right = Globals.Right;

            var player = Engine.State.MainPlayer ?? Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One);
            var cameraTransform = player?.ControlledPawn?.CameraComponent?.Transform;
            if (cameraTransform is null)
                return false;

            position = cameraTransform.WorldTranslation;
            forward = cameraTransform.WorldForward;
            up = cameraTransform.WorldUp;
            right = cameraTransform.WorldRight;
            return true;
        }

        public UIComponent? FindDeepestComponent(Vector2 normalizedViewportPosition)
            => FindDeepestComponents(normalizedViewportPosition).LastOrDefault();

        public UIComponent?[] FindDeepestComponents(Vector2 normalizedViewportPosition)
        {
            var results = VisualScene2D.RenderTree.Collect(x => x.Bounds.Contains(normalizedViewportPosition), y => y?.CullingVolume?.Contains(normalizedViewportPosition) ?? true);
            return [.. OrderQuadtreeResultsByDepth(results)];
        }

        private static IEnumerable<UIComponent?> OrderQuadtreeResultsByDepth(SortedDictionary<int, List<RenderInfo2D>> results)
            => results.Values.SelectMany(x => x).Select(x => x.Owner as UIComponent).Where(x => x is not null).OrderBy(x => x!.Transform.Depth);

        private readonly XRRenderPipelineInstance _renderPipeline = new() { Pipeline = new UserInterfaceRenderPipeline() };
        [YamlIgnore]
        public XRRenderPipelineInstance RenderPipelineInstance => _renderPipeline;

        [YamlIgnore]
        public RenderPipeline? RenderPipeline
        {
            get => _renderPipeline.Pipeline;
            set
            {
                _renderPipeline.Pipeline = value;
                // Keep the batch collector wired to the active pipeline
                if (value is UserInterfaceRenderPipeline uiPipeline)
                    uiPipeline.BatchCollector = BatchCollector;
            }
        }

        /// <summary>
        /// Ensures the batch collector is wired to the active UI render pipeline on first use.
        /// </summary>
        private void EnsureBatchCollectorWired()
        {
            if (_renderPipeline.Pipeline is UserInterfaceRenderPipeline uiPipeline && uiPipeline.BatchCollector is null)
                uiPipeline.BatchCollector = BatchCollector;
        }
    }
}
