using Extensions;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Xml.Serialization;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Data.Transforms.Rotations
{
    public enum ERotationOrder
    {
        YPR = 0,
        YRP,
        PRY,
        PYR,
        RPY,
        RYP,
    }

    /// <summary>
    /// Represents a rotation in 3D space using Euler angles in any order.
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Rotator
    {
        public Rotator() : this(ERotationOrder.YPR) { }

        public Rotator(ERotationOrder order) : this(0.0f, 0.0f, 0.0f, order) { }
        public Rotator(float pitch, float yaw, float roll, ERotationOrder rotationOrder = ERotationOrder.YPR)
        {
            Yaw = yaw;
            Pitch = pitch;
            Roll = roll;
            _rotationOrder = rotationOrder;
        }
        public Rotator(Vector3 pyr, ERotationOrder rotationOrder)
        {
            _pyr = pyr;
            _rotationOrder = rotationOrder;
        }
        public Rotator(Rotator rotation)
        {
            if (rotation != null)
            {
                _pyr = rotation._pyr;
                _rotationOrder = rotation._rotationOrder;
            }
        }

        private Vector3 _pyr;
        public Vector3 PitchYawRoll
        {
            readonly get => _pyr;
            set => _pyr = value;
        }

        private ERotationOrder _rotationOrder = ERotationOrder.YPR;
        public ERotationOrder Order
        {
            readonly get => _rotationOrder;
            set => _rotationOrder = value;
        }

        /// <summary>
        /// Converts the rotator into a quaternion.
        /// </summary>
        /// <param name="matrixCalc">If true, calculates the quaternion using matrix math. If false, uses axis-angle math.</param>
        /// <returns></returns>
        public readonly Quaternion ToQuaternion(bool matrixCalc = false)
            => matrixCalc
                ? ToQuaternionViaMatrices()
                : ToQuaternionViaAxisAngles();

        private readonly Quaternion ToQuaternionViaAxisAngles()
        {
            Quaternion p = Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(Pitch));
            Quaternion y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, XRMath.DegToRad(Yaw));
            Quaternion r = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, XRMath.DegToRad(Roll));
            return Order switch
            {
                ERotationOrder.RYP => r * y * p,
                ERotationOrder.YRP => y * r * p,
                ERotationOrder.PRY => p * r * y,
                ERotationOrder.RPY => r * p * y,
                ERotationOrder.YPR => y * p * r,
                ERotationOrder.PYR => p * y * r,
                _ => Quaternion.Identity,
            };
        }

        private readonly Quaternion ToQuaternionViaMatrices()
            => Quaternion.CreateFromRotationMatrix(GetMatrix());

        public readonly Matrix4x4 GetMatrix()
        {
            Matrix4x4 y = GetYawMatrix();
            Matrix4x4 p = GetPitchMatrix();
            Matrix4x4 r = GetRollMatrix();
            return Order switch
            {
                ERotationOrder.RPY => y * p * r,
                ERotationOrder.PRY => y * r * p,
                ERotationOrder.RYP => p * y * r,
                ERotationOrder.YRP => p * r * y,
                ERotationOrder.YPR => r * p * y,
                ERotationOrder.PYR => r * y * p,
                _ => Matrix4x4.Identity,
            };
        }

        public readonly Matrix4x4 GetInverseMatrix()
            => Inverted().GetMatrix();

        public readonly Vector3 GetDirection()
            => TransformVector(Globals.Forward);

        public readonly Vector3 TransformVector(Vector3 vector)
            => Vector3.Transform(vector, GetMatrix());

        public readonly Matrix4x4 GetYawMatrix()
            => Matrix4x4.CreateRotationY(float.DegreesToRadians(Yaw));
        public readonly Matrix4x4 GetPitchMatrix()
            => Matrix4x4.CreateRotationX(float.DegreesToRadians(Pitch));
        public readonly Matrix4x4 GetRollMatrix()
            => Matrix4x4.CreateRotationZ(float.DegreesToRadians(Roll));

        public readonly Quaternion GetYawQuaternion()
            => Quaternion.CreateFromAxisAngle(Vector3.UnitY, XRMath.DegToRad(Yaw));
        public readonly Quaternion GetPitchQuaternion()
            => Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(Pitch));
        public readonly Quaternion GetRollQuaternion()
            => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, XRMath.DegToRad(Roll));

        /// <summary>
        /// Returns a YPR rotator looking from the origin to the end of this vector.
        /// Initial vector is assumed to be pointing along the -Z axis.
        /// Yaw rotates counterclockwise around the Y axis,
        /// and pitch rotates upward around the X axis, after X has been yawed.
        /// </summary>
        public static Rotator CreateLookat(Vector3 vector)
            => XRMath.LookatAngles(vector);

        public void LookAt(Vector3 vector)
            => SetRotations(CreateLookat(vector));

        public readonly Rotator HardCopy()
            => new(this);

        public readonly Rotator WithNegatedRotations()
            => new(-Pitch, -Yaw, -Roll, _rotationOrder);

        public void NegateRotations()
        {
            Yaw = -Yaw;
            Pitch = -Pitch;
            Roll = -Roll;
        }

        public void ReverseRotations()
        {
            Yaw += 180.0f;
            Pitch += 180.0f;
            Roll += 180.0f;
        }

        /// <summary>
        /// Clears rotational winding to the range of (min, min + 360.0f).
        /// </summary>
        /// <param name="min"></param>
        public void ClearWinding(float min = 0.0f)
        {
            float max = min + 360.0f;
            Yaw = Yaw.RemapToRange(min, max);
            Pitch = Pitch.RemapToRange(min, max);
            Roll = Roll.RemapToRange(min, max);
        }

        public readonly int GetYawWindCount()
            => (int)(Yaw / 360.0f);

        public readonly int GetPitchWindCount()
            => (int)(Pitch / 360.0f);

        public readonly int GetRollWindCount()
            => (int)(Roll / 360.0f);

        public void ReverseOrder()
            => _rotationOrder = OppositeOf(_rotationOrder);

        public void Invert()
        {
            NegateRotations();
            ReverseOrder();
        }

        public readonly Rotator Inverted()
            => new(-Pitch, -Yaw, -Roll, OppositeOf(_rotationOrder));

        public static ERotationOrder OppositeOf(ERotationOrder order)
            => order switch
            {
                ERotationOrder.PRY => ERotationOrder.YRP,
                ERotationOrder.PYR => ERotationOrder.RYP,
                ERotationOrder.RPY => ERotationOrder.YPR,
                ERotationOrder.RYP => ERotationOrder.PYR,
                ERotationOrder.YRP => ERotationOrder.PRY,
                ERotationOrder.YPR => ERotationOrder.RPY,
                _ => throw new Exception("Invalid rotation order"),
            };

        public static Rotator ComponentMin(Rotator a, Rotator b)
        {
            a.Yaw = a.Yaw < b.Yaw ? a.Yaw : b.Yaw;
            a.Pitch = a.Pitch < b.Pitch ? a.Pitch : b.Pitch;
            a.Roll = a.Roll < b.Roll ? a.Roll : b.Roll;
            return a;
        }

        public static Rotator ComponentMax(Rotator a, Rotator b)
        {
            a.Yaw = a.Yaw > b.Yaw ? a.Yaw : b.Yaw;
            a.Pitch = a.Pitch > b.Pitch ? a.Pitch : b.Pitch;
            a.Roll = a.Roll > b.Roll ? a.Roll : b.Roll;
            return a;
        }

        public static Rotator Clamp(Rotator value, Rotator min, Rotator max) => new()
        {
            Yaw = value.Yaw < min.Yaw ? min.Yaw : value.Yaw > max.Yaw ? max.Yaw : value.Yaw,
            Pitch = value.Pitch < min.Pitch ? min.Pitch : value.Pitch > max.Pitch ? max.Pitch : value.Pitch,
            Roll = value.Roll < min.Roll ? min.Roll : value.Roll > max.Roll ? max.Roll : value.Roll
        };

        public static Rotator? Lerp(Rotator r1, Rotator r2, float time)
        {
            if (r1.Order != r2.Order)
                return null;

            return new Rotator(
                Interp.Lerp(r1.Pitch, r2.Pitch, time),
                Interp.Lerp(r1.Yaw, r2.Yaw, time),
                Interp.Lerp(r1.Roll, r2.Roll, time),
                r1.Order);
        }

        public void Clamp(Rotator min, Rotator max)
        {
            Yaw = Yaw < min.Yaw ? min.Yaw : Yaw > max.Yaw ? max.Yaw : Yaw;
            Pitch = Pitch < min.Pitch ? min.Pitch : Pitch > max.Pitch ? max.Pitch : Pitch;
            Roll = Roll < min.Roll ? min.Roll : Roll > max.Roll ? max.Roll : Roll;
        }

        public Rotator Clamped(Rotator min, Rotator max)
        {
            Rotator v = new()
            {
                Yaw = Yaw < min.Yaw ? min.Yaw : Yaw > max.Yaw ? max.Yaw : Yaw,
                Pitch = Pitch < min.Pitch ? min.Pitch : Pitch > max.Pitch ? max.Pitch : Pitch,
                Roll = Roll < min.Roll ? min.Roll : Roll > max.Roll ? max.Roll : Roll
            };
            return v;
        }

        public void SetRotations(float pitch, float yaw, float roll)
        {
            Pitch = pitch;
            Yaw = yaw;
            Roll = roll;
        }

        public void SetRotations(Rotator other)
        {
            if (other != null)
            {
                _pyr = other._pyr;
                _rotationOrder = other._rotationOrder;
            }
            else
            {
                _pyr = Vector3.Zero;
                _rotationOrder = ERotationOrder.PYR;
            }
        }

        public void SetRotations(float pitch, float yaw, float roll, ERotationOrder order)
        {
            Pitch = pitch;
            Yaw = yaw;
            Roll = roll;
            _rotationOrder = order;
        }

        public void AddRotations(float pitch, float yaw, float roll)
        {
            Pitch += pitch;
            Yaw += yaw;
            Roll += roll;
        }

        public void RemapToRange(float min, float max)
        {
            Pitch = Pitch.RemapToRange(min, max);
            Yaw = Yaw.RemapToRange(min, max);
            Roll = Roll.RemapToRange(min, max);
        }

        public void ChangeZupToYup()
        {
            float temp = _pyr.X;
            _pyr.X = _pyr.Y;
            _pyr.Y = _pyr.Z;
            _pyr.Z = temp;
        }

        public void Round(int decimalPlaces)
        {
            Roll = (float)Math.Round(Roll, decimalPlaces);
            Pitch = (float)Math.Round(Pitch, decimalPlaces);
            Yaw = (float)Math.Round(Yaw, decimalPlaces);
        }

        [Browsable(false)]
        [YamlIgnore]
        public Vector2 YawPitch
        {
            readonly get => new(Yaw, Pitch);
            set
            {
                Yaw = value.X;
                Pitch = value.Y;
            }
        }
        [Category("Rotator")]
        [YamlIgnore]
        public float Yaw
        {
            readonly get => _pyr.Y;
            set => _pyr.Y = value;
        }
        [Category("Rotator")]
        [YamlIgnore]
        public float Pitch
        {
            readonly get => _pyr.X;
            set => _pyr.X = value;
        }
        [Category("Rotator")]
        [YamlIgnore]
        public float Roll
        {
            readonly get => _pyr.Z;
            set => _pyr.Z = value;
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector2 YawRoll
        {
            readonly get => new(Yaw, Roll);
            set
            {
                Yaw = value.X;
                Roll = value.Y;
            }
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector2 PitchYaw
        {
            readonly get => new(Pitch, Yaw);
            set
            {
                Pitch = value.X;
                Yaw = value.Y;
            }
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector2 PitchRoll
        {
            readonly get => new(Pitch, Roll);
            set
            {
                Pitch = value.X;
                Roll = value.Y;
            }
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector2 RollYaw
        {
            readonly get => new(Roll, Yaw);
            set
            {
                Roll = value.X;
                Yaw = value.Y;
            }
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector2 RollPitch
        {
            readonly get => new(Roll, Pitch);
            set
            {
                Roll = value.X;
                Pitch = value.Y;
            }
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector3 YawPitchRoll
        {
            readonly get => new(Yaw, Pitch, Roll);
            set
            {
                Yaw = value.X;
                Pitch = value.Y;
                Roll = value.Z;
            }
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector3 YawRollPitch
        {
            readonly get => new(Yaw, Roll, Pitch);
            set
            {
                Yaw = value.X;
                Roll = value.Y;
                Pitch = value.Z;
            }
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector3 PitchRollYaw
        {
            readonly get => new(Pitch, Roll, Yaw);
            set
            {
                Pitch = value.X;
                Roll = value.Y;
                Yaw = value.Z;
            }
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector3 RollYawPitch
        {
            readonly get => new(Roll, Yaw, Pitch);
            set
            {
                Roll = value.X;
                Yaw = value.Y;
                Pitch = value.Z;
            }
        }
        [Browsable(false)]
        [YamlIgnore]
        public Vector3 RollPitchYaw
        {
            readonly get => new(Roll, Pitch, Yaw);
            set
            {
                Roll = value.X;
                Pitch = value.Y;
                Yaw = value.Z;
            }
        }

        public static bool operator ==(Rotator? left, Rotator? right) =>
            left is null
                ? right is null
                : right is not null && left.Equals(right);

        public static bool operator !=(Rotator? left, Rotator? right) =>
            left is null
                ? right is not null
                : right is null || !left.Equals(right);

        public static explicit operator Vector3(Rotator v)
            => v.PitchYawRoll;

        public static explicit operator Rotator(Vector3 v)
            => new(v.X, v.Y, v.Z, ERotationOrder.PYR);

        public static Rotator GetZero(ERotationOrder order = ERotationOrder.YPR) 
            => new(0.0f, 0.0f, 0.0f, order);

        public static Rotator Parse(string value)
        {
            string[] parts = value.Split(' ');
            return new Rotator(
                float.Parse(parts[0][1..^1]),
                float.Parse(parts[1][..^1]),
                float.Parse(parts[2][..^1]),
                Enum.Parse<ERotationOrder>(parts[3].AsSpan(0, parts[3].Length - 1)));
        }

        public static Rotator Parse(string value, ERotationOrder order)
        {
            string[] parts = value.Split(' ');
            return new Rotator(
                float.Parse(parts[0][1..^1]),
                float.Parse(parts[1][..^1]),
                float.Parse(parts[2][..^1]),
                order);
        }

        public override readonly string ToString()
            => string.Format("({0}{3} {1}{3} {2}{3} {4})", Pitch, Yaw, Roll, CultureInfo.CurrentCulture.TextInfo.ListSeparator, _rotationOrder);

        public override readonly int GetHashCode()
            => HashCode.Combine(Yaw, Pitch, Roll);

        public override readonly bool Equals(object? obj)
            => obj is Rotator rotator && Equals(rotator);

        public readonly bool Equals(Rotator other) =>
            Yaw == other.Yaw &&
            Pitch == other.Pitch &&
            Roll == other.Roll;

        public readonly bool Equals(Rotator other, float precision) => 
            MathF.Abs(Yaw - other.Yaw) < precision &&
            MathF.Abs(Pitch - other.Pitch) < precision &&
            MathF.Abs(Roll - other.Roll) < precision;

        public readonly bool IsZero() =>
            Pitch.IsZero() &&
            Yaw.IsZero() &&
            Roll.IsZero();

        public static Rotator FromQuaternion(Quaternion rotation)
        {
            Vector3 euler = XRMath.QuaternionToEuler(rotation);
            return new Rotator(float.RadiansToDegrees(euler.X), float.RadiansToDegrees(euler.Y), float.RadiansToDegrees(euler.Z), ERotationOrder.YPR);
        }
        public static Quaternion ToQuaternion(Vector3 euler)
        {
            return Quaternion.CreateFromYawPitchRoll(
                XRMath.DegToRad(euler.Y),
                XRMath.DegToRad(euler.X),
                XRMath.DegToRad(euler.Z));
        }

        public void NormalizeRotations180()
        {
            Yaw = NormalizeAngle180(Yaw);
            Pitch = NormalizeAngle180(Pitch);
            Roll = NormalizeAngle180(Roll);
        }

        public static float NormalizeAngle180(float angle)
        {
            angle = angle % 360;
            if (angle > 180)
                angle -= 360;
            if (angle < -180)
                angle += 360;
            return angle;
        }
    }
}
