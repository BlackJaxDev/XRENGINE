using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI
{
    [XRTransformEditor("XREngine.Editor.TransformEditors.UIScrollableTransformEditor")]
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
        private Vector2 _scrollOffset;
        private Vector2 _contentExtent;
        private Vector2 _viewportExtent;
        private bool _hasAcceptedScrollGeometry;

        /// <summary>Positive X scrolls right and positive Y scrolls down.</summary>
        public Vector2 ScrollOffset
        {
            get => _scrollOffset;
            set
            {
                if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
                    throw new ArgumentOutOfRangeException(nameof(value));
                value = Vector2.Max(value, Vector2.Zero);
                if (_hasAcceptedScrollGeometry)
                    value = Vector2.Min(value, MaxScrollOffset);
                if (SetField(ref _scrollOffset, value))
                {
                    InvalidateArrange();
                    RecordUIChange(UIChangeDomain.Arrange | UIChangeDomain.Transform | UIChangeDomain.HitTest);
                }
            }
        }

        /// <summary>Accepted logical content size. Virtual lists contribute their total logical extent.</summary>
        public Vector2 ContentExtent => _contentExtent;
        public Vector2 ViewportExtent => _viewportExtent;
        public Vector2 MaxScrollOffset => Vector2.Max(Vector2.Zero, _contentExtent - _viewportExtent);

        /// <summary>Updates scroll limits from accepted geometry before layout input capture.</summary>
        public void SynchronizeScroll(Vector2 viewportExtent)
        {
            if (!float.IsFinite(viewportExtent.X) || !float.IsFinite(viewportExtent.Y) ||
                viewportExtent.X <= 0 || viewportExtent.Y <= 0)
                return;

            Vector2 content = Vector2.Zero;
            bool hasMeasuredChild = Children.Count == 0;
            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i] is not UIBoundableTransform child)
                    continue;
                if (child is UIListTransform list && list.VirtualProvider is not null)
                {
                    hasMeasuredChild = true;
                    if (list.DisplayHorizontal)
                        content.X = MathF.Max(content.X, list.VirtualContentExtent);
                    else
                        content.Y = MathF.Max(content.Y, list.VirtualContentExtent);
                    continue;
                }
                if (child.ActualWidth <= 0 || child.ActualHeight <= 0)
                    continue;
                hasMeasuredChild = true;
                Vector2 end = child.ActualLocalBottomLeftTranslation + child.ActualSize + ScrollOffset;
                content = Vector2.Max(content, end);
            }
            if (!hasMeasuredChild)
                return;
            _viewportExtent = viewportExtent;
            _contentExtent = Vector2.Max(content, viewportExtent);
            _hasAcceptedScrollGeometry = true;
            ScrollOffset = Vector2.Min(_scrollOffset, MaxScrollOffset);
        }

        /// <summary>Moves the viewport to include a top-down content rectangle.</summary>
        public void ScrollIntoView(Vector2 contentStart, Vector2 contentEnd)
        {
            if (!_hasAcceptedScrollGeometry)
                return;
            Vector2 offset = ScrollOffset;
            if (contentStart.X < offset.X)
                offset.X = contentStart.X;
            else if (contentEnd.X > offset.X + ViewportExtent.X)
                offset.X = contentEnd.X - ViewportExtent.X;
            if (contentStart.Y < offset.Y)
                offset.Y = contentStart.Y;
            else if (contentEnd.Y > offset.Y + ViewportExtent.Y)
                offset.Y = contentEnd.Y - ViewportExtent.Y;
            ScrollOffset = offset;
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

        public override void VerifyPlacementInfo(TransformBase childTransform, ref ITransformChildPlacementInfo? placementInfo)
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
