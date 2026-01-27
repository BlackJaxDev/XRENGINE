using Extensions;
using System.ComponentModel;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Animation;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class PropAnimVector4 : PropAnimVector<Vector4, Vector4Keyframe>
    {
        public PropAnimVector4() : base() { }
        public PropAnimVector4(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes) { }
        public PropAnimVector4(int frameCount, float FPS, bool looped, bool useKeyframes)
            : base(frameCount, FPS, looped, useKeyframes) { }

        protected override Vector4 LerpValues(Vector4 t1, Vector4 t2, float time) => Vector4.Lerp(t1, t2, time);
        protected override float[] GetComponents(Vector4 value) => [value.X, value.Y, value.Z, value.W];
        protected override Vector4 GetMaxValue() => new(float.MaxValue);
        protected override Vector4 GetMinValue() => new(float.MinValue);
        protected override float GetVelocityMagnitude()
        {
            Vector4 b = CurrentVelocity;
            float a = 1.0f;
            Vector5 start = Vector5.Zero;
            Vector5 end = new(a, b.X, b.Y, b.Z, b.W);
            return start.DistanceTo(end);
        }

        private struct Vector5(float x, float y, float z, float u, float v)
        {
            public float X = x, Y = y, Z = z, U = u, V = v;

            public static readonly Vector5 Zero = new();

            [Browsable(false)]
            public readonly float LengthSquared
                => X * X + Y * Y + Z * Z + U * U + V * V;

            [Browsable(false)]
            public readonly float Length
                => (float)Math.Sqrt(LengthSquared);
            [Browsable(false)]
            public readonly float LengthFast
                => 1.0f / XRMath.InverseSqrtFast(LengthSquared);

            public readonly float DistanceTo(Vector5 point)
                => (point - this).Length;
            public readonly float DistanceToFast(Vector5 point)
                => (point - this).LengthFast;
            public readonly float DistanceToSquared(Vector5 point)
                => (point - this).LengthSquared;

            public static Vector5 operator -(Vector5 left, Vector5 right)
                => new(
                    left.X - right.X,
                    left.Y - right.Y,
                    left.Z - right.Z,
                    left.U - right.U,
                    left.V - right.V);
        }
    }
    public class Vector4Keyframe : VectorKeyframe<Vector4>
    {
        public Vector4Keyframe()
          : this(0.0f, Vector4.Zero, Vector4.Zero, EVectorInterpType.Smooth) { }
        public Vector4Keyframe(int frameIndex, float FPS, Vector4 inValue, Vector4 outValue, Vector4 inTangent, Vector4 outTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inValue, outValue, inTangent, outTangent, type) { }
        public Vector4Keyframe(int frameIndex, float FPS, Vector4 inoutValue, Vector4 inoutTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public Vector4Keyframe(float second, Vector4 inoutValue, Vector4 inoutTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public Vector4Keyframe(float second, Vector4 inoutValue, Vector4 inTangent, Vector4 outTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inTangent, outTangent, type) { }
        public Vector4Keyframe(float second, Vector4 inValue, Vector4 outValue, Vector4 inTangent, Vector4 outTangent, EVectorInterpType type)
            : base(second, inValue, outValue, inTangent, outTangent, type) { }

        public override Vector4 LerpOut(VectorKeyframe<Vector4>? next, float diff, float span)
        {
            if (next is null || span.IsZero())
                return OutValue;
            return Interp.Lerp(OutValue, next.InValue, diff / span);
        }
        public override Vector4 LerpVelocityOut(VectorKeyframe<Vector4>? next, float diff, float span)
            => (next is null || span.IsZero()) ? Vector4.Zero : (next.InValue - OutValue) / (diff / span);

        public override Vector4 CubicBezierOut(VectorKeyframe<Vector4>? next, float diff, float span)
        {
            if (next is null || span.IsZero())
                return OutValue;
            return Interp.CubicBezier(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, diff / span);
        }
        public override Vector4 CubicBezierVelocityOut(VectorKeyframe<Vector4>? next, float diff, float span)
        {
            if (next is null || span.IsZero())
                return Vector4.Zero;
            return Interp.CubicBezierVelocity(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, diff / span);
        }
        public override Vector4 CubicBezierAccelerationOut(VectorKeyframe<Vector4>? next, float diff, float span)
        {
            if (next is null || span.IsZero())
                return Vector4.Zero;
            return Interp.CubicBezierAcceleration(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, diff / span);
        }

        public override string WriteToString()
        {
            return string.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11} {12} {13} {14} {15} {16} {17}", Second, InValue.X, InValue.Y, InValue.Z, InValue.W, OutValue.X, OutValue.Y, OutValue.Z, OutValue.W, InTangent.X, InTangent.Y, InTangent.Z, InTangent.W, OutTangent.X, OutTangent.Y, OutTangent.Z, OutTangent.W, InterpolationTypeOut);
            //return string.Format("{0} {1} {2} {3} {4} {5}", Second, InValue.WriteToString(), OutValue.WriteToString(), InTangent.WriteToString(), OutTangent.WriteToString(), InterpolationType);
        }
        public override void ReadFromString(string str)
        {
            string[] parts = str.Split(' ');
            Second = float.Parse(parts[0]);
            InValue = new Vector4(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]), float.Parse(parts[4]));
            OutValue = new Vector4(float.Parse(parts[5]), float.Parse(parts[6]), float.Parse(parts[7]), float.Parse(parts[8]));
            InTangent = new Vector4(float.Parse(parts[9]), float.Parse(parts[10]), float.Parse(parts[11]), float.Parse(parts[12]));
            OutTangent = new Vector4(float.Parse(parts[13]), float.Parse(parts[14]), float.Parse(parts[15]), float.Parse(parts[16]));
            InterpolationTypeOut = parts[17].AsEnum<EVectorInterpType>();
        }

        public override void MakeOutLinear()
        {
            var next = Next;
            float span;
            if (next is null)
            {
                if (OwningTrack != null && OwningTrack.FirstKey != this)
                {
                    next = OwningTrack.FirstKey as VectorKeyframe<Vector4>;
                    if (next is null)
                        return;
                    span = OwningTrack.LengthInSeconds - Second + next.Second;
                }
                else
                    return;
            }
            else
                span = next.Second - Second;
            OutTangent = (next.InValue - OutValue) / span;
        }

        public override void MakeInLinear()
        {
            var prev = Prev;
            float span;
            if (prev is null)
            {
                if (OwningTrack != null && OwningTrack.LastKey != this)
                {
                    prev = OwningTrack.LastKey as VectorKeyframe<Vector4>;
                    if (prev is null)
                        return;
                    span = OwningTrack.LengthInSeconds - prev.Second + Second;
                }
                else
                    return;
            }
            else
                span = Second - prev.Second;
            InTangent = (InValue - prev.OutValue) / span;
        }

        public override void UnifyTangents(EUnifyBias bias)
        {
            switch (bias)
            {
                case EUnifyBias.Average:
                    InTangent = -(OutTangent = (-InTangent + OutTangent) * 0.5f);
                    break;
                case EUnifyBias.In:
                    OutTangent = -InTangent;
                    break;
                case EUnifyBias.Out:
                    InTangent = -OutTangent;
                    break;
            }
        }

        public override void UnifyTangentDirections(EUnifyBias bias)
        {
            switch (bias)
            {
                case EUnifyBias.Average:
                    {
                        float inLength = InTangent.Length();
                        float outLength = OutTangent.Length();
                        Vector4 inTan = InTangent.Normalized();
                        Vector4 outTan = OutTangent.Normalized();
                        Vector4 avg = (-inTan + outTan) * 0.5f;
                        avg.Normalized();
                        InTangent = -avg * inLength;
                        OutTangent = avg * outLength;
                    }
                    break;
                case EUnifyBias.In:
                    {
                        float outLength = OutTangent.Length();
                        Vector4 inTan = InTangent.Normalized();
                        OutTangent = -inTan * outLength;
                    }
                    break;
                case EUnifyBias.Out:
                    {
                        float inLength = InTangent.Length();
                        Vector4 outTan = OutTangent.Normalized();
                        InTangent = -outTan * inLength;
                    }
                    break;
            }
        }

        public override void UnifyTangentMagnitudes(EUnifyBias bias)
        {
            switch (bias)
            {
                case EUnifyBias.Average:
                    {
                        float inLength = InTangent.Length();
                        float outLength = OutTangent.Length();
                        float avgLength = (inLength + outLength) * 0.5f;
                        Vector4 inTan = InTangent.Normalized();
                        Vector4 outTan = OutTangent.Normalized();
                        InTangent = inTan * avgLength;
                        OutTangent = outTan * avgLength;
                    }
                    break;
                case EUnifyBias.In:
                    {
                        float inLength = InTangent.Length();
                        Vector4 outTan = OutTangent.Normalized();
                        OutTangent = -outTan * inLength;
                        break;
                    }
                case EUnifyBias.Out:
                    {
                        float outLength = OutTangent.Length();
                        Vector4 inTan = InTangent.Normalized();
                        InTangent = -inTan * outLength;
                        break;
                    }
            }
        }

        public override void UnifyValues(EUnifyBias bias)
        {
            switch (bias)
            {
                case EUnifyBias.Average:
                    InValue = OutValue = (InValue + OutValue) * 0.5f;
                    break;
                case EUnifyBias.In:
                    OutValue = InValue;
                    break;
                case EUnifyBias.Out:
                    InValue = OutValue;
                    break;
            }
        }

        public override Vector4 LerpIn(VectorKeyframe<Vector4>? prev, float diff, float span)
        {
            if (prev is null || span.IsZero())
                return InValue;
            return Interp.Lerp(prev.OutValue, InValue, diff / span);
        }

        public override Vector4 LerpVelocityIn(VectorKeyframe<Vector4>? prev, float diff, float span)
            => (prev is null || span.IsZero()) ? Vector4.Zero : (InValue - prev.OutValue) / (diff / span);

        public override Vector4 CubicBezierIn(VectorKeyframe<Vector4>? prev, float diff, float span)
        {
            if (prev is null || span.IsZero())
                return InValue;
            return Interp.CubicBezier(prev.OutValue, prev.OutValue + prev.OutTangent * span, InValue + InTangent * span, InValue, diff / span);
        }

        public override Vector4 CubicBezierVelocityIn(VectorKeyframe<Vector4>? prev, float diff, float span)
        {
            if (prev is null || span.IsZero())
                return Vector4.Zero;
            return Interp.CubicBezierVelocity(prev.OutValue, prev.OutValue + prev.OutTangent * span, InValue + InTangent * span, InValue, diff / span);
        }

        public override Vector4 CubicBezierAccelerationIn(VectorKeyframe<Vector4>? prev, float diff, float span)
        {
            if (prev is null || span.IsZero())
                return Vector4.Zero;
            return Interp.CubicBezierAcceleration(prev.OutValue, prev.OutValue + prev.OutTangent * span, InValue + InTangent * span, InValue, diff / span);
        }

        public override Vector4 LerpValues(Vector4 a, Vector4 b, float t)
            => Vector4.Lerp(a, b, t);
    }
}
