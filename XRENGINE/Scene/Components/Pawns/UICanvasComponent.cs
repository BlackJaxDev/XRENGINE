using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

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
            //If in world space, no camera will be calling this method so we have to do it here.
            if (CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen && IsActive)
                CanvasTransform.UpdateLayout();
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
        public XRRenderPipelineInstance RenderPipelineInstance => _renderPipeline;

        public RenderPipeline? RenderPipeline
        {
            get => _renderPipeline.Pipeline;
            set => _renderPipeline.Pipeline = value;
        }
    }
}
