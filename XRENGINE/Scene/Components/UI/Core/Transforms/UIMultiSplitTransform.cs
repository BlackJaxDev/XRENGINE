using Extensions;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// A transform that splits its children into regions based on the specified arrangement.
    /// </summary>
    public class UIMultiSplitTransform : UIBoundableTransform
    {
        private UISplitArrangement _arrangement = UISplitArrangement.LeftMiddleRight;
        public UISplitArrangement Arrangement
        {
            get => _arrangement;
            set => SetField(ref _arrangement, value);
        }

        private float? _fixedSizeFirst = null;
        public float? FixedSizeFirst
        {
            get => _fixedSizeFirst;
            set => SetField(ref _fixedSizeFirst, value);
        }

        private float? _fixedSizeSecond = null;
        public float? FixedSizeSecond
        {
            get => _fixedSizeSecond;
            set => SetField(ref _fixedSizeSecond, value);
        }

        private float _splitPercentFirst = 0.33f;
        public float SplitPercentFirst
        {
            get => _splitPercentFirst;
            set => SetField(ref _splitPercentFirst, value.Clamp(0.0f, 1.0f));
        }

        private float _splitPercentSecond = 0.66f;
        public float SplitPercentSecond
        {
            get => _splitPercentSecond;
            set => SetField(ref _splitPercentSecond, value.Clamp(0.0f, 1.0f));
        }

        private float _splitterSize = 0.0f;
        public float SplitterSize
        {
            get => _splitterSize;
            set => SetField(ref _splitterSize, value);
        }

        private bool _canUserResize = true;
        public bool CanUserResize
        {
            get => _canUserResize;
            set => SetField(ref _canUserResize, value);
        }

        protected override void OnResizeChildComponents(BoundingRectangleF parentRegion)
        {
            List<UIBoundableTransform> children = [.. Children.OfType<UIBoundableTransform>()];
            int childCount = children.Count;

            if (childCount == 0)
                return;

            switch (Arrangement)
            {
                case UISplitArrangement.LeftMiddleRight:
                    ResizeLeftMiddleRight(parentRegion, children);
                    break;
                case UISplitArrangement.TopMiddleBottom:
                    ResizeTopMiddleBottom(parentRegion, children);
                    break;
                case UISplitArrangement.LeftRight:
                    ResizeLeftRight(parentRegion, children);
                    break;
                case UISplitArrangement.TopBottom:
                    ResizeTopBottom(parentRegion, children);
                    break;
            }
        }

        private void ResizeLeftMiddleRight(BoundingRectangleF parentRegion, List<UIBoundableTransform> children)
        {
            float leftSize, middleSize, rightSize;
            int childCount = children.Count;

            if (childCount == 1)
            {
                children[0].FitLayout(parentRegion);
                return;
            }

            if (childCount == 2)
            {
                ResizeLeftRight(parentRegion, children);
                return;
            }

            if (FixedSizeFirst.HasValue && FixedSizeSecond.HasValue)
            {
                leftSize = FixedSizeFirst.Value;
                rightSize = FixedSizeSecond.Value;
                middleSize = Math.Max(0, parentRegion.Width - leftSize - rightSize - (2 * SplitterSize));
            }
            else if (FixedSizeFirst.HasValue)
            {
                leftSize = FixedSizeFirst.Value;
                middleSize = (parentRegion.Width - leftSize - SplitterSize) * SplitPercentFirst;
                rightSize = parentRegion.Width - leftSize - middleSize - (2 * SplitterSize);
            }
            else if (FixedSizeSecond.HasValue)
            {
                rightSize = FixedSizeSecond.Value;
                middleSize = (parentRegion.Width - rightSize - SplitterSize) * SplitPercentFirst;
                leftSize = parentRegion.Width - rightSize - middleSize - (2 * SplitterSize);
            }
            else
            {
                leftSize = parentRegion.Width * SplitPercentFirst;
                middleSize = (parentRegion.Width - leftSize - SplitterSize) * (SplitPercentSecond - SplitPercentFirst);
                rightSize = parentRegion.Width - leftSize - middleSize - (2 * SplitterSize);
            }

            if (children[0].PlacementInfo is UISplitChildPlacementInfo aInfo)
                aInfo.Offset = 0;
            children[0].FitLayout(new(parentRegion.X, parentRegion.Y, leftSize, parentRegion.Height));

            if (children[1].PlacementInfo is UISplitChildPlacementInfo bInfo)
                bInfo.Offset = leftSize + SplitterSize;
            children[1].FitLayout(new(parentRegion.X + leftSize + SplitterSize, parentRegion.Y, middleSize, parentRegion.Height));

            if (children[2].PlacementInfo is UISplitChildPlacementInfo cInfo)
                cInfo.Offset = leftSize + middleSize + (2 * SplitterSize);
            children[2].FitLayout(new(parentRegion.X + leftSize + middleSize + (2 * SplitterSize), parentRegion.Y, rightSize, parentRegion.Height));
        }

        private void ResizeTopMiddleBottom(BoundingRectangleF parentRegion, List<UIBoundableTransform> children)
        {
            float topSize, middleSize, bottomSize;
            int childCount = children.Count;

            if (childCount == 1)
            {
                children[0].FitLayout(parentRegion);
                return;
            }

            if (childCount == 2)
            {
                ResizeTopBottom(parentRegion, children);
                return;
            }

            if (FixedSizeFirst.HasValue && FixedSizeSecond.HasValue)
            {
                topSize = FixedSizeFirst.Value;
                bottomSize = FixedSizeSecond.Value;
                middleSize = Math.Max(0, parentRegion.Height - topSize - bottomSize - (2 * SplitterSize));
            }
            else if (FixedSizeFirst.HasValue)
            {
                topSize = FixedSizeFirst.Value;
                middleSize = (parentRegion.Height - topSize - SplitterSize) * SplitPercentFirst;
                bottomSize = parentRegion.Height - topSize - middleSize - (2 * SplitterSize);
            }
            else if (FixedSizeSecond.HasValue)
            {
                bottomSize = FixedSizeSecond.Value;
                middleSize = (parentRegion.Height - bottomSize - SplitterSize) * SplitPercentFirst;
                topSize = parentRegion.Height - bottomSize - middleSize - (2 * SplitterSize);
            }
            else
            {
                topSize = parentRegion.Height * SplitPercentFirst;
                middleSize = (parentRegion.Height - topSize - SplitterSize) * (SplitPercentSecond - SplitPercentFirst);
                bottomSize = parentRegion.Height - topSize - middleSize - (2 * SplitterSize);
            }

            if (children[0].PlacementInfo is UISplitChildPlacementInfo aInfo)
                aInfo.Offset = bottomSize + middleSize + (2 * SplitterSize);
            children[0].FitLayout(new(parentRegion.X, parentRegion.Y + bottomSize + middleSize + (2 * SplitterSize), parentRegion.Width, topSize));

            if (children[1].PlacementInfo is UISplitChildPlacementInfo bInfo)
                bInfo.Offset = bottomSize + SplitterSize;
            children[1].FitLayout(new(parentRegion.X, parentRegion.Y + bottomSize + SplitterSize, parentRegion.Width, middleSize));

            if (children[2].PlacementInfo is UISplitChildPlacementInfo cInfo)
                cInfo.Offset = 0;
            children[2].FitLayout(new(parentRegion.X, parentRegion.Y, parentRegion.Width, bottomSize));
        }

        private void ResizeLeftRight(BoundingRectangleF parentRegion, List<UIBoundableTransform> children)
        {
            if (children.Count < 2) return;

            float leftSize, rightSize;

            if (FixedSizeFirst.HasValue && FixedSizeSecond.HasValue)
            {
                leftSize = FixedSizeFirst.Value;
                rightSize = FixedSizeSecond.Value;
            }
            else if (FixedSizeFirst.HasValue)
            {
                leftSize = FixedSizeFirst.Value;
                rightSize = parentRegion.Width - FixedSizeFirst.Value - SplitterSize;
            }
            else if (FixedSizeSecond.HasValue)
            {
                rightSize = FixedSizeSecond.Value;
                leftSize = parentRegion.Width - FixedSizeSecond.Value - SplitterSize;
            }
            else
            {
                leftSize = parentRegion.Width * SplitPercentFirst;
                rightSize = parentRegion.Width - leftSize - SplitterSize;
            }

            if (children[0].PlacementInfo is UISplitChildPlacementInfo aInfo)
                aInfo.Offset = 0;
            children[0].FitLayout(new(parentRegion.X, parentRegion.Y, leftSize, parentRegion.Height));

            if (children[1].PlacementInfo is UISplitChildPlacementInfo bInfo)
                bInfo.Offset = leftSize + SplitterSize;
            children[1].FitLayout(new(parentRegion.X + leftSize + SplitterSize, parentRegion.Y, rightSize, parentRegion.Height));
        }

        private void ResizeTopBottom(BoundingRectangleF parentRegion, List<UIBoundableTransform> children)
        {
            if (children.Count < 2) return;

            float topSize, bottomSize;

            if (FixedSizeFirst.HasValue && FixedSizeSecond.HasValue)
            {
                topSize = FixedSizeFirst.Value;
                bottomSize = FixedSizeSecond.Value;
            }
            else if (FixedSizeFirst.HasValue)
            {
                topSize = FixedSizeFirst.Value;
                bottomSize = parentRegion.Height - FixedSizeFirst.Value - SplitterSize;
            }
            else if (FixedSizeSecond.HasValue)
            {
                bottomSize = FixedSizeSecond.Value;
                topSize = parentRegion.Height - FixedSizeSecond.Value - SplitterSize;
            }
            else
            {
                topSize = parentRegion.Height * SplitPercentFirst;
                bottomSize = parentRegion.Height - topSize - SplitterSize;
            }

            if (children[0].PlacementInfo is UISplitChildPlacementInfo aInfo)
                aInfo.Offset = bottomSize + SplitterSize;
            children[0].FitLayout(new(parentRegion.X, parentRegion.Y + bottomSize + SplitterSize, parentRegion.Width, topSize));

            if (children[1].PlacementInfo is UISplitChildPlacementInfo bInfo)
                bInfo.Offset = 0;
            children[1].FitLayout(new(parentRegion.X, parentRegion.Y, parentRegion.Width, bottomSize));
        }

        public override void VerifyPlacementInfo(UITransform childTransform, ref UIChildPlacementInfo? placementInfo)
        {
            if (placementInfo is not UISplitChildPlacementInfo)
                placementInfo = new UISplitChildPlacementInfo(childTransform);
        }

        public class UISplitChildPlacementInfo(UITransform owner) : UIChildPlacementInfo(owner)
        {
            private float _offset;
            public float Offset
            {
                get => _offset;
                set => SetField(ref _offset, value);
            }

            public UIMultiSplitTransform? ParentSplit => Owner?.Parent as UIMultiSplitTransform;

            public bool Vertical => ParentSplit?.Arrangement == UISplitArrangement.TopMiddleBottom || ParentSplit?.Arrangement == UISplitArrangement.TopBottom;

            public override Matrix4x4 GetRelativeItemMatrix()
            {
                bool vertical = Vertical;
                return Matrix4x4.CreateTranslation(vertical ? 0 : Offset, vertical ? Offset : 0, 0);
            }
        }
    }
}
