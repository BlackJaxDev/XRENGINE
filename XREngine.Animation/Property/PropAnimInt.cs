using Extensions;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Animation;

namespace XREngine.Animation
{
    public class PropAnimInt : PropAnimVector<int, IntKeyframe>, IEnumerable<IntKeyframe>
    {
        public PropAnimInt() : base() { }
        public PropAnimInt(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes) { }
        public PropAnimInt(int frameCount, float FPS, bool looped, bool useKeyframes)
            : base(frameCount, FPS, looped, useKeyframes) { }

        //public bool UseConstantVelocity { get; set; } = true;
        //public float ConstantVelocitySpeed { get; set; } = 1.0f;
        
        protected override int LerpValues(int t1, int t2, float time) => (int)Interp.Lerp(t1, t2, time);
        protected override float[] GetComponents(int value) => [value];
        protected override int GetMaxValue() => int.MaxValue;
        protected override int GetMinValue() => int.MinValue;
        protected override float GetVelocityMagnitude()
        {
            float b = CurrentVelocity;
            float a = 1.0f;
            Vector2 start = new(0.0f, 0.0f);
            Vector2 end = new(a, b);
            return start.Distance(end);
        }
    }
    public class IntKeyframe : VectorKeyframe<int>
    {
        public IntKeyframe()
            : this(0.0f, 0, 0, EVectorInterpType.Smooth) { }
        public IntKeyframe(int frameIndex, float FPS, int inValue, int outValue, int inTangent, int outTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inValue, outValue, inTangent, outTangent, type) { }
        public IntKeyframe(int frameIndex, float FPS, int inoutValue, int inoutTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public IntKeyframe(float second, int inoutValue, int inoutTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public IntKeyframe(float second, int inoutValue, int inTangent, int outTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inTangent, outTangent, type) { }
        public IntKeyframe(float second, int inValue, int outValue, int inTangent, int outTangent, EVectorInterpType type)
            : base(second, inValue, outValue, inTangent, outTangent, type) { }
        
        public override int LerpOut(VectorKeyframe<int>? next, float diff, float span)
            => next is null || span.IsZero()
                ? OutValue
                : (int)Interp.Lerp(OutValue, next.InValue, diff / span);
        public override int LerpVelocityOut(VectorKeyframe<int>? next, float diff, float span)
            => next is null || span.IsZero()
                ? 0
                : (int)((next.InValue - OutValue) / (diff / span));

        public override int LerpIn(VectorKeyframe<int>? prev, float diff, float span)
            => (int)Interp.Lerp(prev?.OutValue ?? InValue, InValue, span.IsZero() ? 0.0f : diff / span);
        public override int LerpVelocityIn(VectorKeyframe<int>? prev, float diff, float span)
            => span.IsZero() ? 0 : (int)((InValue - (prev?.OutValue ?? InValue)) / (diff / span));

        public override int CubicBezierOut(VectorKeyframe<int>? next, float diff, float span)
            => next is null || span.IsZero()
                ? OutValue
                : (int)Interp.CubicBezier(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, diff / span);
        public override int CubicBezierVelocityOut(VectorKeyframe<int>? next, float diff, float span)
            => next is null || span.IsZero()
                ? 0
                : (int)Interp.CubicBezierVelocity(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, diff / span);
        public override int CubicBezierAccelerationOut(VectorKeyframe<int>? next, float diff, float span)
            => next is null || span.IsZero()
                ? 0
                : (int)Interp.CubicBezierAcceleration(OutValue, OutValue + OutTangent * span, next.InValue + next.InTangent * span, next.InValue, diff / span);


        public override int CubicBezierIn(VectorKeyframe<int>? prev, float diff, float span)
        {
            if (span.IsZero())
                return InValue;

            int prevOut = prev?.OutValue ?? InValue;
            int prevTan = prev?.OutTangent ?? 0;
            return (int)Interp.CubicBezier(prevOut, prevOut + prevTan * span, InValue + InTangent * span, InValue, diff / span);
        }

        public override int CubicBezierVelocityIn(VectorKeyframe<int>? prev, float diff, float span)
        {
            if (span.IsZero())
                return 0;

            int prevOut = prev?.OutValue ?? InValue;
            int prevTan = prev?.OutTangent ?? 0;
            return (int)Interp.CubicBezierVelocity(prevOut, prevOut + prevTan * span, InValue + InTangent * span, InValue, diff / span);
        }

        public override int CubicBezierAccelerationIn(VectorKeyframe<int>? prev, float diff, float span)
        {
            if (span.IsZero())
                return 0;

            int prevOut = prev?.OutValue ?? InValue;
            int prevTan = prev?.OutTangent ?? 0;
            return (int)Interp.CubicBezierAcceleration(prevOut, prevOut + prevTan * span, InValue + InTangent * span, InValue, diff / span);
        }

        public override int LerpValues(int a, int b, float t)
        {
            return (int)Interp.Lerp(a, b, t);
        }

        public override string WriteToString()
            => string.Format("{0} {1} {2} {3} {4} {5}", Second, InValue.ToString(), OutValue.ToString(), InTangent.ToString(), OutTangent.ToString(), InterpolationTypeOut);

        public override string ToString()
            => InterpolationTypeOut switch
            {
                EVectorInterpType.Step => string.Format("[F:{0} : {3}] V:({1} {2})", Second, InValue.ToString(), OutValue.ToString(), InterpolationTypeOut),
                EVectorInterpType.Linear => string.Format("[F:{0} : {3}] V:({1} {2})", Second, InValue.ToString(), OutValue.ToString(), InterpolationTypeOut),
                _ => string.Format("[F:{0} : {5}] V:({1} {2}) T:({3} {4})", Second, InValue.ToString(), OutValue.ToString(), InTangent.ToString(), OutTangent.ToString(), InterpolationTypeOut),
            };

        public override void ReadFromString(string str)
        {
            string[] parts = str.Split(' ');
            Second = float.Parse(parts[0]);
            InValue = int.Parse(parts[1]);
            OutValue = int.Parse(parts[2]);
            InTangent = int.Parse(parts[3]);
            OutTangent = int.Parse(parts[4]);
            InterpolationTypeOut = parts[5].AsEnum<EVectorInterpType>();
        }

        public override void MakeOutLinear()
        {
            var next = Next;
            float span;
            if (next is null)
            {
                if (OwningTrack?.FirstKey is VectorKeyframe<int> first && first != this)
                {
                    next = first;
                    span = OwningTrack.LengthInSeconds - Second + next.Second;
                }
                else
                    return;
            }
            else
                span = next.Second - Second;

            if (span.IsZero())
                return;

            OutTangent = (int)((next.InValue - OutValue) / span);
        }
        public override void MakeInLinear()
        {
            var prev = Prev;
            float span;
            if (prev is null)
            {
                if (OwningTrack?.LastKey is VectorKeyframe<int> last && last != this)
                {
                    prev = last;
                    span = OwningTrack.LengthInSeconds - prev.Second + Second;
                }
                else
                    return;
            }
            else
                span = Second - prev.Second;

            if (span.IsZero())
                return;

            InTangent = (int)(-(InValue - prev.OutValue) / span);
        }

        public override void UnifyTangentDirections(EUnifyBias bias) => UnifyTangents(bias);
        public override void UnifyTangentMagnitudes(EUnifyBias bias) => UnifyTangents(bias);

        public override void UnifyTangents(EUnifyBias bias)
        {
            switch (bias)
            {
                case EUnifyBias.Average:
                    float avg = (-InTangent + OutTangent) * 0.5f;
                    OutTangent = (int)avg;
                    InTangent = (int)-avg;
                    break;
                case EUnifyBias.In:
                    OutTangent = -InTangent;
                    break;
                case EUnifyBias.Out:
                    InTangent = -OutTangent;
                    break;
            }
        }
        public override void UnifyValues(EUnifyBias bias)
        {
            switch (bias)
            {
                case EUnifyBias.Average:
                    InValue = OutValue = (int)((InValue + OutValue) / 2.0f);
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
            var next = GetNextKeyframe(out float nextSpan);
            var prev = GetPrevKeyframe(out float prevSpan);

            if (Math.Abs(InValue - OutValue) < 0.0001f)
            {
                float tangent = 0.0f;
                float weightCount = 0;
                if (prev != null && prevSpan > 0.0f)
                {
                    tangent += (InValue - prev.OutValue) / prevSpan;
                    weightCount++;
                }
                if (next != null && nextSpan > 0.0f)
                {
                    tangent += (next.InValue - OutValue) / nextSpan;
                    weightCount++;
                }

                if (weightCount > 0)
                    tangent /= weightCount;

                OutTangent = (int)tangent;
                InTangent = (int)-tangent;
            }
            else
            {
                if (prev != null && prevSpan > 0.0f)
                {
                    InTangent = (int)(-(InValue - prev.OutValue) / prevSpan);
                }
                if (next != null && nextSpan > 0.0f)
                {
                    OutTangent = (int)((next.InValue - OutValue) / nextSpan);
                }
            }

            //float valueDiff = (next?.InValue ?? InValue) - (prev?.OutValue ?? OutValue);
            //float secDiff = (next?.Second ?? Second) - (prev?.Second ?? Second);
            //if (secDiff != 0.0f)
            //    InTangent = -(OutTangent = valueDiff / secDiff);
        }
        public void GenerateOutTangent()
        {
            var next = GetNextKeyframe(out float nextSpan);
            if (next != null && nextSpan > 0.0f)
            {
                OutTangent = (int)((next.InValue - OutValue) / nextSpan);
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
                InTangent = (int)(-(InValue - prev.OutValue) / prevSpan);
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
                var prevkf = GetPrevKeyframe(out float span2) as IntKeyframe;
                prevkf?.GenerateOutTangent();
                GenerateInTangent();
            }
            if (next)
            {
                var nextKf = GetNextKeyframe(out float span1) as IntKeyframe;
                nextKf?.GenerateInTangent();
                GenerateOutTangent();
            }
        }
    }
}
