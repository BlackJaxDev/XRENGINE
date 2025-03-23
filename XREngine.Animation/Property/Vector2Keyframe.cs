using Extensions;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Animation;

namespace XREngine.Animation
{
    public class Vector2Keyframe(float second, Vector2 inValue, Vector2 outValue, Vector2 inTangent, Vector2 outTangent, EVectorInterpType type) : VectorKeyframe<Vector2>(second, inValue, outValue, inTangent, outTangent, type)
    {
        public Vector2Keyframe()
            : this(0.0f, Vector2.Zero, Vector2.Zero, EVectorInterpType.Smooth) { }
        public Vector2Keyframe(int frameIndex, float FPS, Vector2 inValue, Vector2 outValue, Vector2 inTangent, Vector2 outTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inValue, outValue, inTangent, outTangent, type) { }
        public Vector2Keyframe(int frameIndex, float FPS, Vector2 inoutValue, Vector2 inoutTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public Vector2Keyframe(float second, Vector2 inoutValue, Vector2 inoutTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public Vector2Keyframe(float second, Vector2 inoutValue, Vector2 inTangent, Vector2 outTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inTangent, outTangent, type) { }

        public override Vector2 LerpOut(VectorKeyframe<Vector2>? next, float diff, float span)
          => Interp.Lerp(OutValue, next.InValue, span.IsZero() ? 0.0f : diff / span);
        public override Vector2 LerpVelocityOut(VectorKeyframe<Vector2>? next, float diff, float span)
            => span.IsZero() ? Vector2.Zero : (next.InValue - OutValue) / (diff / span);

        public override Vector2 CubicBezierOut(VectorKeyframe<Vector2>? next, float diff, float span)
            => Interp.CubicBezier(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, span.IsZero() ? 0.0f : diff / span);
        public override Vector2 CubicBezierVelocityOut(VectorKeyframe<Vector2>? next, float diff, float span)
            => Interp.CubicBezierVelocity(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, span.IsZero() ? 0.0f : diff / span);
        public override Vector2 CubicBezierAccelerationOut(VectorKeyframe<Vector2>? next, float diff, float span)
            => Interp.CubicBezierAcceleration(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, span.IsZero() ? 0.0f : diff / span);

        public override string WriteToString()
        {
            return string.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9}", Second, InValue.X, InValue.Y, OutValue.X, OutValue.Y, InTangent.X, InTangent.Y, OutTangent.X, OutTangent.Y, InterpolationTypeOut);
            //return string.Format("{0} {1} {2} {3} {4} {5}", Second, InValue.WriteToString(), OutValue.WriteToString(), InTangent.WriteToString(), OutTangent.WriteToString(), InterpolationType);
        }
        public override void ReadFromString(string str)
        {
            string[] parts = str.Split(' ');
            Second = float.Parse(parts[0]);
            InValue = new Vector2(float.Parse(parts[1]), float.Parse(parts[2]));
            OutValue = new Vector2(float.Parse(parts[3]), float.Parse(parts[4]));
            InTangent = new Vector2(float.Parse(parts[5]), float.Parse(parts[6]));
            OutTangent = new Vector2(float.Parse(parts[7]), float.Parse(parts[8]));
            InterpolationTypeOut = parts[9].AsEnum<EVectorInterpType>();
        }

        public override void MakeOutLinear()
        {
            var next = Next;
            float span;
            if (next is null)
            {
                if (OwningTrack != null && OwningTrack.FirstKey != this)
                {
                    next = (VectorKeyframe<Vector2>)OwningTrack.FirstKey;
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
                    prev = (VectorKeyframe<Vector2>)OwningTrack.LastKey;
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
                        Vector2 inTan = InTangent.Normalize();
                        Vector2 outTan = OutTangent.Normalize();
                        Vector2 avg = (-inTan + outTan) * 0.5f;
                        avg.Normalize();
                        InTangent = -avg * inLength;
                        OutTangent = avg * outLength;
                    }
                    break;
                case EUnifyBias.In:
                    {
                        float outLength = OutTangent.Length();
                        Vector2 inTan = InTangent.Normalize();
                        OutTangent = -inTan * outLength;
                    }
                    break;
                case EUnifyBias.Out:
                    {
                        float inLength = InTangent.Length();
                        Vector2 outTan = OutTangent.Normalize();
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
                        Vector2 inTan = InTangent.Normalize();
                        Vector2 outTan = OutTangent.Normalize();
                        InTangent = inTan * avgLength;
                        OutTangent = outTan * avgLength;
                    }
                    break;
                case EUnifyBias.In:
                    {
                        float inLength = InTangent.Length();
                        Vector2 outTan = OutTangent.Normalize();
                        OutTangent = -outTan * inLength;
                        break;
                    }
                case EUnifyBias.Out:
                    {
                        float outLength = OutTangent.Length();
                        Vector2 inTan = InTangent.Normalize();
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
        public void GenerateTangents()
        {
            //var next = GetNextKeyframe(out float nextSpan);
            //var prev = GetPrevKeyframe(out float prevSpan);

            //if (Math.Abs(InValue - OutValue) < 0.0001f)
            //{
            //    float tangent = 0.0f;
            //    float weightCount = 0;
            //    if (prev != null && prevSpan > 0.0f)
            //    {
            //        tangent += (InValue - prev.OutValue) / prevSpan;
            //        weightCount++;
            //    }
            //    if (next != null && nextSpan > 0.0f)
            //    {
            //        tangent += (next.InValue - OutValue) / nextSpan;
            //        weightCount++;
            //    }

            //    if (weightCount > 0)
            //        tangent /= weightCount;

            //    OutTangent = tangent;
            //    InTangent = -tangent;
            //}
            //else
            //{
            //    if (prev != null && prevSpan > 0.0f)
            //    {
            //        InTangent = -(InValue - prev.OutValue) / prevSpan;
            //    }
            //    if (next != null && nextSpan > 0.0f)
            //    {
            //        OutTangent = (next.InValue - OutValue) / nextSpan;
            //    }
            //}
        }
        public void GenerateOutTangent()
        {
            var next = GetNextKeyframe(out float nextSpan);
            if (next != null && nextSpan > 0.0f)
            {
                OutTangent = (next.InValue - OutValue) / nextSpan;
            }

            //var next = GetNextKeyframe(out float span1);
            //var prev = GetPrevKeyframe(out float span2);
            //float valueDiff = (next?.InValue ?? InValue) - (prev?.OutValue ?? OutValue);
            //float secDiff = (next?.Second ?? Second) - (prev?.Second ?? Second);
            //if (secDiff != 0.0f)
            //    OutTangent = valueDiff / secDiff;
        }
        public void GenerateInTangent()
        {
            var prev = GetPrevKeyframe(out float prevSpan);
            if (prev != null && prevSpan > 0.0f)
            {
                InTangent = -(InValue - prev.OutValue) / prevSpan;
            }

            //var next = GetNextKeyframe(out float span1);
            //var prev = GetPrevKeyframe(out float span2);
            //float valueDiff = (next?.InValue ?? InValue) - (prev?.OutValue ?? OutValue);
            //float secDiff = (next?.Second ?? Second) - (prev?.Second ?? Second);
            //if (secDiff != 0.0f)
            //    InTangent = -valueDiff / secDiff;
        }
        public void GenerateAdjacentTangents(bool prev, bool next)
        {
            if (prev)
            {
                var prevkf = GetPrevKeyframe(out float span2) as Vector2Keyframe;
                prevkf?.GenerateTangents();
                GenerateInTangent();
            }
            if (next)
            {
                var nextKf = GetNextKeyframe(out float span1) as Vector2Keyframe;
                nextKf?.GenerateTangents();
                GenerateOutTangent();
            }
        }

        public override Vector2 LerpIn(VectorKeyframe<Vector2>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector2 LerpVelocityIn(VectorKeyframe<Vector2>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector2 CubicBezierIn(VectorKeyframe<Vector2>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector2 CubicBezierVelocityIn(VectorKeyframe<Vector2>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector2 CubicBezierAccelerationIn(VectorKeyframe<Vector2>? prev, float diff, float span)
        {
            throw new NotImplementedException();
        }

        public override Vector2 LerpValues(Vector2 a, Vector2 b, float t)
        {
            throw new NotImplementedException();
        }
    }
}
