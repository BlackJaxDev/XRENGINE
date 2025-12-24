using System.Numerics;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public class Vector4Transformer : DataTransformerBase<Vector4?>
    {
        private float? _x;
        private float? _y;
        private float? _z;
        private float? _w;
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
        public float? W
        {
            get => _w;
            set => SetField(ref _w, value);
        }
        protected override bool Equal(Vector4? lhs, Vector4? rhs)
            => lhs is null || rhs is null ? lhs is null && rhs is null : lhs.Value.Equals(rhs.Value);
        protected override void DisplayValue(Vector4? value)
        {
            if (value is null)
            {
                X = Y = Z = W = null;
                return;
            }
            X = value.Value.X;
            Y = value.Value.Y;
            Z = value.Value.Z;
            W = value.Value.W;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (Property is null || Targets is null || propName is not nameof(X) and not nameof(Y) and not nameof(Z) and not nameof(W) || !IsFocused)
                return;
            var newVector = new Vector4(
                X ?? 0.0f,
                Y ?? 0.0f,
                Z ?? 0.0f,
                W ?? 0.0f);
            foreach (var target in Targets)
            {
                if (target is null)
                    continue;
                Property.SetValue(target, newVector);
            }
        }
    }
}
