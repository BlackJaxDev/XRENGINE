using System.Collections;
using System.ComponentModel;
using XREngine.Rendering.UI.State;
using YamlDotNet.Serialization;
using System.Numerics;
using System.Threading;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    [XRTransformEditor("XREngine.Editor.TransformEditors.UICanvasTransformEditor")]
    public class UICanvasTransform : UIBoundableTransform
    {
        private UIChangeJournal? _changeJournal;

        /// <summary>Owns bounded mutation records. Records contain values, not live tree references.</summary>
        [RuntimeOnly, YamlIgnore, Browsable(false)]
        public UIChangeJournal ChangeJournal => LazyInitializer.EnsureInitialized(ref _changeJournal, static () => new UIChangeJournal());

        /// <summary>Coalesces mutations on this canvas owner. Does not run or publish layout.</summary>
        public IDisposable BeginMutationTransaction()
            => (GetMutationCanvas() ?? this).ChangeJournal.BeginTransaction();

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

        private float _dpiScale = 1.0f;
        /// <summary>Explicit UI text scale in canvas pixels; one preserves physical-pixel sizing.</summary>
        public float DpiScale
        {
            get => _dpiScale;
            set
            {
                if (!float.IsFinite(value) || value <= 0.0f)
                    throw new ArgumentOutOfRangeException(nameof(value));
                SetField(ref _dpiScale, value);
            }
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

        private UILayoutCanvasSession? _valueLayoutSession;
        private UILayoutNodeOutput[] _currentLayoutOutputs = [];
        private UIElementIdentity[] _currentLayoutIdentities = [];
        private ulong _currentLayoutGeneration;

        /// <summary>Gets the most recent validated layout values in snapshot node order.</summary>
        public ReadOnlyMemory<UILayoutNodeOutput> CurrentLayoutOutputs => _currentLayoutOutputs;
        /// <summary>Gets identities in the same order as CurrentLayoutOutputs.</summary>
        public ReadOnlyMemory<UIElementIdentity> CurrentLayoutIdentities => _currentLayoutIdentities;
        /// <summary>Changes only after the owner accepts a complete layout result.</summary>
        public ulong CurrentLayoutGeneration => _currentLayoutGeneration;
        // Internal accessors for UILayoutSystem
        internal void SetLayoutInvalidated(bool value) => IsLayoutInvalidated = value;
        internal void SetUpdatingLayout(bool value) => IsUpdatingLayout = value;
        internal void RaiseLayoutingStarted() => LayoutingStarted?.Invoke(this);
        internal void RaiseLayoutingFinished() => LayoutingFinished?.Invoke(this);
        internal void CancelLayoutJob()
        {
            _valueLayoutSession?.Cancel();
        }

        internal void PublishLayoutValues(UIElementIdentity[] identities, UILayoutNodeOutput[] outputs)
        {
            _currentLayoutIdentities = identities;
            _currentLayoutOutputs = outputs;
            _currentLayoutGeneration++;
        }

        internal void ClearLayoutValues()
        {
            _currentLayoutIdentities = [];
            _currentLayoutOutputs = [];
            _currentLayoutGeneration++;
        }

        protected override void OnDestroying()
        {
            CancelLayoutJob();
            DetachCameraSpaceCameraListener();
            base.OnDestroying();
        }

        /// <summary>Accepts one completed worker result on the canvas owner thread.</summary>
        public bool PumpLayoutResults()
        {
            ChangeJournal.VerifyMutationAccess();
            return (_valueLayoutSession ??= new UILayoutCanvasSession(this)).Pump();
        }

        public override void InvalidateLayout()
        {
            UILayoutSystem.InvalidateLayout(this);
        }

        /// <summary>
        /// Requests layout from the bounded worker path.
        /// </summary>
        public virtual void UpdateLayout()
        {
            UpdateLayoutAsync();
        }

        /// <summary>
        /// Captures an immutable input and schedules bounded worker layout.
        /// </summary>
        public virtual void UpdateLayoutAsync()
        {
            ChangeJournal.VerifyMutationAccess();
            (_valueLayoutSession ??= new UILayoutCanvasSession(this)).Request();
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
                    RecordUIChange(UIChangeDomain.Transform | UIChangeDomain.Clip | UIChangeDomain.HitTest);
                    AttachCameraSpaceCameraListener();
                    // Force immediate recalculation for Camera space so bounds are correct for octree
                    if (DrawSpace == ECanvasDrawSpace.Camera)
                        RecalculateMatrices(true, false);
                    else
                        MarkWorldModified();
                    break;
                case nameof(DrawSpace):
                    RecordUIChange(UIChangeDomain.Transform | UIChangeDomain.Clip | UIChangeDomain.HitTest | UIChangeDomain.Topology);
                    // Force immediate recalculation when switching to Camera/World space
                    if (DrawSpace != ECanvasDrawSpace.Screen)
                        RecalculateMatrices(true, false);
                    else
                        MarkWorldModified();
                    break;
                case nameof(CameraDrawSpaceDistance):
                    RecordUIChange(UIChangeDomain.Transform | UIChangeDomain.Clip | UIChangeDomain.HitTest);
                    // Force immediate recalculation for Camera space
                    if (DrawSpace == ECanvasDrawSpace.Camera)
                        RecalculateMatrices(true, false);
                    else
                        MarkWorldModified();
                    break;
                case nameof(DpiScale):
                    InvalidateMeasure();
                    RecordUIChange(UIChangeDomain.Text);
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
            {
                // For Camera space, we need immediate recalculation so the UI stays attached to the camera.
                // Using MarkWorldModified() would defer the update until SwapBuffers, which is too late
                // for proper octree positioning during CollectVisible.
                RecalculateMatrices(true, false);
            }
        }

        protected override bool HasCustomWorldMatrix => true;

        protected override Matrix4x4 CreateWorldMatrix()
        {
            switch (DrawSpace)
            {
                case ECanvasDrawSpace.Screen:
                    return Matrix4x4.Identity;
                case ECanvasDrawSpace.Camera:
                    if (CameraSpaceCamera is not null)
                    {
                        float depth = XRMath.DistanceToDepth(
                            CameraDrawSpaceDistance,
                            CameraSpaceCamera.NearZ,
                            CameraSpaceCamera.FarZ,
                            CameraSpaceCamera.IsReversedDepth);
                        var bottomLeft = CameraSpaceCamera.NormalizedViewportToWorldCoordinate(Vector2.Zero, depth);
                        return Matrix4x4.CreateWorld(bottomLeft, CameraSpaceCamera.Transform.WorldForward, CameraSpaceCamera.Transform.WorldUp);
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
