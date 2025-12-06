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

            VisualScene2D.UseGpuDispatch = true;

            using var overrideMaterial = _renderPipeline.RenderState.PushOverrideMaterial(UIRenderResources.UIUberMaterial);

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
            _renderPipeline.MeshRenderCommands.SwapBuffers();
            VisualScene2D.GlobalSwapBuffers();
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            Engine.Time.Timer.CollectVisible += UpdateLayoutWorldSpace;
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Engine.Time.Timer.CollectVisible -= UpdateLayoutWorldSpace;
        }

        private void UpdateLayoutWorldSpace()
        {
            //If in world/camera space, no screen-space render path will be calling layout, so we do it here.
            if (CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen && IsActive)
            {
                CanvasTransform.UpdateLayout();
                
                // For Camera/World space, we need to force immediate render matrix updates
                // so the octree has correct bounds before collection happens.
                // The deferred system won't apply changes until SwapBuffers, which is too late.
                ForceRenderMatrixUpdatesRecursive(CanvasTransform);
            }
        }

        /// <summary>
        /// Recursively forces render matrix updates for all UI transforms.
        /// This is needed for Camera/World space UI where render matrices must be up-to-date
        /// before the world's visual scene collects items.
        /// </summary>
        private static void ForceRenderMatrixUpdatesRecursive(TransformBase transform)
        {
            // Force recalculate and set render matrix now
            transform.RecalculateMatrices(false, true);
            
            // Process children
            foreach (var child in transform.Children)
                ForceRenderMatrixUpdatesRecursive(child);
        }

        public void CollectVisibleItemsScreenSpace()
        {
            if (!IsActive)
                return;

            //Update the layout if it's invalid.
            CanvasTransform.UpdateLayout();

            //Collect the rendered items now that the layout is updated.
            if (_renderPipeline.Pipeline is not null)
                VisualScene2D.CollectRenderedItems(_renderPipeline.MeshRenderCommands, Camera2D, false, null, null, false);
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
            set => _renderPipeline.Pipeline = value;
        }
    }
}
