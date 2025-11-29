using System.Collections;
using System.Numerics;
using System.Threading;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    public class UICanvasTransform : UIBoundableTransform
    {
        public event Action<UICanvasTransform>? LayoutingStarted;
        public event Action<UICanvasTransform>? LayoutingFinished;

        private XRCamera? _cameraSpaceCamera;
        public XRCamera? CameraSpaceCamera
        {
            get => _cameraSpaceCamera;
            set => SetField(ref _cameraSpaceCamera, value);
        }

        private TransformBase? _cameraSpaceCameraTransformListener;

        private ECanvasDrawSpace _drawSpace = ECanvasDrawSpace.Screen;
        /// <summary>
        /// This is the space in which the canvas is drawn.
        /// Screen means the canvas is drawn on top of the viewport, and it will always be visible.
        /// Camera means the canvas is drawn in front of the camera, and will only be visible as long as nothing is clipping into it.
        /// World means the canvas is drawn in the world like any other actor, and the camera is irrelevant.
        /// </summary>
        public ECanvasDrawSpace DrawSpace
        {
            get => _drawSpace;
            set => SetField(ref _drawSpace, value);
        }

        private float _cameraDrawSpaceDistance = 1.0f;
        /// <summary>
        /// When DrawSpace is set to Camera, this is the distance from the camera.
        /// Make sure the distance lies between NearZ and FarZ of the camera, or else the UI will seem to not render.
        /// </summary>
        public float CameraDrawSpaceDistance
        {
            get => _cameraDrawSpaceDistance;
            set => SetField(ref _cameraDrawSpaceDistance, value);
        }

        private int _isLayoutInvalidated = 1; // Start invalidated
        public bool IsLayoutInvalidated
        {
            get => Volatile.Read(ref _isLayoutInvalidated) == 1;
            private set => Volatile.Write(ref _isLayoutInvalidated, value ? 1 : 0);
        }

        private int _isUpdatingLayout = 0;
        public bool IsUpdatingLayout
        {
            get => Volatile.Read(ref _isUpdatingLayout) == 1;
            private set => Volatile.Write(ref _isUpdatingLayout, value ? 1 : 0);
        }

        private Job? _layoutJob;
        /// <summary>
        /// The currently running layout job, if any.
        /// </summary>
        public Job? LayoutJob => _layoutJob;

        /// <summary>
        /// If true, layout updates will be processed over multiple frames using the job system.
        /// This prevents frame hitches for complex UI hierarchies.
        /// </summary>
        private bool _useAsyncLayout = false;
        public bool UseAsyncLayout
        {
            get => _useAsyncLayout;
            set => SetField(ref _useAsyncLayout, value);
        }

        /// <summary>
        /// Maximum number of transforms to process per frame during async layout.
        /// Higher values complete layout faster but may cause frame hitches.
        /// </summary>
        private int _maxLayoutItemsPerFrame = 50;
        public int MaxLayoutItemsPerFrame
        {
            get => _maxLayoutItemsPerFrame;
            set => SetField(ref _maxLayoutItemsPerFrame, Math.Max(1, value));
        }

        public override void InvalidateLayout()
        {
            base.InvalidateLayout();
            IsLayoutInvalidated = true;
        }

        /// <summary>
        /// Root method to update the layout of the canvas synchronously.
        /// </summary>
        public virtual void UpdateLayout()
        {
            //If the layout is not invalidated, or a parent canvas will control its layouting, don't update it as root canvas.
            if (!IsLayoutInvalidated || IsNestedCanvas)
                return;

            // Cancel any pending async layout
            _layoutJob?.Cancel();
            _layoutJob = null;

            IsLayoutInvalidated = false;
            IsUpdatingLayout = true;
            LayoutingStarted?.Invoke(this);
            
            var bounds = GetRootCanvasBounds();
            
            // Phase 1: Measure (bottom-up, can skip unchanged subtrees)
            Measure(bounds.Extents);
            
            // Phase 2: Arrange (top-down, can skip unchanged subtrees)
            Arrange(bounds);
            
            IsUpdatingLayout = false;
            LayoutingFinished?.Invoke(this);
        }

        /// <summary>
        /// Schedules an asynchronous layout update using the job system.
        /// Layout will be processed incrementally over multiple frames.
        /// </summary>
        public virtual void UpdateLayoutAsync()
        {
            //If the layout is not invalidated, job is already running, or a parent canvas controls layouting
            if (!IsLayoutInvalidated || IsNestedCanvas)
                return;

            if (_layoutJob != null && _layoutJob.IsRunning)
                return; // Layout job already in progress

            IsLayoutInvalidated = false;
            IsUpdatingLayout = true;
            LayoutingStarted?.Invoke(this);

            _layoutJob = Engine.Jobs.Schedule(
                routine: LayoutCoroutine(),
                completed: OnLayoutJobCompleted,
                error: OnLayoutJobError
            );
        }

        private void OnLayoutJobCompleted()
        {
            IsUpdatingLayout = false;
            _layoutJob = null;
            LayoutingFinished?.Invoke(this);
        }

        private void OnLayoutJobError(Exception ex)
        {
            IsUpdatingLayout = false;
            _layoutJob = null;
            Debug.LogException(ex);
            LayoutingFinished?.Invoke(this);
        }

        /// <summary>
        /// Helper class to track processed count across coroutine calls.
        /// Required because iterators cannot use ref parameters.
        /// </summary>
        private class LayoutCounter
        {
            public int Count;
            public void Increment() => Count++;
            public void Reset() => Count = 0;
        }

        /// <summary>
        /// Coroutine that performs layout in two phases, yielding periodically to avoid frame hitches.
        /// </summary>
        private IEnumerable LayoutCoroutine()
        {
            var bounds = GetRootCanvasBounds();
            var counter = new LayoutCounter();

            // Phase 1: Measure pass (bottom-up traversal with yields)
            foreach (var step in MeasureCoroutine(bounds.Extents, counter))
            {
                if (counter.Count >= _maxLayoutItemsPerFrame)
                {
                    counter.Reset();
                    yield return null; // Yield to next frame
                }
            }

            yield return null; // Yield between phases

            counter.Reset();

            // Phase 2: Arrange pass (top-down traversal with yields)
            foreach (var step in ArrangeCoroutine(bounds, counter))
            {
                if (counter.Count >= _maxLayoutItemsPerFrame)
                {
                    counter.Reset();
                    yield return null; // Yield to next frame
                }
            }
        }

        /// <summary>
        /// Coroutine for the measure phase.
        /// </summary>
        private IEnumerable MeasureCoroutine(Vector2 availableSize, LayoutCounter counter)
        {
            // Measure this transform
            Measure(availableSize);
            counter.Increment();
            yield return null;

            // Recursively measure children
            foreach (var child in Children)
            {
                if (child is UIBoundableTransform boundableChild)
                {
                    foreach (var step in MeasureChildCoroutine(boundableChild, availableSize, counter))
                        yield return step;
                }
                else if (child is UITransform uiChild)
                {
                    uiChild.Measure(availableSize);
                    counter.Increment();
                    yield return null;
                }
            }
        }

        private IEnumerable MeasureChildCoroutine(UIBoundableTransform child, Vector2 availableSize, LayoutCounter counter)
        {
            // Skip if child doesn't need measuring
            if (!child.NeedsMeasure)
            {
                yield break;
            }

            child.Measure(availableSize);
            counter.Increment();
            yield return null;

            // Recursively measure grandchildren
            foreach (var grandchild in child.Children)
            {
                if (grandchild is UIBoundableTransform boundableGrandchild)
                {
                    foreach (var step in MeasureChildCoroutine(boundableGrandchild, availableSize, counter))
                        yield return step;
                }
                else if (grandchild is UITransform uiGrandchild)
                {
                    uiGrandchild.Measure(availableSize);
                    counter.Increment();
                    yield return null;
                }
            }
        }

        /// <summary>
        /// Coroutine for the arrange phase.
        /// </summary>
        private IEnumerable ArrangeCoroutine(BoundingRectangleF bounds, LayoutCounter counter)
        {
            // Arrange this transform
            Arrange(bounds);
            counter.Increment();
            yield return null;

            // Get child region after arranging self
            var childRegion = ApplyPadding(GetActualBounds());

            // Recursively arrange children
            foreach (var child in Children)
            {
                if (child is UIBoundableTransform boundableChild)
                {
                    foreach (var step in ArrangeChildCoroutine(boundableChild, childRegion, counter))
                        yield return step;
                }
                else if (child is UITransform uiChild)
                {
                    uiChild.Arrange(childRegion);
                    counter.Increment();
                    yield return null;
                }
            }
        }

        private IEnumerable ArrangeChildCoroutine(UIBoundableTransform child, BoundingRectangleF bounds, LayoutCounter counter)
        {
            // Skip if child doesn't need arranging
            if (!child.NeedsArrange)
            {
                yield break;
            }

            child.Arrange(bounds);
            counter.Increment();
            yield return null;

            // Get child's child region
            var grandchildRegion = child.ApplyPadding(child.GetActualBounds());

            // Recursively arrange grandchildren
            foreach (var grandchild in child.Children)
            {
                if (grandchild is UIBoundableTransform boundableGrandchild)
                {
                    foreach (var step in ArrangeChildCoroutine(boundableGrandchild, grandchildRegion, counter))
                        yield return step;
                }
                else if (grandchild is UITransform uiGrandchild)
                {
                    uiGrandchild.Arrange(grandchildRegion);
                    counter.Increment();
                    yield return null;
                }
            }
        }

        /// <summary>
        /// Returns true if this canvas exists within another canvas.
        /// </summary>
        public bool IsNestedCanvas
            => ParentCanvas is not null && ParentCanvas != this;

        /// <summary>
        /// Returns the bounds of this canvas as root.
        /// No translation is applied, and the size is the requested Width x Height size of the canvas.
        /// Auto width and height are allowed.
        /// </summary>
        /// <returns></returns>
        public BoundingRectangleF GetRootCanvasBounds()
            => new(Vector2.Zero, new Vector2(GetWidth(), GetHeight()));

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            if (!base.OnPropertyChanging(propName, field, @new))
                return false;

            switch (propName)
            {
                case nameof(CameraSpaceCamera):
                    DetachCameraSpaceCameraListener();
                    break;
            }

            return true;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(CameraSpaceCamera):
                    AttachCameraSpaceCameraListener();
                    MarkWorldModified();
                    break;
                case nameof(DrawSpace):
                    MarkWorldModified();
                    break;
                case nameof(CameraDrawSpaceDistance):
                    MarkWorldModified();
                    break;
                case nameof(Translation):
                    ActualLocalBottomLeftTranslation = Translation;
                    break;
            }
        }

        private void AttachCameraSpaceCameraListener()
        {
            var camera = _cameraSpaceCamera;
            if (camera is null)
                return;

            var transform = camera.Transform;
            _cameraSpaceCameraTransformListener = transform;
            transform.RenderMatrixChanged += CameraSpaceCameraMatrixChanged;
        }

        private void DetachCameraSpaceCameraListener()
        {
            if (_cameraSpaceCameraTransformListener is null)
                return;

            _cameraSpaceCameraTransformListener.RenderMatrixChanged -= CameraSpaceCameraMatrixChanged;
            _cameraSpaceCameraTransformListener = null;
        }

        private void CameraSpaceCameraMatrixChanged(TransformBase transform, Matrix4x4 matrix)
        {
            if (DrawSpace == ECanvasDrawSpace.Camera)
                MarkWorldModified();
        }

        protected override Matrix4x4 CreateWorldMatrix()
        {
            switch (DrawSpace)
            {
                case ECanvasDrawSpace.Screen:
                    return Matrix4x4.Identity;
                case ECanvasDrawSpace.Camera:
                    if (CameraSpaceCamera is not null)
                    {
                        float depth = XRMath.DistanceToDepth(CameraDrawSpaceDistance, CameraSpaceCamera.NearZ, CameraSpaceCamera.FarZ);
                        var bottomLeft = CameraSpaceCamera.NormalizedViewportToWorldCoordinate(Vector2.Zero, depth);
                        return Matrix4x4.CreateWorld(bottomLeft, -CameraSpaceCamera.Transform.WorldForward, CameraSpaceCamera.Transform.WorldUp);
                    }
                    else
                        return base.CreateWorldMatrix();
                default:
                case ECanvasDrawSpace.World:
                    return base.CreateWorldMatrix();
            }
        }

        /// <summary>
        /// Helper method to quickly set the size of the canvas.
        /// </summary>
        /// <param name="size"></param>
        public void SetSize(Vector2 size)
        {
            Width = size.X;
            Height = size.Y;
            MinAnchor = Vector2.Zero;
            MaxAnchor = Vector2.Zero;
            NormalizedPivot = Vector2.Zero;
        }
    }
}
