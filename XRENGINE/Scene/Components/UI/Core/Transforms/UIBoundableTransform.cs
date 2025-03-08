using Extensions;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using static XREngine.Globals;

namespace XREngine.Rendering.UI
{
    public class UIScrollableTransform : UIBoundableTransform
    {
        public UIScrollableTransform()
        {
            Scrollable = true;
            ScrollableX = true;
            ScrollableY = true;
            ScrollableXMargin = 0.0f;
            ScrollableYMargin = 0.0f;
            ScrollableXMin = 0.0f;
            ScrollableYMin = 0.0f;
            ScrollableXMax = 0.0f;
            ScrollableYMax = 0.0f;
        }
        private bool _scrollable;
        public bool Scrollable
        {
            get => _scrollable;
            set => SetField(ref _scrollable, value);
        }
        private bool _scrollableX;
        public bool ScrollableX
        {
            get => _scrollableX;
            set => SetField(ref _scrollableX, value);
        }
        private bool _scrollableY;
        public bool ScrollableY
        {
            get => _scrollableY;
            set => SetField(ref _scrollableY, value);
        }
        private float _scrollableXMargin;
        public float ScrollableXMargin
        {
            get => _scrollableXMargin;
            set => SetField(ref _scrollableXMargin, value);
        }
        private float _scrollableYMargin;
        public float ScrollableYMargin
        {
            get => _scrollableYMargin;
            set => SetField(ref _scrollableYMargin, value);
        }
        private float _scrollableXMin;
        public float ScrollableXMin
        {
            get => _scrollableXMin;
            set => SetField(ref _scrollableXMin, value);
        }
        private float _scrollableYMin;
        public float ScrollableYMin
        {
            get => _scrollableYMin;
            set => SetField(ref _scrollableYMin, value);
        }
        private float _scrollableXMax;
        public float ScrollableXMax
        {
            get => _scrollableXMax;
            set => SetField(ref _scrollableXMax, value);
        }
        private float _scrollableYMax;
        public float ScrollableYMax
        {
            get => _scrollableYMax;
            set => SetField(ref _scrollableYMax, value);
        }

        protected override void OnResizeChildComponents(BoundingRectangleF parentRegion)
        {
            base.OnResizeChildComponents(parentRegion);
        }
        public override void VerifyPlacementInfo(UITransform childTransform, ref UIChildPlacementInfo? placementInfo)
        {
            base.VerifyPlacementInfo(childTransform, ref placementInfo);
        }
        private class UIScrollablePlacementInfo(UITransform owner) : UIChildPlacementInfo(owner)
        {
            public Vector2 BottomLeftOffset { get; set; }

            public override Matrix4x4 GetRelativeItemMatrix()
                => Matrix4x4.CreateTranslation(new Vector3(BottomLeftOffset, 0.0f));
        }
    }
    /// <summary>
    /// Represents a UI component with area that can be aligned within its parent.
    /// </summary>
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
                    InvalidateLayout();
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
            if (!Width.HasValue || !Height.HasValue)
                InvalidateLayout();
        }

        /// <summary>
        /// This method sets ActualSize and ActualTranslation based on a variety of factors when fitting the component into the parent bounds.
        /// </summary>
        /// <param name="parentBounds"></param>
        protected override void OnResizeActual(BoundingRectangleF parentBounds)
        {
            GetActualBounds(parentBounds, out Vector2 bottomLeftTranslation, out Vector2 size);
            RemakeAxisAlignedRegion(size);
            ActualSize = size;
            ActualLocalBottomLeftTranslation = bottomLeftTranslation;
        }

        /// <summary>
        /// This method calculates the actual size and bottom left translation of the component.
        /// </summary>
        /// <param name="parentBounds"></param>
        /// <param name="bottomLeftTranslation"></param>
        /// <param name="size"></param>
        protected virtual void GetActualBounds(BoundingRectangleF parentBounds, out Vector2 bottomLeftTranslation, out Vector2 size)
        {
            GetAnchors(
                parentBounds.Width,
                parentBounds.Height,
                out float minX,
                out float minY,
                out float maxX,
                out float maxY);

            bool sameX = XRMath.Approx(maxX, minX);
            bool sameY = XRMath.Approx(maxY, minY);

            size = Vector2.Zero;
            if (sameX)
            {
                //If the min and max anchors are the same, use the width of the component.
                size.X = GetWidth();
            }
            else
            {
                //Otherwise, calculate the size based on the anchors.
                //Translation is used as the translation from the min anchor, and width is used as the translation from the max anchor.
                size.X = (maxX + (Width ?? 0)) - (minX + Translation.X);
            }
            if (sameY)
            {
                //If the min and max anchors are the same, use the height of the component.
                size.Y = GetHeight();
            }
            else
            {
                //Otherwise, calculate the size based on the anchors.
                //Translation is used as the translation from the min anchor, and height is used as the translation from the max anchor.
                size.Y = (maxY + (Height ?? 0)) - (minY + Translation.Y);
            }

            //Clamp the size to the min and max size.
            ClampSize(ref size);

            //Adjust the translation based on the pivot.
            minX -= NormalizedPivot.X * size.X;
            minY -= NormalizedPivot.Y * size.Y;

            //If the min and max anchors are the same, add the translation to the min anchor position.
            if (sameX)
                minX += Translation.X;
            if (sameY)
                minY += Translation.Y;

            bottomLeftTranslation = new(minX + Margins.X, minY + Margins.Y);
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
            lock (Children)
            {
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
            }
        }

        /// <summary>
        /// Calculates the height of the component based the heights of its children.
        /// </summary>
        /// <returns></returns>
        public override float GetMaxChildHeight()
        {
            lock (Children)
            {
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
            }
        }

        private void ClampSize(ref Vector2 size)
        {
            if (MinWidth.HasValue)
                size.X = Math.Max(size.X, MinWidth.Value);
            if (MinHeight.HasValue)
                size.Y = Math.Max(size.Y, MinHeight.Value);
            if (MaxWidth.HasValue)
                size.X = Math.Min(size.X, MaxWidth.Value);
            if (MaxHeight.HasValue)
                size.Y = Math.Min(size.Y, MaxHeight.Value);
        }

        private void GetAnchors(float parentWidth, float parentHeight, out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = parentWidth * MinAnchor.X;
            maxX = parentWidth * MaxAnchor.X;
            minY = parentHeight * MinAnchor.Y;
            maxY = parentHeight * MaxAnchor.Y;
        }

        public BoundingRectangleF GetActualBounds()
            => new(_actualLocalBottomLeftTranslation, _actualSize);

        /// <summary>
        /// This method is called to fit the contents of this transform into the provided bounds.
        /// </summary>
        /// <param name="parentBounds"></param>
        public override void FitLayout(BoundingRectangleF parentBounds)
        {
            OnResizeActual(ApplyMargins(parentBounds));
            if (ShouldMarkLocalMatrixChanged())
                MarkLocalModified();
        }

        protected override void OnLocalMatrixChanged()
        {
            base.OnLocalMatrixChanged();
            OnResizeChildComponents(ApplyPadding(GetActualBounds()));
        }

        protected override void OnWorldMatrixChanged()
        {
            RemakeAxisAlignedRegion(ActualSize);
            base.OnWorldMatrixChanged();
        }

        private BoundingRectangleF ApplyPadding(BoundingRectangleF bounds)
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

        private BoundingRectangleF ApplyMargins(BoundingRectangleF bounds)
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

            if (!Engine.Rendering.Settings.RenderMesh2DBounds || Engine.Rendering.State.IsShadowPass)
                return;
            
            var region = AxisAlignedRegion;
            ColorF4 color = Engine.Rendering.Settings.Bounds2DColor;

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

        protected virtual void RemakeAxisAlignedRegion(Vector2 actualSize)
        {
            Matrix4x4 mtx = Matrix4x4.CreateScale(actualSize.X, actualSize.Y, 1.0f) * WorldMatrix;

            RegionWorldTransform = mtx;

            Vector3 minPos = Vector3.Transform(Vector3.Zero, mtx);
            Vector3 maxPos = Vector3.Transform(new Vector3(Vector2.One, 0.0f), mtx);

            // Make sure min is the smallest and max is the largest in case of rotation.
            Vector2 min = new(Math.Min(minPos.X, maxPos.X), Math.Min(minPos.Y, maxPos.Y));
            Vector2 max = new(Math.Max(minPos.X, maxPos.X), Math.Max(minPos.Y, maxPos.Y));

            DebugRenderInfo2D.CullingVolume = AxisAlignedRegion = BoundingRectangleF.FromMinMaxSides(min.X, max.X, min.Y, max.Y, 0.0f, 0.0f);
            
            //Engine.PrintLine($"Axis-aligned region remade: {_axisAlignedRegion.Translation} {_axisAlignedRegion.Extents}");
        }
        public UITransform? FindDeepestComponent(Vector2 worldPoint, bool includeThis)
        {
            try
            {
                lock (Children)
                {
                    foreach (var c in Children)
                    {
                        if (c is not UIBoundableTransform uiComp)
                            continue;

                        UITransform? comp = uiComp.FindDeepestComponent(worldPoint, true);
                        if (comp != null)
                            return comp;
                    }
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

            if (includeThis && Contains(worldPoint))
                return this;

            return null;
        }
        public List<UIBoundableTransform> FindAllIntersecting(Vector2 worldPoint, bool includeThis)
        {
            List<UIBoundableTransform> list = [];
            FindAllIntersecting(worldPoint, includeThis, list);
            return list;
        }
        public void FindAllIntersecting(Vector2 worldPoint, bool includeThis, List<UIBoundableTransform> results)
        {
            try
            {
                lock (Children)
                {
                    foreach (var c in Children)
                        if (c is UIBoundableTransform uiTfm)
                            uiTfm.FindAllIntersecting(worldPoint, true, results);
                }
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
                        renderInfo3D.LocalCullingVolume = AABB.FromSize(new Vector3(h, w, 0.1f));
                        break;
                }
            }
        }
    }
}
