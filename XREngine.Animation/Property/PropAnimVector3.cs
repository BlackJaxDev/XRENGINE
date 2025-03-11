using Extensions;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Animation;

namespace XREngine.Animation
{
    public class PropAnimVector3 : PropAnimVector<Vector3, Vector3Keyframe>
    {
        public PropAnimVector3() : base() { }
        public PropAnimVector3(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes) { }
        public PropAnimVector3(int frameCount, float FPS, bool looped, bool useKeyframes)
            : base(frameCount, FPS, looped, useKeyframes) { }

        protected override Vector3 LerpValues(Vector3 t1, Vector3 t2, float time) => Vector3.Lerp(t1, t2, time);
        protected override float[] GetComponents(Vector3 value) => [value.X, value.Y, value.Z];
        protected override Vector3 GetMaxValue() => new(float.MaxValue);
        protected override Vector3 GetMinValue() => new(float.MinValue);
        protected override float GetVelocityMagnitude()
        {
            Vector3 b = CurrentVelocity;
            float a = 1.0f;
            Vector4 start = Vector4.Zero;
            Vector4 end = new(a, b.X, b.Y, b.Z);
            return Vector4.Distance(start, end);
        }
    }
    public class Vector3Keyframe : VectorKeyframe<Vector3>
    {
        public Vector3Keyframe()
            : this(0.0f, Vector3.Zero, Vector3.Zero, EVectorInterpType.Smooth) { }
        public Vector3Keyframe(int frameIndex, float FPS, Vector3 inValue, Vector3 outValue, Vector3 inTangent, Vector3 outTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inValue, outValue, inTangent, outTangent, type) { }
        public Vector3Keyframe(int frameIndex, float FPS, Vector3 inoutValue, Vector3 inoutTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public Vector3Keyframe(float second, Vector3 inoutValue, Vector3 inoutTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public Vector3Keyframe(float second, Vector3 inoutValue, Vector3 inTangent, Vector3 outTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inTangent, outTangent, type) { }
        public Vector3Keyframe(float second, Vector3 inValue, Vector3 outValue, Vector3 inTangent, Vector3 outTangent, EVectorInterpType type)
            : base(second, inValue, outValue, inTangent, outTangent, type) { }
        
        public override Vector3 LerpOut(VectorKeyframe<Vector3> next, float diff, float span)
          => Interp.Lerp(OutValue, next.InValue, span.IsZero() ? 0.0f : diff / span);
        public override Vector3 LerpVelocityOut(VectorKeyframe<Vector3> next, float diff, float span)
            => span.IsZero() ? Vector3.Zero : (next.InValue - OutValue) / (diff / span);

        public override Vector3 CubicBezierOut(VectorKeyframe<Vector3> next, float diff, float span)
            => Interp.CubicBezier(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, span.IsZero() ? 0.0f : diff / span);
        public override Vector3 CubicBezierVelocityOut(VectorKeyframe<Vector3> next, float diff, float span)
            => Interp.CubicBezierVelocity(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, span.IsZero() ? 0.0f : diff / span);
        public override Vector3 CubicBezierAccelerationOut(VectorKeyframe<Vector3> next, float diff, float span)
            => Interp.CubicBezierAcceleration(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, span.IsZero() ? 0.0f : diff / span);

        public override string WriteToString()
        {
            return string.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10} {11} {12} {13}", Second, InValue.X, InValue.Y, InValue.Z, OutValue.X, OutValue.Y, OutValue.Z, InTangent.X, InTangent.Y, InTangent.Z, OutTangent.X, OutTangent.Y, OutTangent.Z, InterpolationTypeOut);
            //return string.Format("{0} {1} {2} {3} {4} {5}", Second, InValue.WriteToString(), OutValue.WriteToString(), InTangent.WriteToString(), OutTangent.WriteToString(), InterpolationType);
        }
        public override void ReadFromString(string str)
        {
            string[] parts = str.Split(' ');
            Second = float.Parse(parts[0]);
            InValue = new Vector3(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]));
            OutValue = new Vector3(float.Parse(parts[4]), float.Parse(parts[5]), float.Parse(parts[6]));
            InTangent = new Vector3(float.Parse(parts[7]), float.Parse(parts[8]), float.Parse(parts[9]));
            OutTangent = new Vector3(float.Parse(parts[10]), float.Parse(parts[11]), float.Parse(parts[12]));
            InterpolationTypeOut = parts[13].AsEnum<EVectorInterpType>();
        }

        public override void MakeOutLinear()
        {
            var next = Next;
            float span;
            if (next is null)
            {
                if (OwningTrack != null && OwningTrack.FirstKey != this)
                {
                    next = (VectorKeyframe<Vector3>)OwningTrack.FirstKey;
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
                    prev = (VectorKeyframe<Vector3>)OwningTrack.LastKey;
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
                        Vector3 inTan = InTangent.Normalized();
                        Vector3 outTan = OutTangent.Normalized();
                        Vector3 avg = (-inTan + outTan) * 0.5f;
                        avg.Normalized();
                        InTangent = -avg * inLength;
                        OutTangent = avg * outLength;
                    }
                    break;
                case EUnifyBias.In:
                    {
                        float outLength = OutTangent.Length();
                        Vector3 inTan = InTangent.Normalized();
                        OutTangent = -inTan * outLength;
                    }
                    break;
                case EUnifyBias.Out:
                    {
                        float inLength = InTangent.Length();
                        Vector3 outTan = OutTangent.Normalized();
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
                        Vector3 inTan = InTangent.Normalized();
                        Vector3 outTan = OutTangent.Normalized();
                        InTangent = inTan * avgLength;
                        OutTangent = outTan * avgLength;
                    }
                    break;
                case EUnifyBias.In:
                    {
                        float inLength = InTangent.Length();
                        Vector3 outTan = OutTangent.Normalized();
                        OutTangent = -outTan * inLength;
                        break;
                    }
                case EUnifyBias.Out:
                    {
                        float outLength = OutTangent.Length();
                        Vector3 inTan = InTangent.Normalized();
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

        public override Vector3 LerpIn(VectorKeyframe<Vector3>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector3 LerpVelocityIn(VectorKeyframe<Vector3>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector3 CubicBezierIn(VectorKeyframe<Vector3>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector3 CubicBezierVelocityIn(VectorKeyframe<Vector3>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector3 CubicBezierAccelerationIn(VectorKeyframe<Vector3>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector3 LerpValues(Vector3 a, Vector3 b, float t)
        {
            throw new NotImplementedException();
        }
    }
}
