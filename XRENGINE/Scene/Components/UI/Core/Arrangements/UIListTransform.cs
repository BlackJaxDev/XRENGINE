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
            UpperVirtualBound = upper;
            LowerVirtualBound = lower;
        }
        public void SetVirtualBoundsRelativeToTop(float size)
        {
            LowerVirtualBound = UpperVirtualBound - size;
        }
        public void SetVirtualBoundsRelativeToBottom(float size)
        {
            UpperVirtualBound = LowerVirtualBound + size;
        }

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
            float x = 0;
            float y = 0;
            if (!DisplayHorizontal)
                y += parentRegion.Height;
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
                    if (bc.SceneNode is not null)
                        bc.SceneNode.IsActiveSelf = false;
                }
                else
                {
                    bc.Visibility = EVisibility.Visible;
                    if (bc.SceneNode is not null)
                        bc.SceneNode.IsActiveSelf = true;

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
                    if (bc.SceneNode is not null)
                        bc.SceneNode.IsActiveSelf = false;
                }
                else
                {
                    bc.Visibility = EVisibility.Visible;
                    if (bc.SceneNode is not null)
                        bc.SceneNode.IsActiveSelf = true;

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