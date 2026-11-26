using XREngine.Data.Transforms.Rotations;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    /// <summary>
    /// Shows a <see cref="Rotator"/> as three fields: pitch, yaw, and roll in degrees, the same values the ImGui
    /// transform editor shows. A field edit writes a new rotator that keeps the other two angles and the order.
    /// </summary>
    public class RotatorTransformer : DataTransformerBase<Rotator?>
    {
        private float? _pitch;
        private float? _yaw;
        private float? _roll;

        public float? Pitch
        {
            get => _pitch;
            set => SetField(ref _pitch, value);
        }
        public float? Yaw
        {
            get => _yaw;
            set => SetField(ref _yaw, value);
        }
        public float? Roll
        {
            get => _roll;
            set => SetField(ref _roll, value);
        }

        protected override bool Equal(Rotator? lhs, Rotator? rhs)
            => lhs is null || rhs is null ? lhs is null && rhs is null : lhs.Value.Equals(rhs.Value);

        protected override void DisplayValue(Rotator? value)
        {
            if (value is null)
            {
                Pitch = Yaw = Roll = null;
                return;
            }
            Pitch = value.Value.Pitch;
            Yaw = value.Value.Yaw;
            Roll = value.Value.Roll;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (!CanWriteSource || propName is not nameof(Pitch) and not nameof(Yaw) and not nameof(Roll))
                return;
            UpdateSourceValue(previous =>
            {
                Rotator value = previous is Rotator source ? source : new Rotator();
                if (propName == nameof(Pitch)) value.Pitch = Pitch ?? 0.0f;
                if (propName == nameof(Yaw)) value.Yaw = Yaw ?? 0.0f;
                if (propName == nameof(Roll)) value.Roll = Roll ?? 0.0f;
                return value;
            });
        }
    }
}
