using System.Numerics;
using XREngine.Data.Geometry;

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
}
