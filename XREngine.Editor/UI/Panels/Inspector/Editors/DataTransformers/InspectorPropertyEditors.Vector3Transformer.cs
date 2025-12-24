using System.Numerics;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public class Vector3Transformer : DataTransformerBase<Vector3?>
    {
        private float? _x;
        private float? _y;
        private float? _z;
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
        public float? Z
        {
            get => _z;
            set => SetField(ref _z, value);
        }
        protected override bool Equal(Vector3? lhs, Vector3? rhs)
            => lhs is null || rhs is null ? lhs is null && rhs is null : lhs.Value.Equals(rhs.Value);
        protected override void DisplayValue(Vector3? value)
        {
            if (value is null)
            {
                X = Y = Z = null;
                return;
            }
            X = value.Value.X;
            Y = value.Value.Y;
            Z = value.Value.Z;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (Property is null || Targets is null || propName is not nameof(X) and not nameof(Y) and not nameof(Z) || !IsFocused)
                return;
            var newVector = new Vector3(
                X ?? 0.0f,
                Y ?? 0.0f,
                Z ?? 0.0f);
            foreach (var target in Targets)
            {
                if (target is null)
                    continue;
                Property.SetValue(target, newVector);
            }
        }
    }
}
