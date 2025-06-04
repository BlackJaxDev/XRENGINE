using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;

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

        private bool _isLayoutInvalidated = true;
        public bool IsLayoutInvalidated
        {
            get => _isLayoutInvalidated;
            private set => SetField(ref _isLayoutInvalidated, value);
        }

        private bool _isUpdatingLayout = false;
        public bool IsUpdatingLayout
        {
            get => _isUpdatingLayout;
            private set => _isUpdatingLayout = value;
        }

        public override void InvalidateLayout()
        {
            base.InvalidateLayout();
            IsLayoutInvalidated = true;
        }

        /// <summary>
        /// Root method to update the layout of the canvas.
        /// </summary>
        public virtual void UpdateLayout()
        {
            //If the layout is not invalidated, or a parent canvas will control its layouting, don't update it as root canvas.
            if (!IsLayoutInvalidated || IsNestedCanvas)
                return;

            IsLayoutInvalidated = false;
            IsUpdatingLayout = true;
            LayoutingStarted?.Invoke(this);
            FitLayout(GetRootCanvasBounds());
            IsUpdatingLayout = false;
            LayoutingFinished?.Invoke(this);
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

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
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
