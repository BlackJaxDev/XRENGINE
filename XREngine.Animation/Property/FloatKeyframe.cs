using Extensions;
using XREngine.Data;
using XREngine.Data.Animation;

namespace XREngine.Animation
{
    public class FloatKeyframe(float second, float inValue, float outValue, float inTangent, float outTangent, EVectorInterpType type) : VectorKeyframe<float>(second, inValue, outValue, inTangent, outTangent, type)
    {
        public FloatKeyframe()
            : this(0.0f, 0.0f, 0.0f, EVectorInterpType.Linear) { }

        public FloatKeyframe(int frameIndex, float FPS, float inValue, float outValue, float inTangent, float outTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inValue, outValue, inTangent, outTangent, type) { }
        public FloatKeyframe(int frameIndex, float FPS, float inoutValue, float inoutTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inoutValue, inoutTangent, type) { }
        public FloatKeyframe(int frameIndex, float FPS, float inoutValue, float inTangent, float outTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inoutValue, inTangent, outTangent, type) { }

        public FloatKeyframe(float second, float inoutValue, float inoutTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public FloatKeyframe(float second, float inoutValue, float inTangent, float outTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inTangent, outTangent, type) { }

        public override float LerpOut(VectorKeyframe<float>? next, float diff, float span)
        {
            if (span.IsZero())
                return OutValue;

            var t = diff / span;
            t = Math.Clamp(t, 0.0f, 1.0f);
            return Interp.Lerp(OutValue, next?.InValue ?? OutValue, t);
        }
        public override float LerpVelocityOut(VectorKeyframe<float>? next, float diff, float span)
            => span.IsZero() ? 0.0f : ((next?.InValue ?? 0.0f) - OutValue) / (diff / span);

        public override float LerpIn(VectorKeyframe<float>? prev, float diff, float span)
            => Interp.Lerp(prev?.OutValue ?? 0.0f, InValue, span.IsZero() ? 0.0f : diff / span);
        public override float LerpVelocityIn(VectorKeyframe<float>? prev, float diff, float span)
            => span.IsZero() ? 0.0f : (InValue - (prev?.OutValue ?? 0.0f)) / (diff / span);

        /// <summary>
        /// Calculates the value of the cubic Bezier curve at the current keyframe.
        /// </summary>
        /// <param name="next"></param>
        /// <param name="diff"></param>
        /// <param name="span"></param>
        /// <returns></returns>
        public override float CubicBezierOut(VectorKeyframe<float>? next, float diff, float span)
        {
            if (span.IsZero())
                return OutValue;

            var t = diff / span;
            var (p1, p2, p3, p4) = GetBezierPointsWithNext(next, span);
            return Interp.CubicBezier(p1, p2, p3, p4, t);
        }

        /// <summary>
        /// Calculates the velocity of the cubic Bezier curve at the current keyframe.
        /// </summary>
        /// <param name="next"></param>
        /// <param name="diff"></param>
        /// <param name="span"></param>
        /// <returns></returns>
        public override float CubicBezierVelocityOut(VectorKeyframe<float>? next, float diff, float span)
        {
            if (span.IsZero())
                return 0.0f;

            var t = diff / span;
            var (p1, p2, p3, p4) = GetBezierPointsWithNext(next, span);
            return Interp.CubicBezierVelocity(p1, p2, p3, p4, t) / span;
        }

        /// <summary>
        /// Calculates the acceleration of the cubic Bezier curve at the current keyframe.
        /// </summary>
        /// <param name="next"></param>
        /// <param name="diff"></param>
        /// <param name="span"></param>
        /// <returns></returns>
        public override float CubicBezierAccelerationOut(VectorKeyframe<float>? next, float diff, float span)
        {
            if (span.IsZero())
                return 0.0f;

            var t = diff / span;
            var (p1, p2, p3, p4) = GetBezierPointsWithNext(next, span);
            return Interp.CubicBezierAcceleration(p1, p2, p3, p4, t) / (span * span);
        }

        /// <summary>
        /// Calculates the value of the cubic Bezier curve at the current keyframe.
        /// </summary>
        /// <param name="prev"></param>
        /// <param name="diff"></param>
        /// <param name="span"></param>
        /// <returns></returns>
        public override float CubicBezierIn(VectorKeyframe<float>? prev, float diff, float span)
        {
            if (span.IsZero())
                return 0.0f;

            var t = diff / span;
            var (p1, p2, p3, p4) = GetBezierPointsWithPrev(prev, span);
            return Interp.CubicBezier(p1, p2, p3, p4, t);
        }

        /// <summary>
        /// Calculates the velocity of the cubic Bezier curve at the current keyframe.
        /// </summary>
        /// <param name="prev"></param>
        /// <param name="diff"></param>
        /// <param name="span"></param>
        /// <returns></returns>
        public override float CubicBezierVelocityIn(VectorKeyframe<float>? prev, float diff, float span)
        {
            if (span.IsZero())
                return 0.0f;

            var t = diff / span;
            var (p1, p2, p3, p4) = GetBezierPointsWithPrev(prev, span);
            return Interp.CubicBezierVelocity(p1, p2, p3, p4, t) / span;
        }

        /// <summary>
        /// Calculates the acceleration of the cubic Bezier curve at the current keyframe.
        /// </summary>
        /// <param name="prev"></param>
        /// <param name="diff"></param>
        /// <param name="span"></param>
        /// <returns></returns>
        public override float CubicBezierAccelerationIn(VectorKeyframe<float>? prev, float diff, float span)
        {
            if (span.IsZero())
                return 0.0f;

            var t = diff / span;
            var (p1, p2, p3, p4) = GetBezierPointsWithPrev(prev, span);
            return Interp.CubicBezierAcceleration(p1, p2, p3, p4, t) / (span * span);
        }

        /// <summary>
        /// Calculates and returns the four control points needed for cubic Bezier interpolation 
        /// between this keyframe and the next keyframe.
        /// </summary>
        /// <param name="next">The next keyframe in the sequence. If null, this keyframe's values are used.</param>
        /// <param name="span">The time span between the current keyframe and the next keyframe.</param>
        /// <returns>
        /// A tuple containing the four control points (p1, p2, p3, p4) where:
        /// - p1: Starting point (current keyframe's OutValue)
        /// - p2: First control point based on current keyframe's OutTangent
        /// - p3: Second control point based on next keyframe's InTangent
        /// - p4: End point (next keyframe's InValue or current OutValue if next is null)
        /// </returns>
        /// <remarks>
        /// The control points are calculated using the standard cubic Bezier formula where:
        /// - The first and last points (p1, p4) represent the actual keyframe values
        /// - The middle points (p2, p3) are calculated using the tangent values scaled by the time span
        /// This method is used internally by cubic Bezier interpolation functions
        /// </remarks>
        private (float p1, float p2, float p3, float p4) GetBezierPointsWithNext(VectorKeyframe<float>? next, float span)
        {
            float nextInValue = next?.InValue ?? OutValue;
            return (
                OutValue,
                OutValue + OutTangent * span,
                nextInValue + (next?.InTangent ?? 0.0f) * span,
                nextInValue
            );
        }

        /// <summary>
        /// Calculates and returns the four control points needed for cubic Bezier interpolation
        /// between this keyframe and the previous keyframe.
        /// </summary>
        /// <param name="prev">The previous keyframe in the sequence. If null, this keyframe's values are used.</param>
        /// <param name="span">The time span between the previous keyframe and the current keyframe.</param>
        /// <returns>
        /// A tuple containing the four control points (p1, p2, p3, p4) where:
        /// - p1: Starting point (previous keyframe's OutValue or current InValue if prev is null)
        /// - p2: First control point based on previous keyframe's OutTangent
        /// - p3: Second control point based on current keyframe's InTangent
        /// - p4: End point (current keyframe's InValue)
        /// </returns>
        /// <remarks>
        /// The control points are calculated using the standard cubic Bezier formula where:
        /// - The first and last points (p1, p4) represent the actual keyframe values
        /// - The middle points (p2, p3) are calculated using the tangent values scaled by the time span
        /// This method is used internally by cubic Bezier interpolation functions
        private (float p1, float p2, float p3, float p4) GetBezierPointsWithPrev(VectorKeyframe<float>? prev, float span)
        {
            float prevOutValue = prev?.OutValue ?? InValue;
            return (
                prevOutValue,
                prevOutValue + (prev?.OutTangent ?? 0.0f) * span,
                InValue + InTangent * span,
                InValue
            );
        }

        public override string WriteToString()
            => $"{Second} {InValue} {OutValue} {InTangent} {OutTangent} {InterpolationTypeIn} {InterpolationTypeOut}";

        public override string ToString()
            => $"[S:{Second}] V:({InValue} {OutValue}) T:([{InTangent} {InterpolationTypeIn}] [{OutTangent} {InterpolationTypeOut}])";

        public override void ReadFromString(string str)
        {
            string[] parts = str.Split(' ');
            Second = float.Parse(parts[0]);
            InValue = float.Parse(parts[1]);
            OutValue = float.Parse(parts[2]);
            InTangent = float.Parse(parts[3]);
            OutTangent = float.Parse(parts[4]);
            InterpolationTypeIn = parts[5].AsEnum<EVectorInterpType>();
            InterpolationTypeOut = parts[6].AsEnum<EVectorInterpType>();
        }

        public override void MakeOutLinear()
        {
            VectorKeyframe<float>? next = Next;
            float span;
            if (next is null)
            {
                if (OwningTrack != null && OwningTrack.FirstKey != this)
                {
                    next = OwningTrack?.FirstKey as VectorKeyframe<float>;
                    span = (OwningTrack?.LengthInSeconds ?? 0.0f) - Second + (next?.Second ?? 0.0f);
                }
                else
                    return;
            }
            else
                span = next.Second - Second;

            OutTangent = ((next?.InValue ?? 0.0f) - OutValue) / span;
        }
        public override void MakeInLinear()
        {
            var prev = Prev;
            float span;
            if (prev is null)
            {
                if (OwningTrack != null && OwningTrack.LastKey != this)
                {
                    prev = OwningTrack?.LastKey as VectorKeyframe<float>;
                    span = (OwningTrack?.LengthInSeconds ?? 0.0f) - (prev?.Second ?? 0.0f) + Second;
                }
                else
                    return;
            }
            else
                span = Second - (prev?.Second ?? 0.0f);

            InTangent = -(InValue - (prev?.OutValue ?? 0.0f)) / span;
        }

        public override void UnifyTangentDirections(EUnifyBias bias) => UnifyTangents(bias);
        public override void UnifyTangentMagnitudes(EUnifyBias bias) => UnifyTangents(bias);

        public override void UnifyTangents(EUnifyBias bias)
        {
            switch (bias)
            {
                case EUnifyBias.Average:
                    float avg = (-InTangent + OutTangent) * 0.5f;
                    OutTangent = avg;
                    InTangent = -avg;
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
                    InValue = OutValue = (InValue + OutValue) / 2.0f;
                    break;
                case EUnifyBias.In:
                    OutValue = InValue;
                    break;
                case EUnifyBias.Out:
                    InValue = OutValue;
                    break;
            }
        }

        /// <summary>
        /// Generates the tangents for this keyframe based on the surrounding keyframes.
        /// </summary>
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

                OutTangent = tangent;
                InTangent = -tangent;
            }
            else
            {
                if (prev != null && prevSpan > 0.0f)
                {
                    InTangent = -(InValue - prev.OutValue) / prevSpan;
                }
                if (next != null && nextSpan > 0.0f)
                {
                    OutTangent = (next.InValue - OutValue) / nextSpan;
                }
            }
        }
        public void GenerateOutTangent()
        {
            var next = GetNextKeyframe(out float nextSpan);
            if (next != null && nextSpan > 0.0f)
            {
                OutTangent = (next.InValue - OutValue) / nextSpan;
            }
        }
        public void GenerateInTangent()
        {
            var prev = GetPrevKeyframe(out float prevSpan);
            if (prev != null && prevSpan > 0.0f)
            {
                InTangent = -(InValue - prev.OutValue) / prevSpan;
            }
        }
        public void GenerateAdjacentTangents(bool prev, bool next)
        {
            if (prev)
            {
                var prevkf = GetPrevKeyframe(out float span2) as FloatKeyframe;
                prevkf?.GenerateTangents();
                GenerateInTangent();
            }
            if (next)
            {
                var nextKf = GetNextKeyframe(out float span1) as FloatKeyframe;
                nextKf?.GenerateTangents();
                GenerateOutTangent();
            }
        }

        public override float LerpValues(float a, float b, float t)
            => a + (b - a) * t;
    }
}
