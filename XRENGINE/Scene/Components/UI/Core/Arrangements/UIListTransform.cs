using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    public partial class UIListTransform : UIBoundableTransform
    {
        private float? _childSize = null;
        private bool _horizontal = false;
        private float _spacing = 0.0f;
        private EListAlignment _alignment = EListAlignment.TopOrLeft;
        private bool _virtual = false;
        private float _upperVirtualBound = 0.0f;
        private float _lowerVirtualBound = 0.0f;
        private float _contentScrollOffset = 0.0f;

        /// <summary>
        /// The width or height of each child component.
        /// If null, the size of each child is determined by the child's own size.
        /// </summary>
        public float? ItemSize
        {
            get => _childSize;
            set => SetField(ref _childSize, value);
        }
        /// <summary>
        /// If the the list should display left to right instead of top to bottom.
        /// </summary>
        public bool DisplayHorizontal
        {
            get => _horizontal;
            set => SetField(ref _horizontal, value);
        }
        /// <summary>
        /// The distance between each child component.
        /// </summary>
        public float ItemSpacing
        {
            get => _spacing;
            set => SetField(ref _spacing, value);
        }
        /// <summary>
        /// The alignment of the child components.
        /// </summary>
        public EListAlignment ItemAlignment
        {
            get => _alignment;
            set => SetField(ref _alignment, value);
        }
        /// <summary>
        /// If true, items will be pooled and culled if they are outside of the parent region.
        /// </summary>
        public bool Virtual
        {
            get => _virtual;
            set => SetField(ref _virtual, value);
        }
        /// <summary>
        /// The upper bound of the virtual region.
        /// </summary>
        public float UpperVirtualBound
        {
            get => _upperVirtualBound;
            set => SetField(ref _upperVirtualBound, value);
        }
        /// <summary>
        /// The lower bound of the virtual region.
        /// </summary>
        public float LowerVirtualBound
        {
            get => _lowerVirtualBound;
            set => SetField(ref _lowerVirtualBound, value);
        }
        public float VirtualRegionSize => UpperVirtualBound - LowerVirtualBound;
        public void SetVirtualBounds(float upper, float lower)
        {
            if (_upperVirtualBound == upper && _lowerVirtualBound == lower)
                return;
            _upperVirtualBound = upper;
            _lowerVirtualBound = lower;
            InvalidateArrange();
        }

        /// <summary>
        /// Scroll offset applied to content layout.
        /// For vertical lists, positive values scroll down (reveal later items).
        /// For horizontal lists, positive values scroll right.
        /// </summary>
        public float ContentScrollOffset
        {
            get => _contentScrollOffset;
            set => SetField(ref _contentScrollOffset, value);
        }
        public void SetVirtualBoundsRelativeToTop(float size)
        {
            LowerVirtualBound = UpperVirtualBound - size;
        }
        public void SetVirtualBoundsRelativeToBottom(float size)
        {
            UpperVirtualBound = LowerVirtualBound + size;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(ItemSize):
                case nameof(DisplayHorizontal):
                case nameof(ItemSpacing):
                case nameof(ItemAlignment):
                    InvalidateMeasure();
                    break;
                case nameof(Virtual):
                case nameof(UpperVirtualBound):
                case nameof(LowerVirtualBound):
                case nameof(ContentScrollOffset):
                    InvalidateArrange();
                    break;
            }
        }

        #region Measure/Arrange Overrides for List Layout

        /// <summary>
        /// Measures children in list configuration and calculates total required size.
        /// </summary>
        protected override float MeasureChildrenWidth(Vector2 availableSize)
        {
            if (!_horizontal)
                return base.MeasureChildrenWidth(availableSize);

            // For horizontal lists, sum up all child widths
            float totalWidth = 0.0f;
            int visibleCount = 0;

            foreach (var child in Children)
            {
                if (child is not UIBoundableTransform bc || bc.IsCollapsed || bc.ExcludeFromParentAutoCalcWidth)
                    continue;

                // Measure the child first if needed
                if (bc.NeedsMeasure)
                    bc.Measure(availableSize);

                float childWidth = ItemSize ?? bc.DesiredSize.X;
                totalWidth += childWidth;
                visibleCount++;
            }

            // Add spacing between items
            if (visibleCount > 1)
                totalWidth += ItemSpacing * (visibleCount - 1);

            return totalWidth;
        }

        /// <summary>
        /// Measures children in list configuration and calculates total required size.
        /// </summary>
        protected override float MeasureChildrenHeight(Vector2 availableSize)
        {
            if (_horizontal)
                return base.MeasureChildrenHeight(availableSize);

            // For vertical lists, sum up all child heights
            float totalHeight = 0.0f;
            int visibleCount = 0;

            foreach (var child in Children)
            {
                if (child is not UIBoundableTransform bc || bc.IsCollapsed || bc.ExcludeFromParentAutoCalcHeight)
                    continue;

                // Measure the child first if needed
                if (bc.NeedsMeasure)
                    bc.Measure(availableSize);

                float childHeight = ItemSize ?? bc.DesiredSize.Y;
                totalHeight += childHeight;
                visibleCount++;
            }

            // Add spacing between items
            if (visibleCount > 1)
                totalHeight += ItemSpacing * (visibleCount - 1);

            return totalHeight;
        }

        /// <summary>
        /// Arranges children in list configuration with proper positioning.
        /// </summary>
        protected override void ArrangeChildren(BoundingRectangleF childRegion)
        {
            switch (ItemAlignment)
            {
                case EListAlignment.TopOrLeft:
                    ArrangeChildrenLeftTop(childRegion);
                    break;
                case EListAlignment.Centered:
                    ArrangeChildrenCentered(childRegion);
                    break;
                case EListAlignment.BottomOrRight:
                    ArrangeChildrenRightBottom(childRegion);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ArrangeChildrenLeftTop(BoundingRectangleF parentRegion)
        {
            float x = _horizontal ? -_contentScrollOffset : 0;
            float y = _horizontal ? 0 : parentRegion.Height + _contentScrollOffset;

            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i] is not UIBoundableTransform bc || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (_horizontal)
                {
                    float size = ItemSize ?? bc.DesiredSize.X;
                    placementInfo.BottomOrLeftOffset = x;
                    ArrangeChildHorizontal(bc, x, y, size, parentRegion.Height);
                    x += size;
                    if (i < Children.Count - 1)
                        x += ItemSpacing;
                }
                else
                {
                    float size = ItemSize ?? bc.DesiredSize.Y;
                    y -= size;
                    placementInfo.BottomOrLeftOffset = y;
                    ArrangeChildVertical(bc, x, y, size, parentRegion.Width);
                    if (i < Children.Count - 1)
                        y -= ItemSpacing;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ArrangeChildrenCentered(BoundingRectangleF parentRegion)
        {
            float x = 0;
            float y = 0;
            float totalSize = CalculateTotalChildSize();

            if (_horizontal)
                x = (parentRegion.Width - totalSize) / 2.0f;
            else
                y = -(parentRegion.Height - totalSize) / 2.0f;

            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i] is not UIBoundableTransform bc || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (_horizontal)
                {
                    float size = ItemSize ?? bc.DesiredSize.X;
                    placementInfo.BottomOrLeftOffset = x;
                    ArrangeChildHorizontal(bc, x, y, size, parentRegion.Height);
                    x += size;
                    if (i < Children.Count - 1)
                        x += ItemSpacing;
                }
                else
                {
                    float size = ItemSize ?? bc.DesiredSize.Y;
                    y -= size;
                    placementInfo.BottomOrLeftOffset = y;
                    ArrangeChildVertical(bc, x, y, size, parentRegion.Width);
                    if (i < Children.Count - 1)
                        y -= ItemSpacing;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ArrangeChildrenRightBottom(BoundingRectangleF parentRegion)
        {
            float x = 0;
            float y = 0;

            for (int i = Children.Count - 1; i >= 0; i--)
            {
                if (Children[i] is not UIBoundableTransform bc || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (_horizontal)
                {
                    float size = ItemSize ?? bc.DesiredSize.X;
                    x -= size;
                    placementInfo.BottomOrLeftOffset = x;
                    ArrangeChildHorizontal(bc, x, y, size, parentRegion.Height);
                    if (i > 0)
                        x -= ItemSpacing;
                }
                else
                {
                    float size = ItemSize ?? bc.DesiredSize.Y;
                    placementInfo.BottomOrLeftOffset = y;
                    ArrangeChildVertical(bc, x, y, size, parentRegion.Width);
                    y += size;
                    if (i > 0)
                        y += ItemSpacing;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float CalculateTotalChildSize()
        {
            float totalSize = 0.0f;
            int count = 0;

            foreach (var child in Children)
            {
                if (child is not UIBoundableTransform bc || bc.IsCollapsed)
                    continue;

                totalSize += ItemSize ?? (_horizontal ? bc.DesiredSize.X : bc.DesiredSize.Y);
                count++;
            }

            if (count > 1)
                totalSize += ItemSpacing * (count - 1);

            return totalSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ArrangeChildHorizontal(UIBoundableTransform bc, float x, float y, float size, float parentHeight)
        {
            if (Virtual && (x + size < LowerVirtualBound || x > UpperVirtualBound))
            {
                bc.Visibility = EVisibility.Hidden;
            }
            else
            {
                if (Virtual)
                    bc.Visibility = EVisibility.Visible;
                bc.Arrange(new BoundingRectangleF(x, y, size, parentHeight));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ArrangeChildVertical(UIBoundableTransform bc, float x, float y, float size, float parentWidth)
        {
            if (Virtual && (y + size < LowerVirtualBound || y > UpperVirtualBound))
            {
                bc.Visibility = EVisibility.Hidden;
            }
            else
            {
                if (Virtual)
                    bc.Visibility = EVisibility.Visible;
                bc.Arrange(new BoundingRectangleF(x, y, parentWidth, size));
            }
        }

        #endregion

        protected override void OnResizeChildComponents(BoundingRectangleF parentRegion)
        {
            //TODO: clip children that are outside of the parent region
            //lock (Children)
            {
                switch (ItemAlignment)
                {
                    case EListAlignment.TopOrLeft:
                        SizeChildrenLeftTop(parentRegion);
                        break;
                    case EListAlignment.Centered:
                        SizeChildrenCentered(parentRegion);
                        break;
                    case EListAlignment.BottomOrRight:
                        SizeChildrenRightBottom(parentRegion);
                        break;
                }
            }
        }

        private void SizeChildrenRightBottom(BoundingRectangleF parentRegion)
        {
            float x = 0;
            float y = 0;
            //TODO: verify this was implemented correctly
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                TransformBase? child = Children[i];
                if (child is not UIBoundableTransform bc || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (DisplayHorizontal)
                {
                    float parentHeight = parentRegion.Height;
                    float size = ItemSize ?? bc.ActualWidth;
                    x -= size;
                    placementInfo.BottomOrLeftOffset = x;

                    bc.FitLayout(new BoundingRectangleF(x, y, size, parentHeight));

                    if (i > 0)
                        x -= ItemSpacing;
                }
                else
                {
                    float parentWidth = parentRegion.Width;
                    float size = ItemSize ?? bc.ActualHeight;
                    placementInfo.BottomOrLeftOffset = y;

                    bc.FitLayout(new BoundingRectangleF(x, y, parentWidth, size));

                    y += size;
                    if (i > 0)
                        y += ItemSpacing;
                }
            }
        }

        private void SizeChildrenCentered(BoundingRectangleF parentRegion)
        {
            float x = 0;
            float y = 0;
            float[] sizes = new float[Children.Count];
            float totalSize = CalcTotalSize(sizes);

            if (DisplayHorizontal)
                x += (parentRegion.Width - totalSize) / 2.0f;
            else
                y -= (parentRegion.Height - totalSize) / 2.0f;

            for (int i = 0; i < Children.Count; i++)
            {
                TransformBase? child = Children[i];
                if (child is not UIBoundableTransform bc || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (DisplayHorizontal)
                {
                    float parentHeight = parentRegion.Height;
                    float size = sizes[i];
                    placementInfo.BottomOrLeftOffset = x;

                    FitLayoutHorizontal(y, x, bc, parentHeight, size);
                    Increment(ref x, i, size);
                }
                else
                {
                    float parentWidth = parentRegion.Width;
                    float size = sizes[i];
                    y -= size;
                    placementInfo.BottomOrLeftOffset = y;

                    FitLayoutVertical(y, x, bc, parentWidth, size);
                    if (i < Children.Count - 1)
                        y -= ItemSpacing;
                }
            }
        }

        private float CalcTotalSize(float[] sizes)
        {
            float totalSize = 0.0f;
            for (int i = 0; i < Children.Count; i++)
            {
                TransformBase? child = Children[i];
                if (child is not UIBoundableTransform bc)
                    continue;

                float size = ItemSize ?? (DisplayHorizontal ? bc.ActualWidth : bc.ActualHeight);

                sizes[i] = size;
                totalSize += size;
                if (i < Children.Count - 1)
                    totalSize += ItemSpacing;
            }

            return totalSize;
        }

        private void SizeChildrenLeftTop(BoundingRectangleF parentRegion)
        {
            float x = DisplayHorizontal ? -_contentScrollOffset : 0;
            float y = 0;
            if (!DisplayHorizontal)
                y += parentRegion.Height + _contentScrollOffset;
            for (int i = 0; i < Children.Count; i++)
            {
                TransformBase? child = Children[i];
                if (child is not UIBoundableTransform bc || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (DisplayHorizontal)
                {
                    float parentHeight = parentRegion.Height;
                    float size = ItemSize ?? bc.ActualWidth;
                    placementInfo.BottomOrLeftOffset = x;

                    FitLayoutHorizontal(y, x, bc, parentHeight, size);
                    Increment(ref x, i, size);
                }
                else
                {
                    float parentWidth = parentRegion.Width;
                    float size = ItemSize ?? bc.ActualHeight;
                    y -= size;
                    placementInfo.BottomOrLeftOffset = y;

                    FitLayoutVertical(y, x, bc, parentWidth, size);
                    if (i < Children.Count - 1)
                        y -= ItemSpacing;
                }
            }
        }

        private void FitLayoutHorizontal(float y, float x, UIBoundableTransform bc, float parentHeight, float size)
        {
            if (Virtual)
            {
                if (x + size < LowerVirtualBound || x > UpperVirtualBound)
                {
                    bc.Visibility = EVisibility.Hidden;
                }
                else
                {
                    bc.Visibility = EVisibility.Visible;
                    bc.FitLayout(new BoundingRectangleF(x, y, size, parentHeight));
                }
            }
            else
            {
                bc.FitLayout(new BoundingRectangleF(x, y, size, parentHeight));
            }
        }

        private void FitLayoutVertical(float y, float x, UIBoundableTransform bc, float parentWidth, float size)
        {
            if (Virtual)
            {
                if (y + size < LowerVirtualBound || y > UpperVirtualBound)
                {
                    bc.Visibility = EVisibility.Hidden;
                }
                else
                {
                    bc.Visibility = EVisibility.Visible;
                    bc.FitLayout(new BoundingRectangleF(x, y, parentWidth, size));
                }
            }
            else
            {
                bc.FitLayout(new BoundingRectangleF(x, y, parentWidth, size));
            }
        }

        private void Increment(ref float value, int i, float size)
        {
            value += size;
            if (i < Children.Count - 1)
                value += ItemSpacing;
        }

        public override void VerifyPlacementInfo(UITransform childTransform, ref UIChildPlacementInfo? placementInfo)
        {
            if (placementInfo is not UIListChildPlacementInfo)
                placementInfo = new UIListChildPlacementInfo(childTransform);
        }

        public override float GetMaxChildHeight()
        {
            if (_horizontal)
                return base.GetMaxChildHeight();

            //add up all the heights of the children
            float totalHeight = 0.0f;
            //lock (Children)
            //{
                for (int i = 0; i < Children.Count; i++)
                {
                    totalHeight += ItemSize ?? (Children[i] is UIBoundableTransform bc && !bc.IsCollapsed && !bc.ExcludeFromParentAutoCalcHeight ? bc.GetHeight() : 0.0f);
                    if (i < Children.Count - 1)
                        totalHeight += ItemSpacing;
                }
            //}
            return totalHeight;
        }
        public override float GetMaxChildWidth()
        {
            if (!_horizontal)
                return base.GetMaxChildWidth();

            //add up all the widths of the children
            float totalWidth = 0.0f;
            //lock (Children)
            //{
                for (int i = 0; i < Children.Count; i++)
                {
                    totalWidth += ItemSize ?? (Children[i] is UIBoundableTransform bc && !bc.IsCollapsed && !bc.ExcludeFromParentAutoCalcWidth ? bc.GetWidth() : 0.0f);
                    if (i < Children.Count - 1)
                        totalWidth += ItemSpacing;
                }
            //}
            return totalWidth;
        }
    }
}