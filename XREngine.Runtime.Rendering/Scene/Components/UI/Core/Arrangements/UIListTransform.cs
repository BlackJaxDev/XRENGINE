using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    [XRTransformEditor("XREngine.Editor.TransformEditors.UIListTransformEditor")]
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
            set
            {
                checked { _virtualBoundsIntentRevision++; }
                SetField(ref _upperVirtualBound, value);
            }
        }
        /// <summary>
        /// The lower bound of the virtual region.
        /// </summary>
        public float LowerVirtualBound
        {
            get => _lowerVirtualBound;
            set
            {
                checked { _virtualBoundsIntentRevision++; }
                SetField(ref _lowerVirtualBound, value);
            }
        }
        public float VirtualRegionSize => UpperVirtualBound - LowerVirtualBound;
        public void SetVirtualBounds(float upper, float lower)
        {
            if (_upperVirtualBound == upper && _lowerVirtualBound == lower)
                return;
            GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
            UpperVirtualBound = upper;
            LowerVirtualBound = lower;
        }

        /// <summary>
        /// Scroll offset applied to content layout.
        /// For vertical lists, positive values scroll down (reveal later items).
        /// For horizontal lists, positive values scroll right.
        /// </summary>
        public float ContentScrollOffset
        {
            get => _contentScrollOffset;
            set => SetContentScrollOffsetCore(value, 0, static _ => { }, false);
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
            if (_virtualProvider is not null && _horizontal)
                return VirtualContentExtent;
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
            if (_virtualProvider is not null && !_horizontal)
                return VirtualContentExtent;
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
            if (_virtualProvider is not null)
            {
                ArrangeVirtualRows(childRegion);
                return;
            }
            BoundingRectangleF paddedRegion = ApplyPadding(childRegion);
            switch (ItemAlignment)
            {
                case EListAlignment.TopOrLeft:
                    ArrangeChildrenLeftTop(paddedRegion);
                    break;
                case EListAlignment.Centered:
                    ArrangeChildrenCentered(paddedRegion);
                    break;
                case EListAlignment.BottomOrRight:
                    ArrangeChildrenRightBottom(paddedRegion);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ArrangeChildrenLeftTop(BoundingRectangleF parentRegion)
        {
            float padLeft = Padding.X;
            float padBottom = Padding.Y;
            float x = _horizontal ? padLeft - _contentScrollOffset : padLeft;
            float y = _horizontal ? padBottom : padBottom + parentRegion.Height + _contentScrollOffset;
            bool placedAny = false;

            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i] is not UIBoundableTransform bc || bc.IsCollapsed || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (_horizontal)
                {
                    if (placedAny)
                        x += ItemSpacing;
                    float size = ItemSize ?? bc.DesiredSize.X;
                    placementInfo.BottomOrLeftOffset = x;
                    placementInfo.CrossAxisOffset = padBottom;
                    ArrangeChildHorizontal(bc, x, y, size, parentRegion.Height);
                    x += size;
                }
                else
                {
                    if (placedAny)
                        y -= ItemSpacing;
                    float size = ItemSize ?? bc.DesiredSize.Y;
                    y -= size;
                    placementInfo.BottomOrLeftOffset = y;
                    placementInfo.CrossAxisOffset = padLeft;
                    ArrangeChildVertical(bc, x, y, size, parentRegion.Width);
                }

                placedAny = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ArrangeChildrenCentered(BoundingRectangleF parentRegion)
        {
            float padLeft = Padding.X;
            float padBottom = Padding.Y;
            float x = padLeft;
            float y = padBottom;
            float totalSize = CalculateTotalChildSize();

            if (_horizontal)
                x = padLeft + (parentRegion.Width - totalSize) * 0.5f;
            else
                y = padBottom + (parentRegion.Height + totalSize) * 0.5f;

            bool placedAny = false;

            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i] is not UIBoundableTransform bc || bc.IsCollapsed || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (_horizontal)
                {
                    if (placedAny)
                        x += ItemSpacing;
                    float size = ItemSize ?? bc.DesiredSize.X;
                    placementInfo.BottomOrLeftOffset = x;
                    placementInfo.CrossAxisOffset = padBottom;
                    ArrangeChildHorizontal(bc, x, y, size, parentRegion.Height);
                    x += size;
                }
                else
                {
                    if (placedAny)
                        y -= ItemSpacing;
                    float size = ItemSize ?? bc.DesiredSize.Y;
                    y -= size;
                    placementInfo.BottomOrLeftOffset = y;
                    placementInfo.CrossAxisOffset = padLeft;
                    ArrangeChildVertical(bc, x, y, size, parentRegion.Width);
                }

                placedAny = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ArrangeChildrenRightBottom(BoundingRectangleF parentRegion)
        {
            float padLeft = Padding.X;
            float padBottom = Padding.Y;
            float x = _horizontal ? padLeft + parentRegion.Width : padLeft;
            float y = padBottom;
            bool placedAny = false;

            for (int i = Children.Count - 1; i >= 0; i--)
            {
                if (Children[i] is not UIBoundableTransform bc || bc.IsCollapsed || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (_horizontal)
                {
                    if (placedAny)
                        x -= ItemSpacing;
                    float size = ItemSize ?? bc.DesiredSize.X;
                    x -= size;
                    placementInfo.BottomOrLeftOffset = x;
                    placementInfo.CrossAxisOffset = padBottom;
                    ArrangeChildHorizontal(bc, x, y, size, parentRegion.Height);
                }
                else
                {
                    if (placedAny)
                        y += ItemSpacing;
                    float size = ItemSize ?? bc.DesiredSize.Y;
                    placementInfo.BottomOrLeftOffset = y;
                    placementInfo.CrossAxisOffset = padLeft;
                    ArrangeChildVertical(bc, x, y, size, parentRegion.Width);
                    y += size;
                }

                placedAny = true;
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


        private void SizeChildrenRightBottom(BoundingRectangleF parentRegion)
        {
            float padLeft = Padding.X;
            float padBottom = Padding.Y;
            float x = DisplayHorizontal ? padLeft + parentRegion.Width : padLeft;
            float y = padBottom;
            bool placedAny = false;
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                TransformBase? child = Children[i];
                if (child is not UIBoundableTransform bc || bc.IsCollapsed || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (DisplayHorizontal)
                {
                    if (placedAny)
                        x -= ItemSpacing;
                    float parentHeight = parentRegion.Height;
                    float size = ItemSize ?? bc.ActualWidth;
                    x -= size;
                    placementInfo.BottomOrLeftOffset = x;
                    placementInfo.CrossAxisOffset = padBottom;

                    bc.FitLayout(new BoundingRectangleF(x, y, size, parentHeight));
                }
                else
                {
                    if (placedAny)
                        y += ItemSpacing;
                    float parentWidth = parentRegion.Width;
                    float size = ItemSize ?? bc.ActualHeight;
                    placementInfo.BottomOrLeftOffset = y;
                    placementInfo.CrossAxisOffset = padLeft;

                    bc.FitLayout(new BoundingRectangleF(x, y, parentWidth, size));

                    y += size;
                }

                placedAny = true;
            }
        }

        private void SizeChildrenCentered(BoundingRectangleF parentRegion)
        {
            float padLeft = Padding.X;
            float padBottom = Padding.Y;
            float x = padLeft;
            float y = padBottom;
            float[] sizes = new float[Children.Count];
            float totalSize = CalcTotalSize(sizes);

            if (DisplayHorizontal)
                x = padLeft + (parentRegion.Width - totalSize) * 0.5f;
            else
                y = padBottom + (parentRegion.Height + totalSize) * 0.5f;

            bool placedAny = false;

            for (int i = 0; i < Children.Count; i++)
            {
                TransformBase? child = Children[i];
                if (child is not UIBoundableTransform bc || bc.IsCollapsed || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (DisplayHorizontal)
                {
                    if (placedAny)
                        x += ItemSpacing;
                    float parentHeight = parentRegion.Height;
                    float size = sizes[i];
                    placementInfo.BottomOrLeftOffset = x;
                    placementInfo.CrossAxisOffset = padBottom;

                    FitLayoutHorizontal(y, x, bc, parentHeight, size);
                    x += size;
                }
                else
                {
                    if (placedAny)
                        y -= ItemSpacing;
                    float parentWidth = parentRegion.Width;
                    float size = sizes[i];
                    y -= size;
                    placementInfo.BottomOrLeftOffset = y;
                    placementInfo.CrossAxisOffset = padLeft;

                    FitLayoutVertical(y, x, bc, parentWidth, size);
                }

                placedAny = true;
            }
        }

        private float CalcTotalSize(float[] sizes)
        {
            float totalSize = 0.0f;
            int visibleCount = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                TransformBase? child = Children[i];
                if (child is not UIBoundableTransform bc || bc.IsCollapsed)
                    continue;

                float size = ItemSize ?? (DisplayHorizontal ? bc.ActualWidth : bc.ActualHeight);

                sizes[i] = size;
                totalSize += size;
                visibleCount++;
            }

            if (visibleCount > 1)
                totalSize += ItemSpacing * (visibleCount - 1);

            return totalSize;
        }

        private void SizeChildrenLeftTop(BoundingRectangleF parentRegion)
        {
            float padLeft = Padding.X;
            float padBottom = Padding.Y;
            float x = DisplayHorizontal ? padLeft - _contentScrollOffset : padLeft;
            float y = DisplayHorizontal ? padBottom : padBottom + parentRegion.Height + _contentScrollOffset;
            bool placedAny = false;
            for (int i = 0; i < Children.Count; i++)
            {
                TransformBase? child = Children[i];
                if (child is not UIBoundableTransform bc || bc.IsCollapsed || bc.PlacementInfo is not UIListChildPlacementInfo placementInfo)
                    continue;

                if (DisplayHorizontal)
                {
                    if (placedAny)
                        x += ItemSpacing;
                    float parentHeight = parentRegion.Height;
                    float size = ItemSize ?? bc.ActualWidth;
                    placementInfo.BottomOrLeftOffset = x;
                    placementInfo.CrossAxisOffset = padBottom;

                    FitLayoutHorizontal(y, x, bc, parentHeight, size);
                    x += size;
                }
                else
                {
                    if (placedAny)
                        y -= ItemSpacing;
                    float parentWidth = parentRegion.Width;
                    float size = ItemSize ?? bc.ActualHeight;
                    y -= size;
                    placementInfo.BottomOrLeftOffset = y;
                    placementInfo.CrossAxisOffset = padLeft;

                    FitLayoutVertical(y, x, bc, parentWidth, size);
                }

                placedAny = true;
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

        public override void VerifyPlacementInfo(TransformBase childTransform, ref ITransformChildPlacementInfo? placementInfo)
        {
            if (childTransform is UITransform uiChildTransform && placementInfo is not UIListChildPlacementInfo)
                placementInfo = new UIListChildPlacementInfo(uiChildTransform);
        }

        public override float GetMaxChildHeight()
        {
            if (_virtualProvider is not null && !_horizontal)
                return VirtualContentExtent;
            if (_horizontal)
                return base.GetMaxChildHeight();

            float totalHeight = 0.0f;
            int visibleCount = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i] is not UIBoundableTransform bc || bc.IsCollapsed || bc.ExcludeFromParentAutoCalcHeight)
                    continue;

                totalHeight += ItemSize ?? bc.GetHeight();
                visibleCount++;
            }

            if (visibleCount > 1)
                totalHeight += ItemSpacing * (visibleCount - 1);

            return totalHeight;
        }
        public override float GetMaxChildWidth()
        {
            if (_virtualProvider is not null && _horizontal)
                return VirtualContentExtent;
            if (!_horizontal)
                return base.GetMaxChildWidth();

            float totalWidth = 0.0f;
            int visibleCount = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i] is not UIBoundableTransform bc || bc.IsCollapsed || bc.ExcludeFromParentAutoCalcWidth)
                    continue;

                totalWidth += ItemSize ?? bc.GetWidth();
                visibleCount++;
            }

            if (visibleCount > 1)
                totalWidth += ItemSpacing * (visibleCount - 1);

            return totalWidth;
        }
    }
}
