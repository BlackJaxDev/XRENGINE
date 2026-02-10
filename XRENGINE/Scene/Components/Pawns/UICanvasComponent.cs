using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
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
    public class UICanvasComponent : XRComponent
    {
        public UICanvasTransform CanvasTransform => TransformAs<UICanvasTransform>(true)!;

        /// <summary>
        /// Batch collector for instanced UI rendering.
        /// Visible UI components register with this during collect-visible so their draws
        /// can be dispatched as 1-2 instanced calls per render pass instead of N individual calls.
        /// </summary>
        public UIBatchCollector BatchCollector { get; } = new();

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
                    ResizeScreenSpace(CanvasTransform.GetActualBounds());
                    break;
            }
        }

        private void ResizeScreenSpace(BoundingRectangleF bounds)
        {
            //Recreate the size of the render tree to match the new size.
            VisualScene2D.RenderTree.Remake(bounds);

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

        public void RenderScreenSpace(XRViewport? viewport, XRFrameBuffer? outputFBO)
        {
            if (!IsActive)
                return;

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

        public void SwapBuffersScreenSpace()
        {
            if (!IsActive)
                return;

            using var sample = Engine.Profiler.Start("UICanvasComponent.SwapBuffersScreenSpace");
            BatchCollector.SwapBuffers();
            _renderPipeline.MeshRenderCommands.SwapBuffers();
            VisualScene2D.GlobalSwapBuffers();
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            // Layout runs on UpdateFrame, BEFORE XRWorldInstance.PostUpdate (PostUpdateFrame).
            // This way, layout marks dirty transforms via MarkLocalModified, and PostUpdate
            // processes them in the same frame — no 1-frame lag.
            Engine.Time.Timer.UpdateFrame += UpdateLayout;
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Engine.Time.Timer.UpdateFrame -= UpdateLayout;
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

            using var sample = Engine.Profiler.Start("UICanvasComponent.UpdateLayout");
            bool wasInvalidated = CanvasTransform.IsLayoutInvalidated;
            CanvasTransform.UpdateLayout();

            if (!wasInvalidated)
                return;

            // Screen-space UI is not owned by a world, so dirty transforms will not be
            // processed by XRWorldInstance.PostUpdate. Walk the tree and recalculate only
            // transforms that have been marked dirty (_localChanged or _worldChanged).
            if (CanvasTransform.World is null || CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                RecalculateDirtyMatrices(CanvasTransform);
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

        public void CollectVisibleItemsScreenSpace()
        {
            if (!IsActive)
                return;

            using var sample = Engine.Profiler.Start("UICanvasComponent.CollectVisibleItemsScreenSpace");

            // Ensure batch collector is wired to the pipeline
            EnsureBatchCollectorWired();

            // Layout has already been applied during PostUpdateFrame by UpdateLayout().
            // Just collect the rendered items from the quadtree.
            // Components that support batching will register with BatchCollector
            // instead of adding individual render commands.
            if (_renderPipeline.Pipeline is not null)
            {
                using var collectSample = Engine.Profiler.Start("UICanvasComponent.CollectVisibleItemsScreenSpace.CollectRenderedItems");
                VisualScene2D.CollectRenderedItems(_renderPipeline.MeshRenderCommands, Camera2D, false, null, null, false);
            }
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
