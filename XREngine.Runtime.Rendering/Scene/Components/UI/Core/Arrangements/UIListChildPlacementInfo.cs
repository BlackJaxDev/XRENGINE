using System.Numerics;

namespace XREngine.Rendering.UI
{
    public partial class UIListTransform
    {
        public class UIListChildPlacementInfo(UITransform owner) : UIChildPlacementInfo(owner)
        {
            private int _index;
            public int Index
            {
                get => _index;
                set => SetField(ref _index, value);
            }

            private float _offset;
            public float BottomOrLeftOffset
            {
                get => _offset;
                set => SetField(ref _offset, value);
            }

            private float _crossAxisOffset;
            /// <summary>
            /// Offset along the axis perpendicular to the list flow (for example left padding in a vertical list).
            /// </summary>
            public float CrossAxisOffset
            {
                get => _crossAxisOffset;
                set => SetField(ref _crossAxisOffset, value);
            }

            public UIListTransform? Transform => Owner?.Parent as UIListTransform;
            public bool Horizontal => Transform?.DisplayHorizontal ?? false;

            public override Matrix4x4 GetRelativeItemMatrix()
                => Matrix4x4.CreateTranslation(
                    Horizontal ? BottomOrLeftOffset : CrossAxisOffset,
                    Horizontal ? CrossAxisOffset : BottomOrLeftOffset,
                    0);
        }
    }
}