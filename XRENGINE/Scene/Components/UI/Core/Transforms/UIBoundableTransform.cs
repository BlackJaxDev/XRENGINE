using Extensions;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Represents a UI component with area that can be aligned within its parent.
    /// </summary>
    [XRTransformEditor("XREngine.Editor.TransformEditors.UIBoundableTransformEditor")]
    public class UIBoundableTransform : UITransform
    {
        public UIBoundableTransform() : base(null)
        {
            _normalizedPivot = Vector2.Zero;
            _width = null;
            _height = null;
            _minHeight = null;
            _minWidth = null;
            _maxHeight = null;
            _maxWidth = null;
            _margins = Vector4.Zero;
            _padding = Vector4.Zero;
            _minAnchor = Vector2.Zero;
            _maxAnchor = Vector2.One;
        }
        
        protected Vector2 _actualSize = new();
        /// <summary>
        /// This is the size of the component after layout has been applied.
        /// </summary>
        public Vector2 ActualSize
        {
            get => _actualSize;
            protected set => SetField(ref _actualSize, value);
        }
        /// <summary>
        /// The width of the component after layout has been applied.
        /// </summary>
        public float ActualWidth => ActualSize.X;
        /// <summary>
        /// The height of the component after layout has been applied.
        /// </summary>
        public float ActualHeight => ActualSize.Y;

        private float? _width = null;
        /// <summary>
        /// The requested width of this component before layouting.
        /// </summary>
        public float? Width
        {
            get => _width;
            set => SetField(ref _width, value);
        }

        private float? _height = null;
        /// <summary>
        /// The requested height of this component before layouting.
        /// </summary>
        public float? Height
        {
            get => _height;
            set => SetField(ref _height, value);
        }

        private float? _minHeight, _minWidth, _maxHeight, _maxWidth;

        public float? MaxHeight
        {
            get => _maxHeight;
            set => SetField(ref _maxHeight, value);
        }
        public float? MaxWidth
        {
            get => _maxWidth;
            set => SetField(ref _maxWidth, value);
        }
        public float? MinHeight
        {
            get => _minHeight;
            set => SetField(ref _minHeight, value);
        }
        public float? MinWidth
        {
            get => _minWidth;
            set => SetField(ref _minWidth, value);
        }

        private bool _blocksInputBehind;
        /// <summary>
        /// When true, this transform blocks UI input from reaching elements behind it (lower in the hierarchy).
        /// Useful for dropdown menus, popups, and modal overlays that should prevent interaction with elements underneath.
        /// </summary>
        public bool BlocksInputBehind
        {
            get => _blocksInputBehind;
            set => SetField(ref _blocksInputBehind, value);
        }

        private Vector2 _normalizedPivot = Vector2.Zero;
        /// <summary>
        /// The origin of this component as a percentage of its size.
        /// Only affects scale and rotation.
        /// </summary>
        public Vector2 NormalizedPivot
        {
            get => _normalizedPivot;
            set => SetField(ref _normalizedPivot, value);
        }
        /// <summary>
        /// This is the origin of the component after layouting.
        /// </summary>
        public Vector2 LocalPivotTranslation
        {
            get => NormalizedPivot * ActualSize;
            set
            {
                float x = ActualSize.X.IsZero() ? 0.0f : value.X / ActualSize.X;
                float y = ActualSize.Y.IsZero() ? 0.0f : value.Y / ActualSize.Y;
                NormalizedPivot = new(x, y);
            }
        }
        public float PivotTranslationX
        {
            get => NormalizedPivot.X * ActualWidth;
            set => NormalizedPivot = new Vector2(ActualWidth.IsZero() ? 0.0f : value / ActualWidth, NormalizedPivot.Y);
        }
        public float PivotTranslationY
        {
            get => NormalizedPivot.Y * ActualHeight;
            set => NormalizedPivot = new Vector2(NormalizedPivot.X, ActualHeight.IsZero() ? 0.0f : value / ActualHeight);
        }

        private Vector4 _margins;
        /// <summary>
        /// The outside margins of this component. X = left, Y = bottom, Z = right, W = top.
        /// </summary>
        public virtual Vector4 Margins
        {
            get => _margins;
            set => SetField(ref _margins, value);
        }

        private Vector4 _padding;
        /// <summary>
        /// The inside padding of this component. X = left, Y = bottom, Z = right, W = top.
        /// </summary>
        public virtual Vector4 Padding
        {
            get => _padding;
            set => SetField(ref _padding, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Margins):
                case nameof(Padding):
                case nameof(Width):
                case nameof(Height):
                case nameof(MinHeight):
                case nameof(MinWidth):
                case nameof(MaxHeight):
                case nameof(MaxWidth):
                case nameof(NormalizedPivot):
                    InvalidateMeasure();
                    break;
                case nameof(MinAnchor):
                case nameof(MaxAnchor):
                    InvalidateArrange();
                    break;
            }
        }

        private bool ShouldMarkLocalMatrixChanged()
        {
            if (!XRMath.Approx(_prevActualBottomLeftTranslation, ActualLocalBottomLeftTranslation) ||
                !XRMath.Approx(_prevDepthTranslation, DepthTranslation) ||
                !XRMath.Approx(_prevRotationRadians, RotationRadians) ||
                !XRMath.VectorsEqual(_prevScale, Scale) ||
                !XRMath.Approx(_prevPivotTranslation, LocalPivotTranslation))
                return true;

            var p = PlacementInfo;
            if (p is not null && p.RelativePositioningChanged)
                return true;

            return false;
        }

        // Internal accessor for UILayoutSystem
        internal bool ShouldMarkLocalMatrixChangedInternal() => ShouldMarkLocalMatrixChanged();

        private Vector2 _prevActualBottomLeftTranslation = Vector2.Zero;
        private float _prevDepthTranslation = 0.0f;
        private float _prevRotationRadians = 0.0f;
        private Vector3 _prevScale = Vector3.One;
        private Vector2 _prevPivotTranslation = Vector2.Zero;

        /// <summary>
        /// Creates the local transformation of the origin relative to the parent UI transform.
        /// Translates to the parent's translation, applies the origin translation, and then applies this component's translation and scale.
        /// </summary>
        /// <returns></returns>
        protected override Matrix4x4 CreateLocalMatrix()
        {
            using var profiler = Engine.Profiler.Start("UIBoundableTransform.CreateLocalMatrix");

            Matrix4x4 mtx = Matrix4x4.CreateTranslation(new Vector3(ActualLocalBottomLeftTranslation, DepthTranslation));
            var p = PlacementInfo;
            if (p is not null)
            {
                mtx *= p.GetRelativeItemMatrix();
                p.RelativePositioningChanged = false;
            }
            if (!XRMath.VectorsEqual(Scale, Vector3.One) || RotationRadians >= float.Epsilon)
            {
                mtx *=
                    Matrix4x4.CreateTranslation(new Vector3(LocalPivotTranslation, 0.0f)) *
                    Matrix4x4.CreateScale(Scale) *
                    Matrix4x4.CreateFromAxisAngle(Globals.Backward, RotationRadians) *
                    Matrix4x4.CreateTranslation(new Vector3(-LocalPivotTranslation, 0.0f));
            }
            UpdatePrevious();
            return mtx;
        }

        private void UpdatePrevious()
        {
            _prevActualBottomLeftTranslation = ActualLocalBottomLeftTranslation;
            _prevDepthTranslation = DepthTranslation;
            _prevRotationRadians = RotationRadians;
            _prevScale = Scale;
            _prevPivotTranslation = LocalPivotTranslation;
        }

        private Vector2 _minAnchor = Vector2.Zero;
        public Vector2 MinAnchor
        {
            get => _minAnchor;
            set => SetField(ref _minAnchor, value);
        }

        private Vector2 _maxAnchor = Vector2.One;
        public Vector2 MaxAnchor
        {
            get => _maxAnchor;
            set => SetField(ref _maxAnchor, value);
        }

        internal void ChildSizeChanged()
        {
            //Invalidate this component's layout if the size of a child changes and width or height uses auto sizing.
            if (UsesAutoSizing)
                InvalidateMeasure();
        }

        /// <summary>
        /// This method sets ActualSize and ActualTranslation based on a variety of factors when fitting the component into the parent bounds.
        /// </summary>
        /// <param name="parentBounds"></param>
        protected override void OnResizeActual(BoundingRectangleF parentBounds)
        {
            using var profiler = Engine.Profiler.Start("UIBoundableTransform.OnResizeActual");

            // Use the virtual method which can be overridden in derived classes
            GetActualBounds(parentBounds, out Vector2 bottomLeftTranslation, out Vector2 size);
            // NOTE: RemakeAxisAlignedRegion is NOT called here because WorldMatrix is still stale.
            // It will be called correctly from OnWorldMatrixChanged after RecalculateMatrixHierarchy.
            
            bool sizeChanged = !XRMath.VectorsEqual(_actualSize, size);
            bool posChanged = !XRMath.VectorsEqual(_actualLocalBottomLeftTranslation, bottomLeftTranslation);
            
            // Only use property setters (which fire PropertyChanged) when values actually changed.
            // UICanvasComponent listens for ActualSize/ActualLocalBottomLeftTranslation changes
            // to call ResizeScreenSpace, which initializes the ortho camera and remakes the quadtree.
            if (sizeChanged)
                ActualSize = size;
            if (posChanged)
                ActualLocalBottomLeftTranslation = bottomLeftTranslation;
            
            // Mark the local matrix dirty if bounds changed, or if other positioning
            // factors changed (e.g., PlacementInfo offset in split/list transforms).
            // ShouldMarkLocalMatrixChanged detects PlacementInfo.RelativePositioningChanged
            // which fires when a parent split/list updates the child's offset.
            if (sizeChanged || posChanged || ShouldMarkLocalMatrixChanged())
                MarkLocalModified(true); // Force deferred to avoid re-entrant layout
        }

        /// <summary>
        /// Returns Width / Height
        /// </summary>
        /// <returns></returns>
        public float GetAspect()
            => ActualWidth / ActualHeight;

        private Func<UIBoundableTransform, float>? _calcAutoHeightCallback = null;
        /// <summary>
        /// Assign this callback for components that can determine their own height.
        /// </summary>
        [YamlIgnore]
        public Func<UIBoundableTransform, float>? CalcAutoHeightCallback
        {
            get => _calcAutoHeightCallback;
            set => SetField(ref _calcAutoHeightCallback, value);
        }

        private Func<UIBoundableTransform, float>? _calcAutoWidthCallback = null;
        /// <summary>
        /// Assign this callback for components that can determine their own width.
        /// </summary>
        [YamlIgnore]
        public Func<UIBoundableTransform, float>? CalcAutoWidthCallback
        {
            get => _calcAutoWidthCallback;
            set => SetField(ref _calcAutoWidthCallback, value);
        }

        public bool UsesAutoWidth => !Width.HasValue;
        public bool UsesAutoHeight => !Height.HasValue;
        public bool UsesAutoSizing => UsesAutoWidth || UsesAutoHeight;

        #region Measure/Arrange Overrides

        /// <summary>
        /// Measure phase: calculates the desired size based on explicit dimensions,
        /// auto callbacks, or child sizes.
        /// </summary>
        public override Vector2 Measure(Vector2 availableSize)
        {
            return UILayoutSystem.MeasureBoundable(this, availableSize);
        }

        /// <summary>
        /// Measures children to determine required width.
        /// Can be overridden for custom layout (e.g., UIListTransform).
        /// </summary>
        protected virtual float MeasureChildrenWidth(Vector2 availableSize)
        {
            return GetMaxChildWidth();
        }

        // Internal accessor for UILayoutSystem to call the virtual method
        internal float InvokeMeasureChildrenWidth(Vector2 availableSize) => MeasureChildrenWidth(availableSize);

        /// <summary>
        /// Measures children to determine required height.
        /// Can be overridden for custom layout (e.g., UIListTransform).
        /// </summary>
        protected virtual float MeasureChildrenHeight(Vector2 availableSize)
        {
            return GetMaxChildHeight();
        }

        // Internal accessor for UILayoutSystem to call the virtual method
        internal float InvokeMeasureChildrenHeight(Vector2 availableSize) => MeasureChildrenHeight(availableSize);

        /// <summary>
        /// Arrange phase: assigns final position and size.
        /// </summary>
        public override void Arrange(BoundingRectangleF finalBounds)
        {
            UILayoutSystem.ArrangeBoundable(this, finalBounds);
        }

        /// <summary>
        /// Arrange children within the padded region.
        /// Can be overridden for custom layout (e.g., UIListTransform).
        /// </summary>
        protected virtual void ArrangeChildren(BoundingRectangleF childRegion)
        {
            UILayoutSystem.ArrangeChildrenBoundable(this, childRegion);
        }

        // Internal accessor for UILayoutSystem to call the virtual method
        internal void InvokeArrangeChildren(BoundingRectangleF childRegion) => ArrangeChildren(childRegion);

        /// <summary>
        /// This method calculates the actual size and bottom left translation of the component.
        /// Can be overridden for custom fit behavior (e.g., UIFittedTransform).
        /// </summary>
        protected virtual void GetActualBounds(BoundingRectangleF parentBounds, out Vector2 bottomLeftTranslation, out Vector2 size)
        {
            UILayoutSystem.GetActualBounds(this, parentBounds, out bottomLeftTranslation, out size);
        }

        #endregion

        //TODO: cache the max child width and height?
        //private float _maxChildWidthCache = 0.0f;
        //private float _maxChildHeightCache = 0.0f;

        /// <summary>
        /// Returns the width of the component.
        /// If Width is null, this will calculate the width based on the size of child components.
        /// </summary>
        /// <returns></returns>
        public float GetWidth()
        {
            if (IsCollapsed)
                return 0.0f;

            using var profiler = Engine.Profiler.Start("UIBoundableTransform.GetWidth");

            return Width ?? CalcAutoWidthCallback?.Invoke(this) ?? GetMaxChildWidth();
        }

        private float ApplyHorizontalPadding(float width)
            => width + Padding.X + Padding.Z;
        private float ApplyHorizontalMargins(float width)
            => width + Margins.X + Margins.Z;

        /// <summary>
        /// Returns the height of the component.
        /// If Height is null, this will calculate the height based on the size of child components.
        /// </summary>
        /// <returns></returns>
        public float GetHeight()
        {
            if (IsCollapsed)
                return 0.0f;

            using var profiler = Engine.Profiler.Start("UIBoundableTransform.GetHeight");

            return Height ?? CalcAutoHeightCallback?.Invoke(this) ?? GetMaxChildHeight();
        }

        private float ApplyVerticalPadding(float height)
            => height + Padding.Y + Padding.W;
        private float ApplyVerticalMargins(float height)
            => height + Margins.Y + Margins.W;

        /// <summary>
        /// Calculates the width of the component based the widths of its children.
        /// </summary>
        /// <returns></returns>
        public override float GetMaxChildWidth()
        {
            using var profiler = Engine.Profiler.Start("UIBoundableTransform.GetMaxChildWidth");

            //lock (Children)
            //{
                var children = Children.
                    Where(x => x is UIBoundableTransform b && !b.IsCollapsed && !b.ExcludeFromParentAutoCalcWidth).
                    Cast<UIBoundableTransform>();
                float maxWidth = 0.0f;
                foreach (var child in children)
                {
                    float width = child.ApplyHorizontalMargins(child.GetWidth());
                    maxWidth = Math.Max(maxWidth, width);
                }
                return maxWidth;
            //}
        }

        /// <summary>
        /// Calculates the height of the component based the heights of its children.
        /// </summary>
        /// <returns></returns>
        public override float GetMaxChildHeight()
        {
            using var profiler = Engine.Profiler.Start("UIBoundableTransform.GetMaxChildHeight");

            //lock (Children)
            //{
                var children = Children.
                    Where(x => x is UIBoundableTransform b && !b.IsCollapsed && !b.ExcludeFromParentAutoCalcHeight).
                    Cast<UIBoundableTransform>();
                float maxHeight = 0.0f;
                foreach (var child in children)
                {
                    float height = child.ApplyVerticalMargins(child.GetHeight());
                    maxHeight = Math.Max(maxHeight, height);
                }
                return maxHeight;
            //}
        }

        public BoundingRectangleF GetActualBounds()
            => new(_actualLocalBottomLeftTranslation, _actualSize);

        /// <summary>
        /// This method is called to fit the contents of this transform into the provided bounds.
        /// Now uses the two-phase measure/arrange approach for incremental updates.
        /// </summary>
        /// <param name="parentBounds"></param>
        public override void FitLayout(BoundingRectangleF parentBounds)
        {
            UILayoutSystem.FitLayout(this, parentBounds);
        }

        protected override void OnLocalMatrixChanged(Matrix4x4 localMatrix)
        {
            using var profiler = Engine.Profiler.Start("UIBoundableTransform.OnLocalMatrixChanged");

            base.OnLocalMatrixChanged(localMatrix);
            // OLD layout path removed: OnResizeChildComponents is no longer called here.
            // Layout is now solely driven by UILayoutSystem via the ArrangeChildren virtual,
            // triggered from UICanvasComponent's PostUpdate layout pass.
        }

        protected override void OnWorldMatrixChanged(Matrix4x4 worldMatrix)
        {
            using var profiler = Engine.Profiler.Start("UIBoundableTransform.OnWorldMatrixChanged");

            RemakeAxisAlignedRegion(ActualSize, worldMatrix);
            base.OnWorldMatrixChanged(worldMatrix);
        }

        /// <summary>
        /// Applies padding to the bounds, reducing the available area for children.
        /// </summary>
        public BoundingRectangleF ApplyPadding(BoundingRectangleF bounds)
        {
            var padding = Padding;
            float left = padding.X;
            float bottom = padding.Y;
            float right = padding.Z;
            float top = padding.W;

            Vector2 size = bounds.Extents;
            Vector2 pos = bounds.Translation;

            pos += new Vector2(left, bottom);
            size -= new Vector2(left + right, bottom + top);
            bounds = new BoundingRectangleF(pos, size);
            return bounds;
        }

        /// <summary>
        /// Applies margins to the bounds, reducing the available area.
        /// </summary>
        public BoundingRectangleF ApplyMargins(BoundingRectangleF bounds)
        {
            var margins = Margins;
            float left = margins.X;
            float bottom = margins.Y;
            float right = margins.Z;
            float top = margins.W;

            Vector2 size = bounds.Extents;
            Vector2 pos = bounds.Translation;

            pos += new Vector2(left, bottom);
            size -= new Vector2(left + right, bottom + top);
            bounds = new BoundingRectangleF(pos, size);
            return bounds;
        }

        private BoundingRectangleF _axisAlignedRegion;
        public BoundingRectangleF AxisAlignedRegion
        {
            get => _axisAlignedRegion;
            protected set => SetField(ref _axisAlignedRegion, value);
        }

        private Matrix4x4 _regionWorldTransform = Matrix4x4.Identity;
        public Matrix4x4 RegionWorldTransform
        {
            get => _regionWorldTransform;
            protected set => SetField(ref _regionWorldTransform, value);
        }

        private bool _excludeFromParentAutoCalcWidth = false;
        public bool ExcludeFromParentAutoCalcWidth
        {
            get => _excludeFromParentAutoCalcWidth;
            set => SetField(ref _excludeFromParentAutoCalcWidth, value);
        }

        private bool _excludeFromParentAutoCalcHeight = false;
        public bool ExcludeFromParentAutoCalcHeight
        {
            get => _excludeFromParentAutoCalcHeight;
            set => SetField(ref _excludeFromParentAutoCalcHeight, value);
        }

        protected override void RenderDebug()
        {
            base.RenderDebug();

            if (!Engine.EditorPreferences.Debug.RenderMesh2DBounds || Engine.Rendering.State.IsShadowPass)
                return;
            
            var region = AxisAlignedRegion;
            ColorF4 color = Engine.EditorPreferences.Theme.Bounds2DColor;

            Engine.Rendering.Debug.RenderLine(
                new Vector3(region.TopLeft, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
                new Vector3(region.TopRight, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
                color);

            Engine.Rendering.Debug.RenderLine(
                new Vector3(region.TopRight, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
                new Vector3(region.BottomRight, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
                color);

            Engine.Rendering.Debug.RenderLine(
                new Vector3(region.BottomRight, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
                new Vector3(region.BottomLeft, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
                color);

            Engine.Rendering.Debug.RenderLine(
                new Vector3(region.BottomLeft, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
                new Vector3(region.TopLeft, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
                color);
        }

        protected virtual void RemakeAxisAlignedRegion(Vector2 actualSize, Matrix4x4 worldMatrix)
        {
            using var profiler = Engine.Profiler.Start("UIBoundableTransform.RemakeAxisAlignedRegion");

            Matrix4x4 mtx = Matrix4x4.CreateScale(actualSize.X, actualSize.Y, 1.0f) * worldMatrix;

            RegionWorldTransform = mtx;

            Vector3 minPos = Vector3.Transform(Vector3.Zero, mtx);
            Vector3 maxPos = Vector3.Transform(new Vector3(Vector2.One, 0.0f), mtx);

            // Make sure min is the smallest and max is the largest in case of rotation.
            Vector2 min = new(Math.Min(minPos.X, maxPos.X), Math.Min(minPos.Y, maxPos.Y));
            Vector2 max = new(Math.Max(minPos.X, maxPos.X), Math.Max(minPos.Y, maxPos.Y));

            AxisAlignedRegion = BoundingRectangleF.FromMinMaxSides(min.X, max.X, min.Y, max.Y, 0.0f, 0.0f);
        }
        public UITransform? FindDeepestComponent(Vector2 worldPoint, bool includeThis)
        {
            using var profiler = Engine.Profiler.Start("UIBoundableTransform.FindDeepestComponent");

            try
            {
                //lock (Children)
                //{
                    foreach (var c in Children)
                    {
                        if (c is not UIBoundableTransform uiComp)
                            continue;

                        UITransform? comp = uiComp.FindDeepestComponent(worldPoint, true);
                        if (comp != null)
                            return comp;
                    }
                //}
            }
            catch (Exception ex)
            {
                Debug.UIException(ex);
            }
            finally
            {
                //_childLocker.ExitReadLock();
            }

            if (includeThis && Contains(worldPoint))
                return this;

            return null;
        }
        public List<UIBoundableTransform> FindAllIntersecting(Vector2 worldPoint, bool includeThis)
        {
            using var profiler = Engine.Profiler.Start("UIBoundableTransform.FindAllIntersecting");

            List<UIBoundableTransform> list = [];
            FindAllIntersecting(worldPoint, includeThis, list);
            return list;
        }
        public void FindAllIntersecting(Vector2 worldPoint, bool includeThis, List<UIBoundableTransform> results)
        {
            try
            {
                //lock (Children)
                //{
                    foreach (var c in Children)
                        if (c is UIBoundableTransform uiTfm)
                            uiTfm.FindAllIntersecting(worldPoint, true, results);
                //}
            }
            catch// (Exception ex)
            {
                //Engine.LogException(ex);
            }
            finally
            {
                //_childLocker.ExitReadLock();
            }

            if (includeThis && Contains(worldPoint))
                results.Add(this);
        }

        protected override void OnChildAdded(TransformBase item)
        {
            base.OnChildAdded(item);

            //if (item is IRenderable c)
            //    c.RenderedObjects.LayerIndex = RenderInfo2D.LayerIndex;
        }

        public override Vector2 ClosestPoint(Vector2 worldPoint)
            => ScreenToLocal(worldPoint).Clamp(-LocalPivotTranslation, ActualSize - LocalPivotTranslation);

        public override bool Contains(Vector2 worldPoint)
            => ActualSize.Contains(ScreenToLocal(worldPoint));

        /// <summary>
        /// Returns true if the given world point projected perpendicularly to the HUD as a 2D point is contained within this component and the Z value is within the given depth margin.
        /// </summary>
        /// <param name="worldPoint"></param>
        /// <param name="zMargin">How far away the point can be on either side of the HUD for it to be considered close enough.</param>
        /// <returns></returns>
        public bool Contains(Vector3 worldPoint, float zMargin = 0.5f)
        {
            Vector3 localPoint = WorldToLocal(worldPoint);
            return Math.Abs(localPoint.Z) < zMargin && ActualSize.Contains(localPoint.XY());
        }

        public Vector2 WorldToLocal(Vector2 worldPoint)
            => Vector2.Transform(worldPoint, InverseWorldMatrix);
        public Vector2 LocalToWorld(Vector2 localPoint)
            => Vector2.Transform(localPoint, WorldMatrix);
        public Vector3 WorldToLocal(Vector2 worldPoint, float worldZ)
            => Vector3.Transform(new Vector3(worldPoint, worldZ), InverseWorldMatrix);
        public Vector3 LocalToWorld(Vector2 localPoint, float worldZ)
            => Vector3.Transform(new Vector3(localPoint, worldZ), WorldMatrix);
        public Vector3 WorldToLocal(Vector3 worldPoint)
            => Vector3.Transform(worldPoint, InverseWorldMatrix);
        public Vector3 LocalToWorld(Vector3 localPoint)
            => Vector3.Transform(localPoint, WorldMatrix);

        /// <summary>
        /// Sets parameters to stretch this component to the parent bounds.
        /// </summary>
        public void StretchToParent()
        {
            MinAnchor = new Vector2(0.0f, 0.0f);
            MaxAnchor = new Vector2(1.0f, 1.0f);
            Translation = Vector2.Zero;
            MinWidth = null;
            MinHeight = null;
            MaxWidth = null;
            MaxHeight = null;
        }

        public void UpdateRenderInfoBounds(params RenderInfo[] infos)
        {
            float w = ActualWidth;
            float h = ActualHeight;
            foreach (var info in infos)
            {
                //Don't update render info 3D if this is a 2D canvas and vice versa.
                //Otherwise, results in unnecessary octree movement updates (for potentially thousands of UI components).
                switch (info)
                {
                    case RenderInfo2D renderInfo2D when ParentCanvas?.DrawSpace == ECanvasDrawSpace.Screen:
                        renderInfo2D.CullingVolume = AxisAlignedRegion;
                        break;
                    case RenderInfo3D renderInfo3D when ParentCanvas?.DrawSpace != ECanvasDrawSpace.Screen:
                        renderInfo3D.CullingOffsetMatrix = RegionWorldTransform;
                        // AABB size should be (width, height, depth) to match the mesh coordinate system
                        // where X is horizontal (width) and Y is vertical (height)
                        renderInfo3D.LocalCullingVolume = AABB.FromSize(new Vector3(w, h, 0.1f));
                        break;
                }
            }
        }
    }
}
