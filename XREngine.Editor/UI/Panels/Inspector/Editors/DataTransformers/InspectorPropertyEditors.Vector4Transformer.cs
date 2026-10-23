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
            if (!CanWriteSource || propName is not nameof(X) and not nameof(Y) and not nameof(Z) and not nameof(W))
                return;
            UpdateSourceValue(previous =>
            {
                Vector4 value = previous is Vector4 source ? source : default;
                if (propName == nameof(X)) value.X = X ?? 0.0f;
                if (propName == nameof(Y)) value.Y = Y ?? 0.0f;
                if (propName == nameof(Z)) value.Z = Z ?? 0.0f;
                if (propName == nameof(W)) value.W = W ?? 0.0f;
                return value;
            });
        }
    }
}
