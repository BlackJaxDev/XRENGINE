using Extensions;
using System.ComponentModel;
using XREngine.Data;
using XREngine.Data.Animation;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public abstract class PropAnimVector<TValue, TValueKey> : PropAnimKeyframed<TValueKey>
        where TValue : unmanaged
        where TValueKey : VectorKeyframe<TValue>, new()
    {
        protected const string VectorAnimCategory = "Vector Animation";

        public PropAnimVector()
            : base(0.0f, false)
            => _getValue = ChooseValueGetter();

        public PropAnimVector(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes)
            => _getValue = ChooseValueGetter();

        public PropAnimVector(int frameCount, float framesPerSecond, bool looped, bool useKeyframes)
            : base(frameCount, framesPerSecond, looped, useKeyframes)
            => _getValue = ChooseValueGetter();

        private DelGetValue<TValue> ChooseValueGetter()
            => !IsBaked ? GetValueKeyframed : GetValueBakedBySecond;

        public event Action<PropAnimVector<TValue, TValueKey>>? DefaultValueChanged;
        public event Action<PropAnimVector<TValue, TValueKey>>? ConstrainKeyframedFPSChanged;
        public event Action<PropAnimVector<TValue, TValueKey>>? LerpConstrainedFPSChanged;

        public event Action<PropAnimVector<TValue, TValueKey>>? CurrentPositionChanged;
        public event Action<PropAnimVector<TValue, TValueKey>>? CurrentVelocityChanged;
        public event Action<PropAnimVector<TValue, TValueKey>>? CurrentAccelerationChanged;

        private DelGetValue<TValue> _getValue;
        private TValue _defaultValue = new();
        private bool _constrainKeyframedFPS = false;
        private bool _lerpConstrainedFPS = false;
        private VectorKeyframe<TValue>? _prevKeyframe;

        private TValue _currentPosition;
        private TValue _currentVelocity;
        private TValue _currentAcceleration;

        private TValue[]? _baked;

        /// <summary>
        /// If true, speed calculated relative to the current tangent rather than multiplied directly with the current velocity (change in position).
        /// </summary>
        public bool UseTangentRelativeSpeed { get; set; } = false;
        /// <summary>
        /// The default value to return when no keyframes are set.
        /// </summary>
        public TValue DefaultValue
        {
            get => _defaultValue;
            set
            {
                _defaultValue = value;
                OnDefaultValueChanged();
            }
        }
        public bool ConstrainKeyframedFPS
        {
            get => _constrainKeyframedFPS;
            set
            {
                _constrainKeyframedFPS = value;
                OnConstrainKeyframedFPSChanged();
            }
        }
        [DisplayName("Lerp Constrained FPS")]
        /// <summary>
        /// If true and the animation is baked or ConstrainKeyframedFPS is true, 
        /// lerps between two frames if the second lies between them.
        /// This essentially fakes a higher frames per second for data at a lower resolution.
        /// </summary>
        [Description(
            "If true and the animation is baked or ConstrainKeyframedFPS is true, " +
            "lerps between two frames if the second lies between them. " +
            "This essentially fakes a higher frames per second for data at a lower resolution.")]
        public bool LerpConstrainedFPS
        {
            get => _lerpConstrainedFPS;
            set
            {
                _lerpConstrainedFPS = value;
                OnLerpConstrainedFPSChanged();
            }
        }
        /// <summary>
        /// The value at the current time.
        /// </summary>
        public TValue CurrentPosition
        {
            get => _currentPosition;
            private set
            {
                _currentPosition = value;
                CurrentPositionChanged?.Invoke(this);
            }
        }
        /// <summary>
        /// The velocity at the current time.
        /// </summary>
        public TValue CurrentVelocity
        {
            get => _currentVelocity;
            private set
            {
                _currentVelocity = value;
                CurrentVelocityChanged?.Invoke(this);
            }
        }
        /// <summary>
        /// The acceleration at the current time.
        /// </summary>
        public TValue CurrentAcceleration
        {
            get => _currentAcceleration;
            private set
            {
                _currentAcceleration = value;
                CurrentAccelerationChanged?.Invoke(this);
            }
        }

        public override object GetCurrentValueGeneric() => CurrentPosition;
        public override object GetValueGeneric(float second) => _getValue(second);

        public TValue GetValue(float second) => _getValue(second);
        public TValue GetValueBakedBySecond(float second)
        {
            if (_baked is null)
                throw new InvalidOperationException("Cannot get baked value when not baked.");

            float frameTime = second.RemapToRange(0, LengthInSeconds) * BakedFramesPerSecond;
            int frame = (int)frameTime;

            if (LerpConstrainedFPS)
            {
                if (frame == _baked.Length - 1)
                {
                    if (Looped && frame != 0)
                    {
                        TValue t1 = _baked[frame];
                        TValue t2 = _baked[0];

                        //TODO: interpolate values by creating tangents dynamically?

                        //Span is always 1 frame, so no need to divide to normalize
                        float lerpTime = frameTime - frame;

                        return LerpValues(t1, t2, lerpTime);
                    }
                    return _baked[frame];
                }
                else
                {
                    TValue t1 = _baked[frame];
                    TValue t2 = _baked[frame + 1];

                    //TODO: interpolate values by creating tangents dynamically?

                    //Span is always 1 frame, so no need to divide to normalize
                    float lerpTime = frameTime - frame;

                    return LerpValues(t1, t2, lerpTime);
                }
            }
            else if (_baked.IndexInRangeArrayT(frame))
                return _baked[frame];
            else
                return DefaultValue;
        }
        /// <summary>
        /// Returns a value from the baked array by frame index.
        /// </summary>
        /// <param name="frame">The frame to get a value for.</param>
        /// <returns>The value at the specified frame.</returns>
        public TValue GetValueBakedByFrame(int frame)
        {
            if (_baked is null)
                throw new InvalidOperationException("Cannot get baked value when not baked.");

            if (!_baked.IndexInRangeArrayT(frame))
                return new TValue();

            return _baked[frame.Clamp(0, _baked.Length - 1)];
        }
        /// <summary>
        /// Returns a linearly interpolated value between two values.
        /// </summary>
        /// <param name="from">The starting value.</param>
        /// <param name="to">The target value.</param>
        /// <param name="time">Normalized time between the two values (0.0f - 1.0f).</param>
        /// <returns>A linearly interpolated value between two values.</returns>
        protected abstract TValue LerpValues(TValue from, TValue to, float time);

        protected void OnDefaultValueChanged()
            => DefaultValueChanged?.Invoke(this);

        protected void OnConstrainKeyframedFPSChanged()
            => ConstrainKeyframedFPSChanged?.Invoke(this);

        protected void OnLerpConstrainedFPSChanged()
            => LerpConstrainedFPSChanged?.Invoke(this);

        protected override void BakedChanged()
            => _getValue = ChooseValueGetter();

        public TValue GetValueBaked(int frameIndex)
            => _baked is null || _baked.Length == 0 ? new TValue() :
            _baked[frameIndex.Clamp(0, _baked.Length - 1)];

        public TValue GetValueKeyframed(float second)
            => Interpolate(second, EVectorValueType.Position);
        public TValue GetVelocityKeyframed(float second)
            => Interpolate(second, EVectorValueType.Velocity);
        public TValue GetAccelerationKeyframed(float second)
            => Interpolate(second, EVectorValueType.Acceleration);
        private TValue Interpolate(float second, EVectorValueType type)
        {
            if (Keyframes.Count == 0)
                return DefaultValue;

            if (ConstrainKeyframedFPS)
            {
                int frame = (int)(second * _bakedFPS);
                float floorSec = _bakedFPS != 0.0f ? (frame / _bakedFPS) : 0.0f;

                if (LerpConstrainedFPS)
                {
                    float ceilSec = _bakedFPS != 0.0f ? ((frame + 1) / _bakedFPS) : 0.0f;
                    float time = second - floorSec;
                    return LerpKeyedValues(floorSec, ceilSec, time, type);
                }

                second = floorSec;
            }

            return Keyframes.First?.Interpolate(second, type) ?? DefaultValue;
        }

        public override float CurrentTime
        {
            get => base.CurrentTime;
            set
            {
                float newTime = value.RemapToRange(0.0f, _lengthInSeconds);
                float oldTime = _currentTime;
                _currentTime = newTime;
                OnProgressed(newTime - oldTime);
                OnCurrentTimeChanged();
            }
        }

        public override void Tick(float delta)
        {
            if (UseTangentRelativeSpeed)
                delta /= GetVelocityMagnitude();

            base.Tick(delta);
        }

        protected abstract float GetVelocityMagnitude();
        protected override void OnProgressed(float delta)
        {
            //TODO: assign separate functions to be called by OnProgressed to avoid if statements and returns

            if (IsBaked)
            {
                CurrentPosition = GetValueBakedBySecond(_currentTime);
                CurrentVelocity = new TValue();
                CurrentAcceleration = new TValue();
                return;
            }

            _prevKeyframe ??= Keyframes.GetKeyBefore(_currentTime);

            if (Keyframes.Count == 0)
            {
                CurrentPosition = DefaultValue;
                CurrentVelocity = new TValue();
                CurrentAcceleration = new TValue();
                return;
            }

            float second = _currentTime;
            if (ConstrainKeyframedFPS)
            {
                int frame = (int)(second * _bakedFPS);
                float floorSec = frame / _bakedFPS;
                float ceilSec = (frame + 1) / _bakedFPS;

                //second - floorSec is the resulting delta from one frame to the next.
                //we want the delta to be between two frames with a specified number of frames in between, 
                //so we multiply by the FPS.
                float time = (second - floorSec) * _bakedFPS;

                if (LerpConstrainedFPS)
                {
                    TValue floorPosition = default, floorVelocity = default, floorAcceleration = default;
                    TValue ceilPosition = default, ceilVelocity = default, ceilAcceleration = default;

                    _prevKeyframe?.Interpolate(
                        floorSec,
                        out _prevKeyframe,
                        out _,
                        out _,
                        out floorPosition,
                        out floorVelocity,
                        out floorAcceleration);

                    _prevKeyframe?.Interpolate(
                        ceilSec,
                       out _,
                       out _,
                       out _,
                       out ceilPosition,
                       out ceilVelocity,
                       out ceilAcceleration);

                    CurrentPosition = LerpValues(floorPosition, ceilPosition, time);
                    CurrentVelocity = LerpValues(floorVelocity, ceilVelocity, time);
                    CurrentAcceleration = LerpValues(floorAcceleration, ceilAcceleration, time);
                    return;
                }
                second = floorSec;
            }

            TValue 
                pos = DefaultValue,
                vel = new(),
                acc = new();
            _prevKeyframe?.Interpolate(second,
                out _prevKeyframe,
                out _,
                out _,
                out pos,
                out vel,
                out acc);

            CurrentPosition = pos;
            CurrentVelocity = vel;
            CurrentAcceleration = acc;
        }
        private TValue LerpKeyedValues(float floorSec, float ceilSec, float time, EVectorValueType type)
        {
            VectorKeyframe<TValue>? prevKey = null;

            TValue? floorValue = _keyframes.First?.Interpolate(
                floorSec,
                type,
                out prevKey,
                out _,
                out _);

            TValue? ceilValue = prevKey?.Interpolate(ceilSec, type);

            if (floorValue is null || ceilValue is null)
                return DefaultValue;

            return LerpValues(floorValue.Value, ceilValue.Value, time);
        }
        public override void Bake(float framesPerSecond)
        {
            _bakedFPS = framesPerSecond;
            _bakedFrameCount = (int)Math.Ceiling(LengthInSeconds * framesPerSecond);
            _baked = new TValue[BakedFrameCount];
            float invFPS = 1.0f / _bakedFPS;
            for (int i = 0; i < BakedFrameCount; ++i)
                _baked[i] = GetValueKeyframed(i * invFPS);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        public void GetMinMax(bool velocity,
            out (float Time, float Value)[] min,
            out (float Time, float Value)[] max)
        {
            float[] inComps = GetComponents(velocity ? new TValue() : DefaultValue);

            //No keyframes? Return default value
            if (Keyframes.Count == 0)
                EvalZero(out min, out max, inComps);
            else if (Keyframes.Count == 1)
                EvalOne(velocity, out min, out max, ref inComps);
            else //There are two or more keyframes, need to evaluate interpolation for extrema using velocity
                EvalAll(velocity, out min, out max, ref inComps);
        }

        private void EvalAll(
            bool velocity,
            out (float Time, float Value)[] min,
            out (float Time, float Value)[] max,
            ref float[] inComps)
        {
            int compCount = inComps.Length;
            float[] outComps = new float[compCount];
            float[] inTanComps = new float[compCount];
            float[] outTanComps = new float[compCount];

            min = new (float Time, float Value)[compCount];
            min = min.Fill((0.0f, float.MaxValue));

            max = new (float Time, float Value)[compCount];
            max = max.Fill((0.0f, float.MinValue));

            VectorKeyframe<TValue>? next;
            float minVal, maxVal, oldMin, oldMax;

            //Evaluate all keyframes
            VectorKeyframe<TValue> kf = Keyframes.First!;
            for (int i = 0; i < Keyframes.Count; ++i, kf = next)
            {
                //Retrieve the next keyframe; will be the first keyframe if this is the last
                next = kf.Next ??
                    (kf.OwningTrack?.FirstKey != kf ?
                    kf.OwningTrack?.FirstKey as VectorKeyframe<TValue> :
                    null);

                if (next is null)
                    break;

                inComps = GetComponents(velocity ? next.InTangent : next.InValue);
                outComps = GetComponents(velocity ? kf.OutTangent : kf.OutValue);

                bool cubic = kf.InterpolationTypeOut == EVectorInterpType.Smooth;
                if (cubic)
                {
                    inTanComps = GetComponents(next.InTangent);
                    outTanComps = GetComponents(kf.OutTangent);
                }

                //Evaluate interpolation
                for (int compIndex = 0; compIndex < compCount; ++compIndex)
                {
                    minVal = min[compIndex].Value;
                    maxVal = max[compIndex].Value;

                    oldMin = minVal;
                    oldMax = maxVal;

                    //Check if the keyframe already exceeds the current bounds
                    //If the second is zero, the in value is irrelevant.
                    if (kf.Second.IsZero())
                    {
                        minVal = Math.Min(minVal, outComps[compIndex]);
                        maxVal = Math.Max(maxVal, outComps[compIndex]);
                    }
                    //Otherwise, if the second is equal to the total length,
                    //the out value is irrelevant.
                    else if (kf.Second.EqualTo(Keyframes.LengthInSeconds))
                    {
                        minVal = Math.Min(minVal, inComps[compIndex]);
                        maxVal = Math.Max(maxVal, inComps[compIndex]);
                    }
                    else
                    {
                        //Keyframe is somewhere in between the start and end,
                        //look at both the in and out values
                        minVal = XRMath.Min(minVal, inComps[compIndex], outComps[compIndex]);
                        maxVal = XRMath.Max(maxVal, inComps[compIndex], outComps[compIndex]);
                    }

                    //Make sure to update the second of the current min and max
                    if (oldMin != minVal)
                        min[compIndex].Time = kf.Second;
                    if (oldMax != maxVal)
                        max[compIndex].Time = kf.Second;

                    if (cubic)
                        EvalCubic(
                            velocity,
                            min,
                            max,
                            ref inComps,
                            ref outComps,
                            inTanComps,
                            outTanComps,
                            next,
                            ref minVal,
                            ref maxVal,
                            ref oldMin,
                            ref oldMax,
                            kf,
                            compIndex);
                }
            }
        }

        private static void EvalZero(
            out (float Time, float Value)[] min,
            out (float Time, float Value)[] max,
            float[] inComps)
            => min = max = inComps.Select(x => (0.0f, x)).ToArray();

        private void EvalOne(
            bool velocity,
            out (float Time, float Value)[] min,
            out (float Time, float Value)[] max,
            ref float[] inComps)
        {
            int compCount = inComps.Length;
            float[] outComps = new float[compCount];

            VectorKeyframe<TValue> kf = Keyframes.First!;
            //If the second is zero, the in value is irrelevant.
            if (kf.Second.IsZero())
            {
                outComps = GetComponents(velocity ? kf.OutTangent : kf.OutValue);
                min = max = outComps.Select(x => (kf.Second, x)).ToArray();
            }
            //Otherwise, if the second is equal to the total length,
            //the out value is irrelevant.
            else if (kf.Second.EqualTo(Keyframes.LengthInSeconds))
            {
                inComps = GetComponents(velocity ? kf.InTangent : kf.InValue);
                min = max = inComps.Select(x => (kf.Second, x)).ToArray();
            }
            else
            {
                //Keyframe is somewhere in between the start and end,
                //look at both the in and out values
                inComps = GetComponents(velocity ? kf.InTangent : kf.InValue);
                outComps = GetComponents(velocity ? kf.OutTangent : kf.OutValue);

                min = new (float Time, float Value)[compCount];
                max = new (float Time, float Value)[compCount];

                for (int i = 0; i < compCount; ++i)
                {
                    min[i] = (kf.Second, Math.Min(inComps[i], outComps[i]));
                    max[i] = (kf.Second, Math.Max(inComps[i], outComps[i]));
                }
            }
        }

        private void EvalCubic(
            bool velocity,
            (float Time, float Value)[] min,
            (float Time, float Value)[] max,
            ref float[] inComps,
            ref float[] outComps,
            float[] inTanComps,
            float[] outTanComps,
            VectorKeyframe<TValue> next,
            ref float minVal,
            ref float maxVal,
            ref float oldMin,
            ref float oldMax,
            VectorKeyframe<TValue> kf,
            int compIndex)
        {
            float[]? valComps;
            float first;
            float zero;
            if (velocity)
            {
                inComps = GetComponents(next.InValue);
                outComps = GetComponents(kf.OutValue);

                Interp.CubicBezierAccelerationCoefs(
                    outComps[compIndex],
                    outComps[compIndex] + outTanComps[compIndex],
                    inComps[compIndex] + inTanComps[compIndex],
                    inComps[compIndex],
                    out first, out zero);

                if (first != 0.0f)
                {
                    float time = -zero / first;

                    oldMin = minVal;
                    oldMax = maxVal;

                    //We only want times that are within 0 - 1
                    bool timeValid = time >= 0.0f && time <= 1.0f;
                    if (timeValid)
                    {
                        //Retrieve velocity value using time found where acceleration = 0
                        TValue val = kf.InterpolateVelocityNormalized(next, time);

                        //Find real second within the animation using normalized time value
                        float interpSec;
                        if (kf.Next is null)
                        {
                            //This is the last keyframe,
                            //So evaluate past the end to the first keyframe
                            float span = LengthInSeconds - kf.Second + next.Second;
                            interpSec = (kf.Second + span * time).RemapToRange(0.0f, LengthInSeconds);
                        }
                        else //Just lerp from this second to the next, easy
                            interpSec = Interp.Lerp(kf.Second, next.Second, time);

                        //Retrieve the components from the value and update min/max and second as usual
                        valComps = GetComponents(val);

                        minVal = XRMath.Min(minVal, valComps[compIndex]);
                        maxVal = XRMath.Max(maxVal, valComps[compIndex]);

                        if (oldMin != minVal && minVal == valComps[compIndex])
                            min[compIndex].Time = interpSec;
                        if (oldMax != maxVal && maxVal == valComps[compIndex])
                            max[compIndex].Time = interpSec;
                    }
                }
            }
            else
            {
                //If not the last keyframe, evaluate the interpolation
                //between this keyframe and the next to find the exact second(s) where
                //velocity reaches zero. This means that the position value at that second
                //is an extrema and should be considered for min/max.

                //Retrieve velocity interpolation equation coefficients
                //so we can solve for the time value where acceleration is zero.
                Interp.CubicBezierVelocityCoefs(
                    outComps[compIndex],
                    outComps[compIndex] + outTanComps[compIndex],
                    inComps[compIndex] + inTanComps[compIndex],
                    inComps[compIndex],
                    out float second, out first, out zero);

                //Find the roots (zeroes) of the interpolation binomial using the coefficients
                if (XRMath.QuadraticRealRoots(second, first, zero, out float time1, out float time2))
                {
                    oldMin = minVal;
                    oldMax = maxVal;

                    //The quadratic equation will return two times
                    float[] times = [time1, time2];
                    foreach (float time in times)
                    {
                        //We only want times that are within 0 - 1
                        bool timeValid = time >= 0.0f && time <= 1.0f;
                        if (timeValid)
                        {
                            //Retrieve position value using time found where velocity = 0
                            TValue val = kf.InterpolatePositionNormalized(next, time);

                            //Find real second within the animation using normalized time value
                            float interpSec = 0.0f;
                            if (kf.Next is null)
                            {
                                //This is the last keyframe,
                                //So evaluate past the end to the first keyframe
                                float span = LengthInSeconds - kf.Second + next.Second;
                                interpSec = (kf.Second + span * time).RemapToRange(0.0f, LengthInSeconds);
                            }
                            else //Just lerp from this second to the next, easy
                                interpSec = Interp.Lerp(kf.Second, next.Second, time);

                            //Retrieve the components from the value and update min/max and second as usual
                            valComps = GetComponents(val);

                            minVal = XRMath.Min(minVal, valComps[compIndex]);
                            maxVal = XRMath.Max(maxVal, valComps[compIndex]);

                            if (oldMin != minVal && minVal == valComps[compIndex])
                                min[compIndex].Time = interpSec;
                            if (oldMax != maxVal && maxVal == valComps[compIndex])
                                max[compIndex].Time = interpSec;
                        }
                    }
                }
            }

            min[compIndex].Value = minVal;
            max[compIndex].Value = maxVal;
        }

        protected abstract float[] GetComponents(TValue value);
        protected abstract TValue GetMaxValue();
        protected abstract TValue GetMinValue();
    }
}
