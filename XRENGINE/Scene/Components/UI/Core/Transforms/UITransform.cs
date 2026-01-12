using System.Collections;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Input.Devices;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Represents the current phase of UI layout processing.
    /// </summary>
    public enum ELayoutPhase
    {
        /// <summary>
        /// No layout operation is in progress.
        /// </summary>
        None,
        /// <summary>
        /// Measure phase: calculates desired sizes bottom-up.
        /// This phase can be parallelized for independent subtrees.
        /// </summary>
        Measure,
        /// <summary>
        /// Arrange phase: assigns final positions and sizes top-down.
        /// </summary>
        Arrange
    }

    /// <summary>
    /// Represents a UI transform in 2D space.
    /// </summary>
    [XRTransformEditor("XREngine.Editor.TransformEditors.UITransformEditor")]
    public class UITransform : TransformBase, IRenderable
    {
        private UICanvasTransform? _parentCanvas;
        public UICanvasTransform? ParentCanvas
        {
            get => _parentCanvas;
            set => SetField(ref _parentCanvas, value);
        }

        public UICanvasTransform? GetCanvasTransform()
            => ParentCanvas ?? this as UICanvasTransform;
        public UICanvasComponent? GetCanvasComponent()
            => GetCanvasTransform()?.SceneNode?.GetComponent<UICanvasComponent>();

        private string _stylingClass = string.Empty;
        /// <summary>
        /// The CSS class that this UI component uses for styling.
        /// </summary>
        public string StylingClass
        {
            get => _stylingClass;
            set => SetField(ref _stylingClass, value);
        }

        private string _stylingID = string.Empty;
        /// <summary>
        /// The CSS ID that this UI component uses for styling.
        /// </summary>
        public string StylingID
        {
            get => _stylingID;
            set => SetField(ref _stylingID, value);
        }

        protected Vector2 _translation = Vector2.Zero;
        public virtual Vector2 Translation
        {
            get => _translation;
            set => SetField(ref _translation, value);
        }

        protected Vector2 _actualLocalBottomLeftTranslation = new();
        /// <summary>
        /// This is the translation after being potentially modified by the parent's placement info.
        /// </summary>
        public Vector2 ActualLocalBottomLeftTranslation
        {
            get => _actualLocalBottomLeftTranslation;
            set => SetField(ref _actualLocalBottomLeftTranslation, value);
        }

        protected float _z = 0.0f;
        public virtual float DepthTranslation
        {
            get => _z;
            set => SetField(ref _z, value);
        }

        protected Vector3 _scale = Vector3.One;
        public virtual Vector3 Scale
        {
            get => _scale;
            set => SetField(ref _scale, value);
        }

        protected float _rotationRadians = 0.0f;
        public float RotationRadians
        {
            get => _rotationRadians;
            set => SetField(ref _rotationRadians, value);
        }
        public float RotationDegrees
        {
            get => XRMath.RadToDeg(RotationRadians);
            set => RotationRadians = XRMath.DegToRad(value);
        }

        #region Layout State Tracking

        /// <summary>
        /// Version number that increments each time layout properties change.
        /// Used for dirty checking to avoid redundant layout passes.
        /// </summary>
        protected volatile uint _layoutVersion = 0;

        /// <summary>
        /// The layout version when this transform was last measured.
        /// </summary>
        protected uint _lastMeasuredVersion = 0;

        /// <summary>
        /// The layout version when this transform was last arranged.
        /// </summary>
        protected uint _lastArrangedVersion = 0;

        /// <summary>
        /// Indicates if this transform needs to be re-measured.
        /// Thread-safe check using version comparison.
        /// </summary>
        public bool NeedsMeasure
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _lastMeasuredVersion != _layoutVersion;
        }

        /// <summary>
        /// Indicates if this transform needs to be re-arranged.
        /// </summary>
        public bool NeedsArrange
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _lastArrangedVersion != _layoutVersion;
        }

        /// <summary>
        /// Marks the layout as dirty, requiring both measure and arrange passes.
        /// This is thread-safe via interlocked increment.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void IncrementLayoutVersion()
        {
            Interlocked.Increment(ref _layoutVersion);
        }

        /// <summary>
        /// The desired size calculated during the measure phase.
        /// This is the size the transform wants to be before constraints are applied.
        /// </summary>
        protected Vector2 _desiredSize = Vector2.Zero;
        public Vector2 DesiredSize
        {
            get => _desiredSize;
            protected set => _desiredSize = value;
        }

        /// <summary>
        /// The available size passed to this transform during measure.
        /// Cached to detect if re-measure is needed due to constraint changes.
        /// </summary>
        protected Vector2 _lastMeasureConstraint = new(float.PositiveInfinity, float.PositiveInfinity);

        /// <summary>
        /// The final bounds assigned during the arrange phase.
        /// Cached to detect if re-arrange is needed.
        /// </summary>
        protected BoundingRectangleF _lastArrangeBounds = default;

        #endregion

        public RenderInfo2D DebugRenderInfo2D { get; private set; }

        public UITransform() : this(null) { }
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public UITransform(TransformBase? parent) : base(parent)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            Children.PostAnythingAdded += OnChildAdded;
            Children.PostAnythingRemoved += OnChildRemoved;
        }
        ~UITransform()
        {
            Children.PostAnythingAdded -= OnChildAdded;
            Children.PostAnythingRemoved -= OnChildRemoved;
        }

        private RenderCommandMethod2D _debugRC;
        public RenderCommandMethod2D DebugRenderCommand => _debugRC;

        protected override RenderInfo[] GetDebugRenderInfo()
            => [DebugRenderInfo2D = RenderInfo2D.New(this, _debugRC = new RenderCommandMethod2D((int)EDefaultRenderPass.OnTopForward, RenderDebug))];

        protected override Matrix4x4 CreateLocalMatrix() => 
            Matrix4x4.CreateScale(Scale) * 
            Matrix4x4.CreateFromAxisAngle(Globals.Backward, RotationRadians) *
            Matrix4x4.CreateTranslation(new Vector3(Translation, DepthTranslation));

        /// <summary>
        /// Scale and translate in/out to/from a specific point.
        /// </summary>
        /// <param name="delta"></param>
        /// <param name="worldScreenPoint"></param>
        /// <param name="minScale"></param>
        /// <param name="maxScale"></param>
        public void Zoom(float delta, Vector2 worldScreenPoint, Vector2? minScale, Vector2? maxScale)
        {
            if (Math.Abs(delta) < 0.0001f)
                return;

            Vector2 scale = new(_scale.X, _scale.Y);
            Vector2 newScale = scale - new Vector2(delta);

            if (minScale != null)
            {
                if (newScale.X < minScale.Value.X)
                    newScale.X = minScale.Value.X;

                if (newScale.Y < minScale.Value.Y)
                    newScale.Y = minScale.Value.Y;
            }

            if (maxScale != null)
            {
                if (newScale.X > maxScale.Value.X)
                    newScale.X = maxScale.Value.X;

                if (newScale.Y > maxScale.Value.Y)
                    newScale.Y = maxScale.Value.Y;
            }

            if (Vector2.Distance(scale, newScale) < 0.0001f)
                return;
            
            Translation += (worldScreenPoint - new Vector2(WorldTranslation.X, WorldTranslation.Y)) * Vector2.One / scale * delta;
            Scale = new Vector3(newScale, Scale.Z);
        }

        public event Action<UITransform>? LayoutInvalidated;
        protected void OnLayoutInvalidated()
            => LayoutInvalidated?.Invoke(this);

        /// <summary>
        /// Marks this transform's layout as needing recalculation.
        /// Propagates to the parent canvas for batched processing.
        /// </summary>
        public virtual void InvalidateLayout()
        {
            IncrementLayoutVersion();
            if (ParentCanvas != null && ParentCanvas != this)
                ParentCanvas.InvalidateLayout();
            MarkLocalModified(true);
            OnLayoutInvalidated();
        }

        /// <summary>
        /// Invalidates only the measure phase, not the full layout.
        /// Use when only size calculations need to be redone.
        /// </summary>
        public virtual void InvalidateMeasure()
        {
            IncrementLayoutVersion();
            // Propagate measure invalidation up for auto-sizing parents
            if (Parent is UIBoundableTransform parentBoundable && parentBoundable.UsesAutoSizing)
                parentBoundable.InvalidateMeasure();
            if (ParentCanvas != null && ParentCanvas != this)
                ParentCanvas.InvalidateLayout();
        }

        /// <summary>
        /// Invalidates only the arrange phase.
        /// Use when position needs updating but size is unchanged.
        /// </summary>
        public virtual void InvalidateArrange()
        {
            // Only increment if we haven't already invalidated measure
            if (!NeedsMeasure)
                IncrementLayoutVersion();
            if (ParentCanvas != null && ParentCanvas != this)
                ParentCanvas.InvalidateLayout();
        }

        #region Measure/Arrange Phase Methods

        /// <summary>
        /// Measure phase: calculates the desired size of this transform.
        /// Override in derived classes to provide custom measurement logic.
        /// </summary>
        /// <param name="availableSize">The available space from the parent.</param>
        /// <returns>The desired size of this transform.</returns>
        public virtual Vector2 Measure(Vector2 availableSize)
        {
            // Skip if already measured with same constraints
            if (!NeedsMeasure && XRMath.VectorsEqual(_lastMeasureConstraint, availableSize))
                return _desiredSize;

            using var profiler = Engine.Profiler.Start($"{nameof(UITransform)}.{nameof(Measure)}");

            _lastMeasureConstraint = availableSize;

            // Measure children and aggregate their sizes
            Vector2 childrenSize = MeasureChildren(availableSize);
            _desiredSize = childrenSize;

            _lastMeasuredVersion = _layoutVersion;
            return _desiredSize;
        }

        /// <summary>
        /// Measures all child transforms and returns the aggregate size.
        /// </summary>
        protected virtual Vector2 MeasureChildren(Vector2 availableSize)
        {
            Vector2 maxSize = Vector2.Zero;
            try
            {
                foreach (var child in Children)
                {
                    if (child is UITransform uiChild && !uiChild.IsCollapsed)
                    {
                        var childSize = uiChild.Measure(availableSize);
                        maxSize = Vector2.Max(maxSize, childSize);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            return maxSize;
        }

        /// <summary>
        /// Arrange phase: assigns final position and size to this transform.
        /// </summary>
        /// <param name="finalBounds">The final bounds assigned by the parent.</param>
        public virtual void Arrange(BoundingRectangleF finalBounds)
        {
            // Skip if already arranged with same bounds
            if (!NeedsArrange && _lastArrangeBounds.Equals(finalBounds))
                return;

            using var profiler = Engine.Profiler.Start($"{nameof(UITransform)}.{nameof(Arrange)}");

            _lastArrangeBounds = finalBounds;
            OnResizeActual(finalBounds);

            _lastArrangedVersion = _layoutVersion;
        }

        /// <summary>
        /// Arrange children within the given region.
        /// </summary>
        protected virtual void ArrangeChildren(BoundingRectangleF childRegion)
        {
            try
            {
                foreach (var child in Children)
                {
                    if (child is UITransform uiChild)
                        uiChild.Arrange(childRegion);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        #endregion

        /// <summary>
        /// Fits the layout of this UI transform to the parent region.
        /// This is the legacy single-pass layout method.
        /// </summary>
        /// <param name="parentRegion"></param>
        public virtual void FitLayout(BoundingRectangleF parentRegion)
        {

        }

        public bool IsVisible => Visibility == EVisibility.Visible;
        public bool IsHidden => Visibility == EVisibility.Hidden;
        public bool IsCollapsed => Visibility == EVisibility.Collapsed;

        public void Show() => Visibility = EVisibility.Visible;
        public void Hide() => Visibility = EVisibility.Hidden;
        public void Collapse() => Visibility = EVisibility.Collapsed;

        protected EVisibility _visibility = EVisibility.Visible;
        public virtual EVisibility Visibility
        {
            get => _visibility;
            set => SetField(ref _visibility, value);
        }

        public bool IsVisibleInHierarchy => IsVisible && (Parent is not UITransform tfm || tfm.IsVisibleInHierarchy);

        private UIChildPlacementInfo? _placementInfo = null;
        /// <summary>
        /// Dictates how this UI component is arranged within the parent transform's bounds.
        /// </summary>
        public UIChildPlacementInfo? PlacementInfo
        {
            get
            {
                Parent?.VerifyPlacementInfo(this, ref _placementInfo);
                return _placementInfo;
            }
            set => _placementInfo = value;
        }

        /// <summary>
        /// Recursively registers (or unregisters) inputs on this and all child UI components.
        /// </summary>
        /// <param name="input"></param>
        internal protected virtual void RegisterInputs(InputInterface input)
        {
            //try
            //{
            //    foreach (ISceneComponent comp in ChildComponents)
            //        if (comp is IUIComponent uiComp)
            //            uiComp.RegisterInputs(input);
            //}
            //catch (Exception ex) 
            //{
            //    Engine.LogException(ex);
            //}
        }
        //protected internal override void Start()
        //{
        //    if (this is IRenderable r)
        //        OwningUserInterface?.AddRenderableComponent(r);
        //}
        //protected internal override void Stop()
        //{
        //    if (this is IRenderable r)
        //        OwningUserInterface?.RemoveRenderableComponent(r);
        //}

        protected virtual void OnResizeChildComponents(BoundingRectangleF parentRegion)
        {
            try
            {
                //lock (Children)
                {
                    foreach (var c in Children)
                        if (c is UITransform uiTfm)
                            uiTfm.FitLayout(parentRegion);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                //_childLocker.ExitReadLock();
            }
        }

        /// <summary>
        /// Converts a local-space coordinate of a parent UI component 
        /// to a local-space coordinate of a child UI component.
        /// </summary>
        /// <param name="coordinate">The coordinate relative to the parent UI component.</param>
        /// <param name="parent">The parent UI component whose space the coordinate is already in.</param>
        /// <param name="targetChild">The UI component whose space you wish to convert the coordinate to.</param>
        /// <returns></returns>
        public static Vector2 ConvertUICoordinate(Vector2 coordinate, UITransform parent, UITransform targetChild)
            => Vector2.Transform(coordinate, targetChild.InverseWorldMatrix * parent.WorldMatrix);
        /// <summary>
        /// Converts a screen-space coordinate
        /// to a local-space coordinate of a UI component.
        /// </summary>
        /// <param name="coordinate">The coordinate relative to the screen / origin of the root UI component.</param>
        /// <param name="uiComp">The UI component whose space you wish to convert the coordinate to.</param>
        /// <param name="delta">If true, the coordinate and returned value are treated like a vector offset instead of an absolute point.</param>
        /// <returns></returns>
        public Vector2 ScreenToLocal(Vector2 coordinate)
            => Vector2.Transform(coordinate, ParentCanvas?.InverseWorldMatrix ?? Matrix4x4.Identity);
        public Vector3 ScreenToLocal(Vector3 coordinate)
            => Vector3.Transform(coordinate, ParentCanvas?.InverseWorldMatrix ?? Matrix4x4.Identity);
        public Vector3 LocalToScreen(Vector3 coordinate)
            => Vector3.Transform(coordinate, ParentCanvas?.WorldMatrix ?? Matrix4x4.Identity);
        public Vector2 LocalToScreen(Vector2 coordinate)
            => Vector2.Transform(coordinate, ParentCanvas?.WorldMatrix ?? Matrix4x4.Identity);

        public virtual float GetMaxChildWidth() => 0.0f;
        public virtual float GetMaxChildHeight() => 0.0f;

        public virtual bool Contains(Vector2 worldPoint)
        {
            var worldTranslation = WorldTranslation;
            return Vector2.Distance(worldPoint, new Vector2(worldTranslation.X, worldTranslation.Y)) < 0.0001f;
        }
        public virtual Vector2 ClosestPoint(Vector2 worldPoint)
        {
            var worldTranslation = WorldTranslation;
            return new Vector2(worldTranslation.X, worldTranslation.Y);
        }

        protected virtual void OnChildAdded(TransformBase item)
        {
            //if (item is IRenderable c && c.RenderedObjects is RenderInfo2D r2D)
            //{
            //    r2D.LayerIndex = RenderInfo2D.LayerIndex;
            //    r2D.IndexWithinLayer = RenderInfo2D.IndexWithinLayer + 1;
            //}

            if (item is UITransform uic)
                uic.InvalidateLayout();
        }
        protected virtual void OnChildRemoved(TransformBase item)
        {

        }

        protected virtual void OnResizeActual(BoundingRectangleF parentBounds)
        {
            ActualLocalBottomLeftTranslation = Translation;
        }

        public override byte[] EncodeToBytes(bool delta)
        {
            return [];
        }

        public override void DecodeFromBytes(byte[] arr)
        {

        }

        //protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        //{
        //    bool change = base.OnPropertyChanging(propName, field, @new);
        //    if (change)
        //    {
        //        switch (propName)
        //        {
                    
        //        }
        //    }
        //    return change;
        //}
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Translation):
                case nameof(DepthTranslation):
                case nameof(Scale):
                    InvalidateLayout();
                    break;
                case nameof(Visibility):
                    InvalidateLayout();
                    break;
                case nameof(ParentCanvas):
                    if (this is IRenderable r)
                        foreach (var rc in r.RenderedObjects)
                            rc.UserInterfaceCanvas = ParentCanvas?.SceneNode?.GetComponent<UICanvasComponent>();
                    //lock (Children)
                    //{
                        foreach (var child in Children)
                            if (child is UITransform uiTransform)
                                uiTransform.ParentCanvas = ParentCanvas;
                    //}
                    InvalidateLayout();
                    break;
                case nameof(PlacementInfo):
                    InvalidateLayout();
                    break;
                case nameof(Parent):
                    ParentCanvas = Parent switch
                    {
                        UICanvasTransform uiCanvas => uiCanvas,
                        UITransform uiTfm => uiTfm.ParentCanvas,
                        _ => null,
                    };
                    InvalidateLayout();
                    break;
            }
        }

        protected override void RenderDebug()
        {
            base.RenderDebug();

            if (!Engine.Rendering.Settings.RenderUITransformCoordinate || Engine.Rendering.State.IsShadowPass)
                return;
            
            Vector3 endPoint = RenderTranslation + Engine.Rendering.Debug.UIPositionBias;
            Vector3 up = RenderUp * 50.0f;
            Vector3 right = RenderRight * 50.0f;

            Engine.Rendering.Debug.RenderLine(endPoint, endPoint + up, Color.Green);
            Engine.Rendering.Debug.RenderLine(endPoint, endPoint + right, Color.Red);
        }

        /// <summary>
        /// Converts a canvas-space coordinate to a local-space coordinate of this UI component.
        /// </summary>
        /// <param name="canvasPoint"></param>
        /// <returns></returns>
        public Vector2 CanvasToLocal(Vector2 canvasPoint)
        {
            Matrix4x4 canvasToLocal = InverseWorldMatrix * (ParentCanvas?.WorldMatrix ?? Matrix4x4.Identity);
            return Vector2.Transform(canvasPoint, canvasToLocal);
        }
        /// <summary>
        /// Converts a local-space coordinate of this UI component to a canvas-space coordinate.
        /// </summary>
        /// <param name="localPoint"></param>
        /// <returns></returns>
        public Vector2 LocalToCanvas(Vector2 localPoint)
        {
            Matrix4x4 localToCanvas = WorldMatrix * (ParentCanvas?.InverseWorldMatrix ?? Matrix4x4.Identity);
            return Vector2.Transform(localPoint, localToCanvas);
        }
    }
}
