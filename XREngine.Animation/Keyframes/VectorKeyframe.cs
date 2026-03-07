using Extensions;
using System.ComponentModel;
using XREngine.Data.Animation;

namespace XREngine.Animation
{
    public abstract class VectorKeyframe<T> : Keyframe, ICartesianKeyframe<T> where T : unmanaged
    {
        object IPlanarKeyframe.InValue { get => InValue; set => InValue = (T)value; }
        object IPlanarKeyframe.OutValue { get => OutValue; set => OutValue = (T)value; }
        object IPlanarKeyframe.InTangent { get => InTangent; set => InTangent = (T)value; }
        object IPlanarKeyframe.OutTangent { get => OutTangent; set => OutTangent = (T)value; }

        public float TrackLength => OwningTrack?.LengthInSeconds ?? 0.0f;

        public VectorKeyframe()
            : this(0.0f, new T(), new T(), EVectorInterpType.Smooth) { }
        public VectorKeyframe(int frameIndex, float FPS, T inValue, T outValue, T inTangent, T outTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inValue, outValue, inTangent, outTangent, type) { }
        public VectorKeyframe(int frameIndex, float FPS, T inoutValue, T inoutTangent, EVectorInterpType type)
            : this(frameIndex / FPS, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public VectorKeyframe(float second, T inoutValue, T inoutTangent, EVectorInterpType type)
            : this(second, inoutValue, inoutValue, inoutTangent, inoutTangent, type) { }
        public VectorKeyframe(float second, T inValue, T outValue, T inTangent, T outTangent, EVectorInterpType type) : base()
        {
            _interpolateOut = StepOut;
            _interpolateVelocityOut = StepVelocityOut;
            _interpolateAccelerationOut = StepAccelerationOut;
            _interpolateIn = StepIn;
            _interpolateVelocityIn = StepVelocityIn;
            _interpolateAccelerationIn = StepAccelerationIn;
            Second = second;
            InValue = inValue;
            OutValue = outValue;
            InTangent = inTangent;
            OutTangent = outTangent;
            InterpolationTypeIn = type;
            InterpolationTypeOut = type;
        }

        protected delegate T DelInterpolate(VectorKeyframe<T>? other, float timeOffset, float timeSpan);
        protected EVectorInterpType _interpolationTypeOut;
        protected DelInterpolate _interpolateOut = (_, _, _) => new();
        protected DelInterpolate _interpolateVelocityOut = (_, _, _) => new();
        protected DelInterpolate _interpolateAccelerationOut = (_, _, _) => new();
        protected EVectorInterpType _interpolationTypeIn;
        protected DelInterpolate _interpolateIn = (_, _, _) => new();
        protected DelInterpolate _interpolateVelocityIn = (_, _, _) => new();
        protected DelInterpolate _interpolateAccelerationIn = (_, _, _) => new();

        [Browsable(false)]
        public override Type ValueType => typeof(T);

        private T _inValue, _outValue, _inTangent, _outTangent;
        private bool _syncInOutValues = true;
        private bool _syncInOutTangentDirections = true;
        private bool _syncInOutTangentMagnitudes = true;
        private bool _synchronizing = false;

        public bool SyncInOutValues
        {
            get => _syncInOutValues;
            set
            {
                _syncInOutValues = value;
                if (_syncInOutValues && !_synchronizing)
                {
                    _synchronizing = true;
                    UnifyValues(EUnifyBias.Average);
                    _synchronizing = false;
                }
                OwningTrack?.OnChanged();
            }
        }
        public bool SyncInOutTangentDirections
        {
            get => _syncInOutTangentDirections;
            set
            {
                _syncInOutTangentDirections = value;
                if (_syncInOutTangentDirections && !_synchronizing)
                {
                    _synchronizing = true;
                    UnifyTangentDirections(EUnifyBias.Average);
                    _synchronizing = false;
                }
                OwningTrack?.OnChanged();
            }
        }
        public bool SyncInOutTangentMagnitudes
        {
            get => _syncInOutTangentMagnitudes;
            set
            {
                _syncInOutTangentMagnitudes = value;
                if (_syncInOutTangentMagnitudes && !_synchronizing)
                {
                    _synchronizing = true;
                    UnifyTangentMagnitudes(EUnifyBias.Average);
                    _synchronizing = false;
                }
                OwningTrack?.OnChanged();
            }
        }
        public T InValue
        {
            get => _inValue;
            set
            {
                _inValue = value;
                if (_syncInOutValues && !_synchronizing)
                {
                    _synchronizing = true;
                    UnifyValues(EUnifyBias.In);
                    _synchronizing = false;
                }
                OwningTrack?.OnChanged();
            }
        }
        public T OutValue
        {
            get => _outValue;
            set
            {
                _outValue = value;
                if (_syncInOutValues && !_synchronizing)
                {
                    _synchronizing = true;
                    UnifyValues(EUnifyBias.Out);
                    _synchronizing = false;
                }
                OwningTrack?.OnChanged();
            }
        }
        public T InTangent
        {
            get => _inTangent;
            set
            {
                _inTangent = value;
                if (!_synchronizing)
                {
                    _synchronizing = true;
                    if (SyncInOutTangentDirections && SyncInOutTangentMagnitudes)
                        UnifyTangents(EUnifyBias.In);
                    else if (SyncInOutTangentMagnitudes)
                        UnifyTangentMagnitudes(EUnifyBias.In);
                    else if (SyncInOutTangentDirections)
                        UnifyTangentDirections(EUnifyBias.In);
                    _synchronizing = false;
                }
                OwningTrack?.OnChanged();
            }
        }
        public T OutTangent
        {
            get => _outTangent;
            set
            {
                _outTangent = value;
                if (!_synchronizing)
                {
                    _synchronizing = true;
                    if (SyncInOutTangentDirections && SyncInOutTangentMagnitudes)
                        UnifyTangents(EUnifyBias.Out);
                    else if (SyncInOutTangentMagnitudes)
                        UnifyTangentMagnitudes(EUnifyBias.Out);
                    else if (SyncInOutTangentDirections)
                        UnifyTangentDirections(EUnifyBias.Out);
                    _synchronizing = false;
                }
                OwningTrack?.OnChanged();
            }
        }

        public new VectorKeyframe<T>? Next
        {
            get => _next as VectorKeyframe<T>;
            //set => _next = value;
        }
        public new VectorKeyframe<T>? Prev
        {
            get => _prev as VectorKeyframe<T>;
            //set => _prev = value;
        }

        public EVectorInterpType InterpolationTypeIn
        {
            get => _interpolationTypeIn;
            set
            {
                _interpolationTypeIn = value;
                switch (_interpolationTypeIn)
                {
                    case EVectorInterpType.Step:
                        _interpolateIn = StepIn;
                        _interpolateVelocityIn = StepVelocityIn;
                        _interpolateAccelerationIn = StepAccelerationIn;
                        break;
                    case EVectorInterpType.Linear:
                        _interpolateIn = LerpIn;
                        _interpolateVelocityIn = LerpVelocityIn;
                        _interpolateAccelerationIn = LerpAccelerationIn;
                        break;
                    case EVectorInterpType.Smooth:
                        _interpolateIn = CubicBezierIn;
                        _interpolateVelocityIn = CubicBezierVelocityIn;
                        _interpolateAccelerationIn = CubicBezierAccelerationIn;
                        break;
                    case EVectorInterpType.Hermite:
                        _interpolateIn = CubicHermiteIn;
                        _interpolateVelocityIn = CubicHermiteVelocityIn;
                        _interpolateAccelerationIn = CubicHermiteAccelerationIn;
                        break;
                }
                OwningTrack?.OnChanged();
            }
        }
        public EVectorInterpType InterpolationTypeOut
        {
            get => _interpolationTypeOut;
            set
            {
                _interpolationTypeOut = value;
                switch (_interpolationTypeOut)
                {
                    case EVectorInterpType.Step:
                        _interpolateOut = StepOut;
                        _interpolateVelocityOut = StepVelocityOut;
                        _interpolateAccelerationOut = StepAccelerationOut;
                        break;
                    case EVectorInterpType.Linear:
                        _interpolateOut = LerpOut;
                        _interpolateVelocityOut = LerpVelocityOut;
                        _interpolateAccelerationOut = LerpAccelerationOut;
                        break;
                    case EVectorInterpType.Smooth:
                        _interpolateOut = CubicBezierOut;
                        _interpolateVelocityOut = CubicBezierVelocityOut;
                        _interpolateAccelerationOut = CubicBezierAccelerationOut;
                        break;
                    case EVectorInterpType.Hermite:
                        _interpolateOut = CubicHermiteOut;
                        _interpolateVelocityOut = CubicHermiteVelocityOut;
                        _interpolateAccelerationOut = CubicHermiteAccelerationOut;
                        break;
                }
                OwningTrack?.OnChanged();
            }
        }

        /// <summary>
        /// Returns the next keyframe in the track, and the timespan in seconds between this keyframe and the next.
        /// If the next keyframe is the first keyframe in the track, the timespan will be the time from this keyframe to the end of the track.
        /// </summary>
        /// <param name="span"></param>
        /// <returns></returns>
        public VectorKeyframe<T>? GetNextKeyframe(out float span)
        {
            float nextSecond = Next?.Second ?? 0.0f;
            if (!IsLast && nextSecond <= TrackLength)
            {
                span = nextSecond - Second;
                return Next;
            }
            else if (OwningTrack?.LoopsAfterLastKey == true && OwningTrack?.FirstKey != this)
            {
                VectorKeyframe<T>? next = OwningTrack?.FirstKey as VectorKeyframe<T>;
                span = TrackLength - Second + (next?.Second ?? 0.0f);
                return next;
            }
            else
            {
                span = 0.0f;
                return null;
            }
        }

        public VectorKeyframe<T>? GetPrevKeyframe(out float span)
        {
            float prevSecond = Prev?.Second ?? 0.0f;
            if (!IsFirst && prevSecond >= 0.0f)
            {
                span = Second - prevSecond;
                return Prev;
            }
            else if (OwningTrack?.LoopsBeforeFirstKey == true && OwningTrack?.LastKey != this)
            {
                VectorKeyframe<T>? prev = OwningTrack?.LastKey as VectorKeyframe<T>;
                span = TrackLength - (prev?.Second ?? 0.0f) + Second;
                return prev;
            }
            else
            {
                span = 0.0f;
                return null;
            }
        }

        /// <summary>
        /// Interpolates from this keyframe to the next using a normalized time value (0.0f - 1.0f)
        /// </summary>
        public T InterpolatePositionNextNormalized(float time)
        {
            var next = GetNextKeyframe(out float span);

            return InterpolatePositionSegment(next, span * time, span);
        }

        /// <summary>
        /// Interpolates velocity from this keyframe to the next using a normalized time value (0.0f - 1.0f)
        /// </summary>
        public T InterpolateVelocityNextNormalized(float time)
        {
            var next = GetNextKeyframe(out float span);
            return InterpolateVelocitySegment(next, span * time, span);
        }

        /// <summary>
        /// Interpolates acceleration from this keyframe to the next using a normalized time value (0.0f - 1.0f)
        /// </summary>
        public T InterpolateAccelerationNextNormalized(float time)
        {
            var next = GetNextKeyframe(out float span);
            return InterpolateAccelerationSegment(next, span * time, span);
        }

        /// <summary>
        /// Interpolates from this keyframe to the next using a normalized time value (0.0f - 1.0f)
        /// </summary>
        public T InterpolatePositionNormalized(VectorKeyframe<T>? next, float time)
        {
            if (next is null)
                return OutValue;

            float span = next.Second < Second 
                ? TrackLength - Second + next.Second 
                : next.Second - Second;

            if (span.IsZero())
                return OutValue;

            return InterpolatePositionSegment(next, span * time, span);
        }

        /// <summary>
        /// Interpolates velocity from this keyframe to the next using a normalized time value (0.0f - 1.0f)
        /// </summary>
        public T InterpolateVelocityNormalized(VectorKeyframe<T>? next, float time)
        {
            if (next is null)
                return OutTangent;

            float span = next.Second < Second 
                ? TrackLength - Second + next.Second 
                : next.Second - Second;

            if (span.IsZero())
                return OutTangent;

            return InterpolateVelocitySegment(next, span * time, span);
        }

        /// <summary>
        /// Interpolates acceleration from this keyframe to the next using a normalized time value (0.0f - 1.0f)
        /// </summary>
        public T InterpolateAccelerationNormalized(VectorKeyframe<T>? next, float time)
        {
            if (next is null)
                return default;

            float span = next.Second < Second 
                ? TrackLength - Second + next.Second 
                : next.Second - Second;

            if (span.IsZero())
                return default;

            return InterpolateAccelerationSegment(next, span * time, span);
        }

        public T? Interpolate(float desiredSecond, EVectorValueType type)
        {
            VectorKeyframe<T>? key1, key2;
            float diff, span;
            if (desiredSecond >= Second)
            {
                if (IsLast || Next!.Second > TrackLength)
                {
                    if (OwningTrack?.LoopsAfterLastKey == true && OwningTrack?.FirstKey != this)
                    {
                        VectorKeyframe<T>? first = OwningTrack?.FirstKey as VectorKeyframe<T>;
                        span = TrackLength - Second + (first?.Second ?? 0.0f);
                        diff = desiredSecond - Second;
                        key1 = this;
                        key2 = first;
                    }
                    else
                        return type == EVectorValueType.Position ? OutValue : new T();
                }
                else if (desiredSecond < (Next?.Second ?? 0.0f))
                {
                    //Within two keyframes, interpolate regularly
                    span = _next!.Second - Second;
                    diff = desiredSecond - Second;
                    key1 = this;
                    key2 = Next;
                }
                else
                    return Next?.Interpolate(desiredSecond, type);
            }
            else //desiredSecond < Second
            {
                if (!IsFirst)
                    return Prev!.Interpolate(desiredSecond, type);

                VectorKeyframe<T>? last = OwningTrack?.GetKeyBeforeGeneric(TrackLength) as VectorKeyframe<T>;

                if (OwningTrack?.LoopsBeforeFirstKey == true && last != this && last != null)
                {
                    span = TrackLength - last.Second + Second;
                    diff = TrackLength - last.Second + desiredSecond;
                    key1 = last;
                    key2 = this;
                }
                else
                    return type == EVectorValueType.Position ? InValue : new T();
            }

            return type switch
            {
                EVectorValueType.Velocity => key1.InterpolateVelocitySegment(key2, diff, span),
                EVectorValueType.Acceleration => key1.InterpolateAccelerationSegment(key2, diff, span),
                _ => key1.InterpolatePositionSegment(key2, diff, span),
            };
        }
        public T Interpolate(
            float desiredSecond,
            EVectorValueType type,
            out VectorKeyframe<T>? prevKey,
            out VectorKeyframe<T>? nextKey,
            out float normalizedTime)
        {
            prevKey = this;
            nextKey = Next;

            float span, diff;
            VectorKeyframe<T>? key1, key2;

            if (desiredSecond >= Second)
            {
                if (IsLast || Next!.Second > TrackLength)
                {
                    if (OwningTrack?.LoopsAfterLastKey == true && OwningTrack?.FirstKey != this)
                    {
                        VectorKeyframe<T>? first = OwningTrack?.FirstKey as VectorKeyframe<T>;
                        span = TrackLength - Second + (first?.Second ?? 0.0f);
                        diff = desiredSecond - Second;
                        key1 = this;
                        key2 = first;
                    }
                    else
                    {
                        normalizedTime = 0.0f;
                        return type == EVectorValueType.Position ? OutValue : new T();
                    }
                }
                else if (desiredSecond < Next.Second)
                {
                    //Within two keyframes, interpolate regularly
                    span = (_next?.Second ?? 0.0f) - Second;
                    diff = desiredSecond - Second;
                    key1 = this;
                    key2 = Next;
                }
                else
                {
                    return Next.Interpolate(desiredSecond, type, out prevKey, out nextKey, out normalizedTime);
                }
            }
            else //desiredSecond < Second
            {
                if (!IsFirst)
                    return Prev!.Interpolate(desiredSecond, type, out prevKey, out nextKey, out normalizedTime);

                VectorKeyframe<T>? last = OwningTrack?.GetKeyBeforeGeneric(TrackLength) as VectorKeyframe<T>;

                if (OwningTrack?.LoopsBeforeFirstKey == true && last != this && last != null)
                {
                    span = TrackLength - last.Second + Second;
                    diff = TrackLength - last.Second + desiredSecond;
                    key1 = last;
                    key2 = this;
                }
                else
                {
                    normalizedTime = 0.0f;
                    return type == EVectorValueType.Position ? InValue : new T();
                }
            }

            normalizedTime = diff / span;
            return type switch
            {
                EVectorValueType.Velocity => key1.InterpolateVelocitySegment(key2, diff, span),
                EVectorValueType.Acceleration => key1.InterpolateAccelerationSegment(key2, diff, span),
                _ => key1.InterpolatePositionSegment(key2, diff, span),
            };
        }
        public void Interpolate(
            float desiredSecond,
            out VectorKeyframe<T>? prevKey,
            out VectorKeyframe<T>? nextKey,
            out float normalizedTime,
            out T position,
            out T velocity,
            out T acceleration)
        {
            prevKey = this;
            nextKey = Next;

            float span, diff;
            VectorKeyframe<T>? key1, key2;

            if (desiredSecond >= Second)
            {
                if (IsLast || Next!.Second > TrackLength)
                {
                    if (OwningTrack?.LoopsAfterLastKey == true && OwningTrack?.FirstKey != this)
                    {
                        VectorKeyframe<T>? first = OwningTrack?.FirstKey as VectorKeyframe<T>;
                        span = TrackLength - Second + (first?.Second ?? 0.0f);
                        diff = desiredSecond - Second;
                        key1 = this;
                        key2 = first;
                    }
                    else
                    {
                        normalizedTime = 0.0f;
                        position = OutValue;
                        velocity = new T();
                        acceleration = new T();
                        return;
                    }
                }
                else if (desiredSecond < Next.Second)
                {
                    //Within two keyframes, interpolate regularly
                    span = (_next?.Second ?? 0.0f) - Second;
                    diff = desiredSecond - Second;
                    key1 = this;
                    key2 = Next;
                }
                else
                {
                    Next.Interpolate(desiredSecond,
                        out prevKey,
                        out nextKey,
                        out normalizedTime,
                        out position,
                        out velocity,
                        out acceleration);

                    return;
                }
            }
            else //desiredSecond < Second
            {
                if (!IsFirst)
                {
                    Prev!.Interpolate(desiredSecond,
                        out prevKey,
                        out nextKey,
                        out normalizedTime,
                        out position,
                        out velocity,
                        out acceleration);

                    return;
                }
                else
                {
                    VectorKeyframe<T>? last = OwningTrack?.GetKeyBeforeGeneric(TrackLength) as VectorKeyframe<T>;

                    if (OwningTrack?.LoopsBeforeFirstKey == true && last != this && last != null)
                    {
                        span = TrackLength - last.Second + Second;
                        diff = TrackLength - last.Second + desiredSecond;
                        key1 = last;
                        key2 = this;
                    }
                    else
                    {
                        normalizedTime = 0.0f;
                        position = InValue;
                        velocity = new T();
                        acceleration = new T();
                        return;
                    }
                }
            }

            normalizedTime = diff / span;
            position = key1.InterpolatePositionSegment(key2, diff, span);
            velocity = key1.InterpolateVelocitySegment(key2, diff, span);
            acceleration = key1.InterpolateAccelerationSegment(key2, diff, span);
        }

        private T InterpolatePositionSegment(VectorKeyframe<T>? next, float diff, float span)
            => InterpolateSegment(
                next,
                diff,
                span,
                OutValue,
                _interpolateOut,
                static key => key._interpolateIn,
                static (key, other) => other);

        private T InterpolateVelocitySegment(VectorKeyframe<T>? next, float diff, float span)
            => InterpolateSegment(
                next,
                diff,
                span,
                OutTangent,
                _interpolateVelocityOut,
                static key => key._interpolateVelocityIn,
                static (key, other) => other);

        private T InterpolateAccelerationSegment(VectorKeyframe<T>? next, float diff, float span)
            => InterpolateSegment(
                next,
                diff,
                span,
                default,
                _interpolateAccelerationOut,
                static key => key._interpolateAccelerationIn,
                static (key, other) => other);

        private T InterpolateSegment(
            VectorKeyframe<T>? next,
            float diff,
            float span,
            T fallback,
            DelInterpolate interpolateOut,
            Func<VectorKeyframe<T>, DelInterpolate> getIncomingInterpolator,
            Func<VectorKeyframe<T>, VectorKeyframe<T>, VectorKeyframe<T>?> getIncomingOtherKey)
        {
            if (next is null || span.IsZero())
                return fallback;

            if (next.InterpolationTypeIn == InterpolationTypeOut)
                return interpolateOut(next, diff, span);

            float normalizedTime = Math.Clamp(diff / span, 0.0f, 1.0f);
            T outInterp = interpolateOut(next, diff, span);
            T inInterp = getIncomingInterpolator(next)(getIncomingOtherKey(next, this), diff, span);
            return LerpValues(outInterp, inInterp, normalizedTime);
        }

        public T StepOut(VectorKeyframe<T>? next, float diff, float span)
            => (diff / span) < 1.0f ? OutValue : (next?.OutValue ?? new ());
        public T StepVelocityOut(VectorKeyframe<T>? next, float diff, float span)
            => new();
        public T StepAccelerationOut(VectorKeyframe<T>? next, float diff, float span)
            => new();

        public abstract T LerpOut(VectorKeyframe<T>? next, float diff, float span);
        public abstract T LerpVelocityOut(VectorKeyframe<T>? next, float diff, float span);
        public T LerpAccelerationOut(VectorKeyframe<T>? next, float diff, float span)
            => new();

        public abstract T CubicBezierOut(VectorKeyframe<T>? next, float diff, float span);
        public abstract T CubicBezierVelocityOut(VectorKeyframe<T>? next, float diff, float span);
        public abstract T CubicBezierAccelerationOut(VectorKeyframe<T>? next, float diff, float span);
        public virtual T CubicHermiteOut(VectorKeyframe<T>? next, float diff, float span)
            => CubicBezierOut(next, diff, span);
        public virtual T CubicHermiteVelocityOut(VectorKeyframe<T>? next, float diff, float span)
            => CubicBezierVelocityOut(next, diff, span);
        public virtual T CubicHermiteAccelerationOut(VectorKeyframe<T>? next, float diff, float span)
            => CubicBezierAccelerationOut(next, diff, span);

        public T StepIn(VectorKeyframe<T>? prev, float diff, float span)
            => (diff / span) < 1.0f ? InValue : (prev?.InValue ?? new());
        public T StepVelocityIn(VectorKeyframe<T>? prev, float diff, float span)
            => new();
        public T StepAccelerationIn(VectorKeyframe<T>? prev, float diff, float span)
            => new();

        public abstract T LerpIn(VectorKeyframe<T>? prev, float diff, float span);
        public abstract T LerpVelocityIn(VectorKeyframe<T>? prev, float diff, float span);
        public T LerpAccelerationIn(VectorKeyframe<T>? prev, float diff, float span)
            => new();

        public abstract T CubicBezierIn(VectorKeyframe<T>? prev, float diff, float span);
        public abstract T CubicBezierVelocityIn(VectorKeyframe<T>? prev, float diff, float span);
        public abstract T CubicBezierAccelerationIn(VectorKeyframe<T>? prev, float diff, float span);
        public virtual T CubicHermiteIn(VectorKeyframe<T>? prev, float diff, float span)
            => CubicBezierIn(prev, diff, span);
        public virtual T CubicHermiteVelocityIn(VectorKeyframe<T>? prev, float diff, float span)
            => CubicBezierVelocityIn(prev, diff, span);
        public virtual T CubicHermiteAccelerationIn(VectorKeyframe<T>? prev, float diff, float span)
            => CubicBezierAccelerationIn(prev, diff, span);

        public abstract T LerpValues(T a, T b, float t);

        public void AverageKeyframe(
            EUnifyBias valueBias,
            EUnifyBias tangentBias,
            bool tangentDirections,
            bool tangentMagnitudes)
        {
            UnifyValues(valueBias);
            if (tangentDirections)
            {
                if (tangentMagnitudes)
                    UnifyTangents(tangentBias);
                else
                    UnifyTangentDirections(tangentBias);
            }
            else
                UnifyTangentMagnitudes(tangentBias);
        }

        public abstract void UnifyTangents(EUnifyBias bias);
        public abstract void UnifyTangentDirections(EUnifyBias bias);
        public abstract void UnifyTangentMagnitudes(EUnifyBias bias);
        public abstract void UnifyValues(EUnifyBias bias);
        public abstract void MakeOutLinear();
        public abstract void MakeInLinear();

        public void UnifyKeyframe(EUnifyBias bias)
        {
            UnifyTangents(bias);
            UnifyValues(bias);
        }
    }
}
