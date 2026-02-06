using Extensions;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// A transform that splits two children into two regions.
    /// The user can drag the splitter to resize the regions.
    /// </summary>
    public class UIDualSplitTransform : UIBoundableTransform
    {
        private bool _verticalSplit = false;
        public bool VerticalSplit
        {
            get => _verticalSplit;
            set => SetField(ref _verticalSplit, value);
        }

        private float? _fixedSize = null;
        /// <summary>
        /// The fixed size of the top or bottom region, depending on TopFixed.
        /// </summary>
        public float? FixedSize
        {
            get => _fixedSize;
            set => SetField(ref _fixedSize, value);
        }

        private bool? _topFixed = null;
        /// <summary>
        /// If null, both regions scale by parent size.
        /// If true, the top region uses FixedSize and the bottom region scales to fill the remaining space.
        /// If false, the bottom region uses FixedSize and the top region scales to fill the remaining space.
        /// </summary>
        public bool? FirstFixedSize
        {
            get => _topFixed;
            set => SetField(ref _topFixed, value);
        }

        private float _splitPercent = 0.5f;
        public float SplitPercent
        {
            get => _splitPercent;
            set => SetField(ref _splitPercent, value.Clamp(0.0f, 1.0f));
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

        public UIBoundableTransform? First => Children.FirstOrDefault() as UIBoundableTransform;
        public UIBoundableTransform? Second => Children.LastOrDefault() as UIBoundableTransform;

        /// <summary>
        /// New layout path: arranges children using the centralized layout system.
        /// Splits the padded region into two sub-regions and arranges each child within its region.
        /// </summary>
        protected override void ArrangeChildren(BoundingRectangleF childRegion)
        {
            var paddedRegion = ApplyPadding(childRegion);

            var a = First;
            var b = Second;
            if (a is null)
                return;

            if (b is null)
            {
                UILayoutSystem.FitLayout(a, paddedRegion);
                return;
            }
            else if (VerticalSplit)
            {
                float topSize, bottomSize;

                if (FirstFixedSize.HasValue)
                {
                    if (FirstFixedSize.Value)
                    {
                        float fixedSize = GetFixedSize(true);
                        topSize = fixedSize;
                        bottomSize = paddedRegion.Height - fixedSize;
                    }
                    else
                    {
                        float fixedSize = GetFixedSize(false);
                        topSize = paddedRegion.Height - fixedSize;
                        bottomSize = fixedSize;
                    }
                }
                else
                {
                    float split = paddedRegion.Height * SplitPercent;
                    topSize = split;
                    bottomSize = paddedRegion.Height - split;
                }

                if (a.PlacementInfo is UISplitChildPlacementInfo aInfo)
                    aInfo.Offset = bottomSize + SplitterSize;
                UILayoutSystem.FitLayout(a, new(paddedRegion.X, paddedRegion.Y + bottomSize + SplitterSize, paddedRegion.Width, topSize));

                if (b.PlacementInfo is UISplitChildPlacementInfo bInfo)
                    bInfo.Offset = 0;
                UILayoutSystem.FitLayout(b, new(paddedRegion.X, paddedRegion.Y, paddedRegion.Width, bottomSize));
            }
            else
            {
                float leftSize, rightSize;

                if (FirstFixedSize.HasValue)
                {
                    if (FirstFixedSize.Value)
                    {
                        float fixedSize = GetFixedSize(true);
                        leftSize = fixedSize;
                        rightSize = paddedRegion.Width - fixedSize;
                    }
                    else
                    {
                        float fixedSize = GetFixedSize(false);
                        leftSize = paddedRegion.Width - fixedSize;
                        rightSize = fixedSize;
                    }
                }
                else
                {
                    float split = paddedRegion.Width * SplitPercent;
                    leftSize = split;
                    rightSize = paddedRegion.Width - split;
                }

                if (a.PlacementInfo is UISplitChildPlacementInfo aInfo)
                    aInfo.Offset = 0;
                UILayoutSystem.FitLayout(a, new(paddedRegion.X, paddedRegion.Y, leftSize, paddedRegion.Height));

                if (b.PlacementInfo is UISplitChildPlacementInfo bInfo)
                    bInfo.Offset = leftSize + SplitterSize;
                UILayoutSystem.FitLayout(b, new(paddedRegion.X + leftSize + SplitterSize, paddedRegion.Y, rightSize, paddedRegion.Height));
            }
        }

        /// <summary>
        /// Old layout path: called from OnLocalMatrixChanged.
        /// Kept for compatibility — the new ArrangeChildren path handles arrangement during the layout system pass.
        /// When OnResizeChildComponents fires after ArrangeChildren, FitLayout calls will early-exit
        /// because children were already arranged with the same bounds.
        /// </summary>
        protected override void OnResizeChildComponents(BoundingRectangleF parentRegion)
        {
            var a = First;
            var b = Second;
            if (a is null)
                return;

            if (b is null)
            {
                a.FitLayout(parentRegion);
                return;
            }
            else if (VerticalSplit)
            {
                float topSize, bottomSize;

                if (FirstFixedSize.HasValue)
                {
                    if (FirstFixedSize.Value)
                    {
                        float fixedSize = GetFixedSize(true);
                        topSize = fixedSize;
                        bottomSize = parentRegion.Height - fixedSize;
                    }
                    else
                    {
                        float fixedSize = GetFixedSize(false);
                        topSize = parentRegion.Height - fixedSize;
                        bottomSize = fixedSize;
                    }
                }
                else
                {
                    float split = parentRegion.Height * SplitPercent;
                    topSize = split;
                    bottomSize = parentRegion.Height - split;
                }

                if (a.PlacementInfo is UISplitChildPlacementInfo aInfo)
                    aInfo.Offset = bottomSize + SplitterSize;
                a.FitLayout(new(parentRegion.X, parentRegion.Y + bottomSize + SplitterSize, parentRegion.Width, topSize));

                if (b.PlacementInfo is UISplitChildPlacementInfo bInfo)
                    bInfo.Offset = 0;
                b.FitLayout(new(parentRegion.X, parentRegion.Y, parentRegion.Width, bottomSize));
            }
            else
            {
                float leftSize, rightSize;

                if (FirstFixedSize.HasValue)
                {
                    if (FirstFixedSize.Value)
                    {
                        float fixedSize = GetFixedSize(true);
                        leftSize = fixedSize;
                        rightSize = parentRegion.Width - fixedSize;
                    }
                    else
                    {
                        float fixedSize = GetFixedSize(false);
                        leftSize = parentRegion.Width - fixedSize;
                        rightSize = fixedSize;
                    }
                }
                else
                {
                    float split = parentRegion.Width * SplitPercent;
                    leftSize = split;
                    rightSize = parentRegion.Width - split;
                }

                if (a.PlacementInfo is UISplitChildPlacementInfo aInfo)
                    aInfo.Offset = 0;
                a.FitLayout(new(parentRegion.X, parentRegion.Y, leftSize, parentRegion.Height));

                if (b.PlacementInfo is UISplitChildPlacementInfo bInfo)
                    bInfo.Offset = leftSize + SplitterSize;
                b.FitLayout(new(parentRegion.X + leftSize + SplitterSize, parentRegion.Y, rightSize, parentRegion.Height));
            }
        }

        private float GetFixedSize(bool firstChild)
        {
            if (FixedSize.HasValue)
                return FixedSize.Value;

            if (firstChild)
            {
                var a = First;
                return a is null ? 0 : (VerticalSplit ? a.GetHeight() : a.GetWidth());
            }
            else
            {
                var b = Second;
                return b is null ? 0 : (VerticalSplit ? b.GetHeight() : b.GetWidth());
            }
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

            public bool Vertical => (Owner?.Parent as UIDualSplitTransform)?.VerticalSplit ?? false;

            public override Matrix4x4 GetRelativeItemMatrix()
                => Matrix4x4.CreateTranslation(
                    Vertical ? 0 : Offset,
                    Vertical ? Offset : 0,
                    0);
        }
    }
}
