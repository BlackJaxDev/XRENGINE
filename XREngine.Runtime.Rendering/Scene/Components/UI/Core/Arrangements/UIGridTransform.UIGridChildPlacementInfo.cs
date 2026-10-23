using System.ComponentModel;
using System.Numerics;

namespace XREngine.Rendering.UI
{
    public partial class UIGridTransform
    {
        public class UIGridChildPlacementInfo(UITransform owner) : UIChildPlacementInfo(owner)
        {
            private int _row = 0;
            private int _column = 0;
            private int _rowSpan = 1;
            private int _columnSpan = 1;

            public int Row
            {
                get => _row;
                set => SetField(ref _row, value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
            }
            public int Column
            {
                get => _column;
                set => SetField(ref _column, value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
            }
            public int RowSpan
            {
                get => _rowSpan;
                set => SetField(ref _rowSpan, value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
            }
            public int ColumnSpan
            {
                get => _columnSpan;
                set => SetField(ref _columnSpan, value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
            }

            private Vector2 _offset;
            /// <summary>Bottom-left offset of the arranged cell from the grid origin.</summary>
            public Vector2 Offset
            {
                get => _offset;
                set => SetField(ref _offset, value);
            }

            /// <summary>Applies a validated layout result without publishing an authored placement edit.</summary>
            internal void ApplyLayoutOffset(Vector2 value)
            {
                if (_offset == value)
                    return;
                _offset = value;
                RelativePositioningChanged = true;
            }

            public HashSet<int> AssociatedRowIndices { get; } = [];
            public HashSet<int> AssociatedColumnIndices { get; } = [];

            public override Matrix4x4 GetRelativeItemMatrix()
                => Matrix4x4.CreateTranslation(Offset.X, Offset.Y, 0);
        }
    }
}
