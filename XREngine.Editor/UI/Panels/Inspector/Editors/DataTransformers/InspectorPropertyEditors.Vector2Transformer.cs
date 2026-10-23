using System.Numerics;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public class Vector2Transformer : DataTransformerBase<Vector2?>
    {
        private float? _x;
        private float? _y;
        public float? X
        {
            get => _x;
            set => SetField(ref _x, value);
        }
        public float? Y
        {
            get => _y;
            set => SetField(ref _y, value);
        }
        protected override bool Equal(Vector2? lhs, Vector2? rhs)
            => lhs is null || rhs is null ? lhs is null && rhs is null : lhs.Value.Equals(rhs.Value);
        protected override void DisplayValue(Vector2? value)
        {
            if (value is null)
            {
                X = Y = null;
                return;
            }
            X = value.Value.X;
            Y = value.Value.Y;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (!CanWriteSource || propName is not nameof(X) and not nameof(Y))
                return;
            UpdateSourceValue(previous =>
            {
                Vector2 value = previous is Vector2 source ? source : default;
                if (propName == nameof(X)) value.X = X ?? 0.0f;
                if (propName == nameof(Y)) value.Y = Y ?? 0.0f;
                return value;
            });
        }
    }
}
