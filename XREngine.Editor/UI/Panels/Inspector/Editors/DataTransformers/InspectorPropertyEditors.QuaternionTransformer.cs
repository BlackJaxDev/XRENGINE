using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public class QuaternionTransformer : DataTransformerBase<Quaternion?>
    {
        private float? _yaw;
        private float? _pitch;
        private float? _roll;

        public float? Yaw
        {
            get => _yaw;
            set => SetField(ref _yaw, value);
        }
        public float? Pitch
        {
            get => _pitch;
            set => SetField(ref _pitch, value);
        }
        public float? Roll
        {
            get => _roll;
            set => SetField(ref _roll, value);
        }

        protected override bool Equal(Quaternion? lhs, Quaternion? rhs)
            => lhs is null || rhs is null ? lhs is null && rhs is null : lhs.Value.Equals(rhs.Value);

        protected override void DisplayValue(Quaternion? value)
        {
            if (value is null)
            {
                Yaw = Pitch = Roll = null;
                return;
            }

            Vector3 pyr = XRMath.QuaternionToEuler(value.Value);
            Yaw = pyr.Y; // Yaw is the second component in the Euler angles (Y, P, R)
            Pitch = pyr.X; // Pitch is the first component in the Euler angles (Y, P, R)
            Roll = pyr.Z; // Roll is the third component in the Euler angles (Y, P, R)
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            if (Property is null || Targets is null || propName is not nameof(Yaw) and not nameof(Pitch) and not nameof(Roll) || !IsFocused)
                return;

            var newQuat = Quaternion.CreateFromYawPitchRoll(
                Yaw ?? 0.0f,
                Pitch ?? 0.0f,
                Roll ?? 0.0f);

            foreach (var target in Targets)
            {
                if (target is null)
                    continue;

                Property.SetValue(target, newQuat);
            }
        }
    }
}
