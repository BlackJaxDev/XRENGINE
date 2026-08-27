using XREngine.Extensions;
using XREngine.Data;
using XREngine.Data.Animation;

namespace XREngine.Animation
{
    public class FloatKeyframe(float second, float inValue, float outValue, float inTangent, float outTangent, EVectorInterpType type) : VectorKeyframe<float>(second, inValue, outValue, inTangent, outTangent, type)
    {
        private const float DefaultTangentWeight = 1.0f / 3.0f;
        private const float BezierDerivativeEpsilon = 0.000001f;

        private EKeyframeWeightedMode _weightedMode;
        private float _inWeight = DefaultTangentWeight;
        private float _outWeight = DefaultTangentWeight;

        /// <summary>
        /// Identifies which tangent weights are authored. Unweighted handles use
        /// the canonical one-third Hermite-to-Bezier conversion.
        /// </summary>
        public EKeyframeWeightedMode WeightedMode
        {
            get => _weightedMode;
            set => SetField(ref _weightedMode, value);
        }

        /// <summary>Normalized duration of the incoming tangent handle.</summary>
        public float InWeight
        {
            get => _inWeight;
            set => SetField(ref _inWeight, SanitizeWeight(value));
        }

        /// <summary>Normalized duration of the outgoing tangent handle.</summary>
        public float OutWeight
        {
            get => _outWeight;
            set => SetField(ref _outWeight, SanitizeWeight(value));
        }

        public FloatKeyframe()
            : this(0.0f, 0.0f, 0.0f, EVectorInterpType.Linear) { }

        public FloatKeyframe(int frameIndex, float FPS, float inValue, float outValue, float inTangent, float outTangent, EVectorInterpType type)
            : this(GetSecondForAuthoredFrame(frameIndex, FPS), inValue, outValue, inTangent, outTangent, type)
            => TrySetAuthoredFrameIndex(frameIndex, FPS);
        public FloatKeyframe(int frameIndex, float FPS, float inoutValue, float inoutTangent, EVectorInterpType type)
            : this(GetSecondForAuthoredFrame(frameIndex, FPS), inoutValue, inoutTangent, type)
            => TrySetAuthoredFrameIndex(frameIndex, FPS);
        public FloatKeyframe(int frameIndex, float FPS, float inoutValue, float inTangent, float outTangent, EVectorInterpType type)
            : this(GetSecondForAuthoredFrame(frameIndex, FPS), inoutValue, inTangent, outTangent, type)
            => TrySetAuthoredFrameIndex(frameIndex, FPS);

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

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
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

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
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

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
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

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
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

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
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

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
            var (p1, p2, p3, p4) = GetBezierPointsWithPrev(prev, span);
            return Interp.CubicBezierAcceleration(p1, p2, p3, p4, t) / (span * span);
        }

        public override float CubicHermiteOut(VectorKeyframe<float>? next, float diff, float span)
        {
            if (span.IsZero())
                return OutValue;

            if (next is FloatKeyframe nextFloat && IsWeightedSegment(nextFloat))
                return EvaluateWeightedSegment(nextFloat, diff, span, EVectorValueType.Position);

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
            return Interp.CubicHermite(
                OutValue,
                OutTangent * span,
                -(next?.InTangent ?? 0.0f) * span,
                next?.InValue ?? OutValue,
                t);
        }

        public override float CubicHermiteVelocityOut(VectorKeyframe<float>? next, float diff, float span)
        {
            if (span.IsZero())
                return 0.0f;

            if (next is FloatKeyframe nextFloat && IsWeightedSegment(nextFloat))
                return EvaluateWeightedSegment(nextFloat, diff, span, EVectorValueType.Velocity);

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
            return Interp.CubicHermiteVelocity(
                OutValue,
                OutTangent * span,
                -(next?.InTangent ?? 0.0f) * span,
                next?.InValue ?? OutValue,
                t) / span;
        }

        public override float CubicHermiteAccelerationOut(VectorKeyframe<float>? next, float diff, float span)
        {
            if (span.IsZero())
                return 0.0f;

            if (next is FloatKeyframe nextFloat && IsWeightedSegment(nextFloat))
                return EvaluateWeightedSegment(nextFloat, diff, span, EVectorValueType.Acceleration);

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
            return Interp.CubicHermiteAcceleration(
                OutValue,
                OutTangent * span,
                -(next?.InTangent ?? 0.0f) * span,
                next?.InValue ?? OutValue,
                t) / (span * span);
        }

        public override float CubicHermiteIn(VectorKeyframe<float>? prev, float diff, float span)
        {
            if (span.IsZero())
                return InValue;

            if (prev is FloatKeyframe prevFloat && prevFloat.IsWeightedSegment(this))
                return prevFloat.EvaluateWeightedSegment(this, diff, span, EVectorValueType.Position);

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
            return Interp.CubicHermite(
                prev?.OutValue ?? InValue,
                (prev?.OutTangent ?? 0.0f) * span,
                -InTangent * span,
                InValue,
                t);
        }

        public override float CubicHermiteVelocityIn(VectorKeyframe<float>? prev, float diff, float span)
        {
            if (span.IsZero())
                return 0.0f;

            if (prev is FloatKeyframe prevFloat && prevFloat.IsWeightedSegment(this))
                return prevFloat.EvaluateWeightedSegment(this, diff, span, EVectorValueType.Velocity);

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
            return Interp.CubicHermiteVelocity(
                prev?.OutValue ?? InValue,
                (prev?.OutTangent ?? 0.0f) * span,
                -InTangent * span,
                InValue,
                t) / span;
        }

        public override float CubicHermiteAccelerationIn(VectorKeyframe<float>? prev, float diff, float span)
        {
            if (span.IsZero())
                return 0.0f;

            if (prev is FloatKeyframe prevFloat && prevFloat.IsWeightedSegment(this))
                return prevFloat.EvaluateWeightedSegment(this, diff, span, EVectorValueType.Acceleration);

            var t = Math.Clamp(diff / span, 0.0f, 1.0f);
            return Interp.CubicHermiteAcceleration(
                prev?.OutValue ?? InValue,
                (prev?.OutTangent ?? 0.0f) * span,
                -InTangent * span,
                InValue,
                t) / (span * span);
        }

        private bool IsWeightedSegment(FloatKeyframe next)
            => (WeightedMode & EKeyframeWeightedMode.Out) != 0
                || (next.WeightedMode & EKeyframeWeightedMode.In) != 0;

        /// <summary>
        /// Evaluates Unity-style weighted tangents as a two-dimensional cubic
        /// Bezier. Time is the x axis, so the Bezier parameter must be inverted
        /// before evaluating value or time derivatives.
        /// </summary>
        private float EvaluateWeightedSegment(
            FloatKeyframe next,
            float diff,
            float span,
            EVectorValueType valueType)
        {
            float normalizedTime = Math.Clamp(diff / span, 0.0f, 1.0f);
            float outWeight = (WeightedMode & EKeyframeWeightedMode.Out) != 0
                ? OutWeight
                : DefaultTangentWeight;
            float inWeight = (next.WeightedMode & EKeyframeWeightedMode.In) != 0
                ? next.InWeight
                : DefaultTangentWeight;

            float x1 = outWeight;
            float x2 = 1.0f - inWeight;
            float y0 = OutValue;
            float y1 = y0 + OutTangent * span * outWeight;
            float y3 = next.InValue;
            // Incoming tangents are stored with XRE's historical sign convention.
            float y2 = y3 + next.InTangent * span * inWeight;
            float parameter = InvertBezierTime(normalizedTime, x1, x2);

            if (valueType == EVectorValueType.Position)
                return EvaluateBezier(y0, y1, y2, y3, parameter);

            EvaluateBezierDerivatives(x1, x2, parameter, out float dx, out float ddx);
            EvaluateBezierDerivatives(y0, y1, y2, y3, parameter, out float dy, out float ddy);
            if (MathF.Abs(dx) <= BezierDerivativeEpsilon)
                return 0.0f;

            if (valueType == EVectorValueType.Velocity)
                return dy / (dx * span);

            return (ddy * dx - dy * ddx) / (dx * dx * dx * span * span);
        }

        private static float InvertBezierTime(float target, float x1, float x2)
        {
            if (target <= 0.0f || target >= 1.0f)
                return target;

            float lower = 0.0f;
            float upper = 1.0f;
            float parameter = target;
            for (int i = 0; i < 14; i++)
            {
                float value = EvaluateBezier(0.0f, x1, x2, 1.0f, parameter);
                float error = value - target;
                if (MathF.Abs(error) <= 0.0000005f)
                    break;

                if (error < 0.0f)
                    lower = parameter;
                else
                    upper = parameter;

                EvaluateBezierDerivatives(x1, x2, parameter, out float derivative, out _);
                float candidate = MathF.Abs(derivative) > BezierDerivativeEpsilon
                    ? parameter - error / derivative
                    : float.NaN;
                parameter = float.IsFinite(candidate) && candidate > lower && candidate < upper
                    ? candidate
                    : (lower + upper) * 0.5f;
            }

            return Math.Clamp(parameter, 0.0f, 1.0f);
        }

        private static float EvaluateBezier(float p0, float p1, float p2, float p3, float parameter)
        {
            float inverse = 1.0f - parameter;
            return inverse * inverse * inverse * p0
                + 3.0f * inverse * inverse * parameter * p1
                + 3.0f * inverse * parameter * parameter * p2
                + parameter * parameter * parameter * p3;
        }

        private static void EvaluateBezierDerivatives(
            float p1,
            float p2,
            float parameter,
            out float first,
            out float second)
            => EvaluateBezierDerivatives(0.0f, p1, p2, 1.0f, parameter, out first, out second);

        private static void EvaluateBezierDerivatives(
            float p0,
            float p1,
            float p2,
            float p3,
            float parameter,
            out float first,
            out float second)
        {
            float inverse = 1.0f - parameter;
            first = 3.0f * inverse * inverse * (p1 - p0)
                + 6.0f * inverse * parameter * (p2 - p1)
                + 3.0f * parameter * parameter * (p3 - p2);
            second = 6.0f * inverse * (p2 - 2.0f * p1 + p0)
                + 6.0f * parameter * (p3 - 2.0f * p2 + p1);
        }

        private static float SanitizeWeight(float value)
            => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : DefaultTangentWeight;

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
        /// This method is used internally by cubic Bezier interpolation functions.
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
            string[] parts = str.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            SyncInOutValues = false;
            SyncInOutTangentDirections = false;
            SyncInOutTangentMagnitudes = false;
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
                if (OwningTrack?.LoopsAfterLastKey == true && OwningTrack.FirstKey != this)
                {
                    next = OwningTrack.FirstKey as VectorKeyframe<float>;
                    span = OwningTrack.LengthInSeconds - Second + (next?.Second ?? 0.0f);
                }
                else
                    return;
            }
            else
                span = next.Second - Second;

            if (span.IsZero())
                return;

            OutTangent = ((next?.InValue ?? 0.0f) - OutValue) / span;
        }
        public override void MakeInLinear()
        {
            var prev = Prev;
            float span;
            if (prev is null)
            {
                if (OwningTrack?.LoopsBeforeFirstKey == true && OwningTrack.LastKey != this)
                {
                    prev = OwningTrack.LastKey as VectorKeyframe<float>;
                    span = OwningTrack.LengthInSeconds - (prev?.Second ?? 0.0f) + Second;
                }
                else
                    return;
            }
            else
                span = Second - (prev?.Second ?? 0.0f);

            if (span.IsZero())
                return;

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
