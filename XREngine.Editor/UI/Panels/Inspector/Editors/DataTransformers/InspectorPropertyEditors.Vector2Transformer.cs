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
            if (Property is null || Targets is null || propName is not nameof(X) and not nameof(Y) || !IsFocused)
                return;
            var newVector = new Vector2(
                X ?? 0.0f,
                Y ?? 0.0f);
            foreach (var target in Targets)
            {
                if (target is null)
                    continue;
                Property.SetValue(target, newVector);
            }
        }
    }
}
