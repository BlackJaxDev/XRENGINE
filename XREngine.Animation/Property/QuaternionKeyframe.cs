using Extensions;
using System.ComponentModel;
using System.Numerics;
using XREngine.Data;

namespace XREngine.Animation
{
    public class QuaternionKeyframe : Keyframe, IRadialKeyframe
    {
        public QuaternionKeyframe() { }
        public QuaternionKeyframe(int frameIndex, float FPS, Quaternion inValue, Quaternion outValue, Quaternion inTangent, Quaternion outTangent, ERadialInterpType type)
            : this(frameIndex / FPS, inValue, outValue, inTangent, outTangent, type) { }
        public QuaternionKeyframe(int frameIndex, float FPS, Quaternion inoutValue, Quaternion inTangent, Quaternion outTangent, ERadialInterpType type)
            : this(frameIndex / FPS, inoutValue, inoutValue, inTangent, outTangent, type) { }
        public QuaternionKeyframe(float second, Quaternion inoutValue, Quaternion inOutTangent, ERadialInterpType type)
            : this(second, inoutValue, inoutValue, inOutTangent, inOutTangent, type) { }
        public QuaternionKeyframe(float second, Quaternion inoutValue, Quaternion inTangent, Quaternion outTangent, ERadialInterpType type)
            : this(second, inoutValue, inoutValue, inTangent, outTangent, type) { }
        public QuaternionKeyframe(float second, Quaternion inValue, Quaternion outValue, Quaternion inTangent, Quaternion outTangent, ERadialInterpType type) : base()
        {
            Second = second;
            InValue = inValue;
            OutValue = outValue;
            InTangent = inTangent;
            OutTangent = outTangent;
            InterpolationTypeOut = type;
        }

        private delegate Quaternion DelInterpolate(QuaternionKeyframe? key1, QuaternionKeyframe? key2, float time);
        private DelInterpolate _interpolateOut = CubicBezier;
        private DelInterpolate _interpolateIn = CubicBezier;
        protected ERadialInterpType _interpolationTypeOut;
        protected ERadialInterpType _interpolationTypeIn;
        private Quaternion _inValue = Quaternion.Identity;
        private Quaternion _outValue = Quaternion.Identity;
        private Quaternion _inTangent = Quaternion.Identity;
        private Quaternion _outTangent = Quaternion.Identity;

        [Browsable(false)]
        public override Type ValueType => typeof(Quaternion);

        public Quaternion InValue
        {
            get => _inValue;
            set => SetField(ref _inValue, value);
        }
        public Quaternion OutValue
        {
            get => _outValue;
            set => SetField(ref _outValue, value);
        }
        public Quaternion InTangent
        {
            get => _inTangent;
            set => SetField(ref _inTangent, value);
        }
        public Quaternion OutTangent
        {
            get => _outTangent;
            set => SetField(ref _outTangent, value);
        }

        public new QuaternionKeyframe? Next
        {
            get => _next as QuaternionKeyframe;
            set => _next = value;
        }
        public new QuaternionKeyframe? Prev
        {
            get => _prev as QuaternionKeyframe;
            set => _prev = value;
        }

        public float TrackLength => OwningTrack?.LengthInSeconds ?? 0.0f;

        public ERadialInterpType InterpolationTypeIn
        {
            get => _interpolationTypeIn;
            set => SetField(ref _interpolationTypeIn, value);
        }
        public ERadialInterpType InterpolationTypeOut
        {
            get => _interpolationTypeOut;
            set => SetField(ref _interpolationTypeOut, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(InValue):
                case nameof(OutValue):
                case nameof(InTangent):
                case nameof(OutTangent):
                    OwningTrack?.OnChanged();
                    break;
                case nameof(InterpolationTypeIn):
                    switch (_interpolationTypeIn)
                    {
                        case ERadialInterpType.Step:
                            _interpolateIn = Step;
                            break;
                        case ERadialInterpType.Linear:
                            _interpolateIn = Linear;
                            break;
                        case ERadialInterpType.Smooth:
                            _interpolateIn = CubicBezier;
                            break;
                    }
                    break;
                case nameof(InterpolationTypeOut):

                    switch (_interpolationTypeOut)
                    {
                        case ERadialInterpType.Step:
                            _interpolateOut = Step;
                            break;
                        case ERadialInterpType.Linear:
                            _interpolateOut = Linear;
                            break;
                        case ERadialInterpType.Smooth:
                            _interpolateOut = CubicBezier;
                            break;
                    }
                    break;
            }
        }

        public Quaternion Interpolate(float desiredSecond)
            => Interpolate(desiredSecond, out _, out _, out _);

        public Quaternion Interpolate(
            float desiredSecond,
            out QuaternionKeyframe? prevKey,
            out QuaternionKeyframe? nextKey,
            out float normalizedTime)
        {
            prevKey = this;
            nextKey = Next;

            float span, diff;
            QuaternionKeyframe? key1, key2;

            if (desiredSecond >= Second)
            {
                if (IsLast || Next!.Second > TrackLength)
                {
                    if (OwningTrack?.FirstKey != this)
                    {
                        QuaternionKeyframe? first = OwningTrack?.FirstKey as QuaternionKeyframe;
                        span = TrackLength - Second + (first?.Second ?? 0.0f);
                        diff = desiredSecond - Second;
                        key1 = this;
                        key2 = first;
                    }
                    else
                    {
                        normalizedTime = 0.0f;
                        return OutValue;
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
                    return Next.Interpolate(desiredSecond, out prevKey, out nextKey, out normalizedTime);
                }
            }
            else //desiredSecond < Second
            {
                if (!IsFirst)
                    return Prev!.Interpolate(desiredSecond, out prevKey, out nextKey, out normalizedTime);

                QuaternionKeyframe? last = OwningTrack?.GetKeyBeforeGeneric(TrackLength) as QuaternionKeyframe;

                if (last != this && last != null)
                {
                    span = TrackLength - last.Second + Second;
                    diff = TrackLength - last.Second + desiredSecond;
                    key1 = last;
                    key2 = this;
                }
                else
                {
                    normalizedTime = 0.0f;
                    return InValue;
                }
            }

            normalizedTime = diff / span;

            if (key2 is null)
                return key1.OutValue;

            if (key1.InterpolationTypeOut == key2.InterpolationTypeIn)
                return _interpolateOut(key1, key2, normalizedTime);

            var outInterp = key1._interpolateOut(key1, key2, normalizedTime);
            var inInterp = key2._interpolateIn(key1, key2, normalizedTime);
            return Quaternion.Slerp(outInterp, inInterp, normalizedTime);
        }

        public static Quaternion Step(QuaternionKeyframe? key1, QuaternionKeyframe? key2, float time)
            => time < 0.5f
            ? (key1?.OutValue ?? key2?.InValue ?? Quaternion.Identity)
            : (key2?.InValue ?? key1?.OutValue ?? Quaternion.Identity);

        public static Quaternion Linear(QuaternionKeyframe? key1, QuaternionKeyframe? key2, float time)
            => Quaternion.Slerp(
                key1?.OutValue ?? Quaternion.Identity,
                key2?.InValue ?? Quaternion.Identity,
                time);
        
        public static Quaternion CubicBezier(QuaternionKeyframe? key1, QuaternionKeyframe? key2, float time)
            => Interp.SCubic(
                key1?.OutValue ?? Quaternion.Identity,
                key1?.OutTangent ?? Quaternion.Identity,
                key2?.InTangent ?? Quaternion.Identity,
                key2?.InValue ?? Quaternion.Identity,
                time);

        public override string WriteToString()
            => string.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8} {9} {10}", Second, InValue.X, InValue.Y, InValue.Z, InValue.W, OutValue.X, OutValue.Y, OutValue.Z, OutValue.W, InterpolationTypeIn, InterpolationTypeOut);

        public override void ReadFromString(string str)
        {
            string[] parts = str.Split(' ');
            Second = float.Parse(parts[0]);
            InValue = new Quaternion(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]), float.Parse(parts[4]));
            OutValue = new Quaternion(float.Parse(parts[5]), float.Parse(parts[6]), float.Parse(parts[7]), float.Parse(parts[8]));
            InTangent = new Quaternion(float.Parse(parts[9]), float.Parse(parts[10]), float.Parse(parts[11]), float.Parse(parts[12]));
            OutTangent = new Quaternion(float.Parse(parts[13]), float.Parse(parts[14]), float.Parse(parts[15]), float.Parse(parts[16]));
            InterpolationTypeIn = parts[17].AsEnum<ERadialInterpType>();
            InterpolationTypeOut = parts[18].AsEnum<ERadialInterpType>();
        }
    }
}
