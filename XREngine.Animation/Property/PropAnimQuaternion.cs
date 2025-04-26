using Extensions;
using System.ComponentModel;
using System.Numerics;
using YamlDotNet.Core.Tokens;

namespace XREngine.Animation
{
    public class PropAnimQuaternion : PropAnimKeyframed<QuaternionKeyframe>, IEnumerable<QuaternionKeyframe>
    {
        public event Action<PropAnimQuaternion>? ConstrainKeyframedFPSChanged;
        public event Action<PropAnimQuaternion>? LerpConstrainedFPSChanged;

        private DelGetValue<Quaternion> _getValue;
        private Quaternion[]? _baked = null;

        private Quaternion _defaultValue = Quaternion.Identity;
        /// <summary>
        /// The default value to return when no keyframes are set.
        /// </summary>
        public Quaternion DefaultValue
        {
            get => _defaultValue;
            set => SetField(ref _defaultValue, value);
        }

        public PropAnimQuaternion() : base(0.0f, false)
            => _getValue = !IsBaked ? GetValueKeyframed : GetValueBakedBySecond;
        public PropAnimQuaternion(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes)
            => _getValue = !IsBaked ? GetValueKeyframed : GetValueBakedBySecond;
        public PropAnimQuaternion(int frameCount, float FPS, bool looped, bool useKeyframes)
            : base(frameCount, FPS, looped, useKeyframes)
            => _getValue = !IsBaked ? GetValueKeyframed : GetValueBakedBySecond;

        protected override void BakedChanged()
            => _getValue = !IsBaked ? GetValueKeyframed : GetValueBakedBySecond;

        protected void OnConstrainKeyframedFPSChanged()
            => ConstrainKeyframedFPSChanged?.Invoke(this);

        protected void OnLerpConstrainedFPSChanged()
            => LerpConstrainedFPSChanged?.Invoke(this);

        private bool _constrainKeyframedFPS = false;
        public bool ConstrainKeyframedFPS
        {
            get => _constrainKeyframedFPS;
            set
            {
                _constrainKeyframedFPS = value;
                OnConstrainKeyframedFPSChanged();
            }
        }
        private bool _lerpConstrainedFPS = false;
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
                SetField(ref _lerpConstrainedFPS, value);
                OnLerpConstrainedFPSChanged();
            }
        }

        public Quaternion GetValue(float second)
            => _getValue(second);
        public override object GetCurrentValueGeneric()
            => GetValue(CurrentTime);
        public override object GetValueGeneric(float second)
            => _getValue(second);
        public Quaternion GetValueBakedBySecond(float second)
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
                        Quaternion t1 = _baked[frame];
                        Quaternion t2 = _baked[0];

                        //TODO: interpolate values by creating tangents dynamically?

                        //Span is always 1 frame, so no need to divide to normalize
                        float lerpTime = frameTime - frame;

                        return Quaternion.Slerp(t1, t2, lerpTime);
                    }
                    return _baked[frame];
                }
                else
                {
                    Quaternion t1 = _baked[frame];
                    Quaternion t2 = _baked[frame + 1];

                    //TODO: interpolate values by creating tangents dynamically?

                    //Span is always 1 frame, so no need to divide to normalize
                    float lerpTime = frameTime - frame;

                    return Quaternion.Slerp(t1, t2, lerpTime);
                }
            }
            else if (_baked.IndexInRangeArrayT(frame))
                return _baked[frame];
            else
                return DefaultValue;
        }
        public Quaternion GetValueBakedByFrame(int frame)
        {
            if (_baked is null)
                throw new InvalidOperationException("Cannot get baked value when not baked.");

            if (!_baked.IndexInRangeArrayT(frame))
                return Quaternion.Identity;

            return _baked[frame.Clamp(0, _baked.Length - 1)];
        }
        public Quaternion GetValueKeyframed(float second)
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
                    return LerpKeyedValues(floorSec, ceilSec, time);
                }

                second = floorSec;
            }

            return Keyframes.First?.Interpolate(second) ?? DefaultValue;
        }
        private Quaternion LerpKeyedValues(float floorSec, float ceilSec, float time)
        {
            QuaternionKeyframe? prevKey = null;

            Quaternion? floorValue = _keyframes.First?.Interpolate(
                floorSec,
                out prevKey,
                out _,
                out _);

            Quaternion? ceilValue = prevKey?.Interpolate(ceilSec);

            if (floorValue is null || ceilValue is null)
                return DefaultValue;

            return Quaternion.Slerp(floorValue.Value, ceilValue.Value, time);
        }
        public override void Bake(float framesPerSecond)
        {
            _bakedFPS = framesPerSecond;
            _bakedFrameCount = (int)Math.Ceiling(LengthInSeconds * framesPerSecond);
            _baked = new Quaternion[BakedFrameCount];
            float invFPS = 1.0f / _bakedFPS;
            for (int i = 0; i < BakedFrameCount; ++i)
                _baked[i] = GetValueKeyframed(i * invFPS);
        }

        public event Action<PropAnimQuaternion>? CurrentValueChanged;

        private Quaternion _currentValue = Quaternion.Identity;
        /// <summary>
        /// The value at the current time.
        /// </summary>
        public Quaternion CurrentValue
        {
            get => _currentValue;
            private set
            {
                _currentValue = value;
                CurrentValueChanged?.Invoke(this);
            }
        }

        private QuaternionKeyframe? _prevKeyframe;

        protected override void OnProgressed(float delta)
        {
            //TODO: assign separate functions to be called by OnProgressed to avoid if statements and returns

            if (IsBaked)
            {
                CurrentValue = GetValueBakedBySecond(_currentTime);
                return;
            }

            _prevKeyframe ??= Keyframes.GetKeyBefore(_currentTime);

            if (Keyframes.Count == 0)
            {
                CurrentValue = DefaultValue;
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
                    var floorPosition = _prevKeyframe?.Interpolate(
                        floorSec,
                        out _prevKeyframe,
                        out _,
                        out _) ?? Quaternion.Identity;

                    var ceilPosition = _prevKeyframe?.Interpolate(
                        ceilSec,
                        out _,
                        out _,
                        out _) ?? Quaternion.Identity;

                    CurrentValue = Quaternion.Slerp(floorPosition, ceilPosition, time);
                    return;
                }
                second = floorSec;
            }

            CurrentValue = _prevKeyframe?.Interpolate(second,
                out _prevKeyframe,
                out _,
                out _) ?? DefaultValue;
        }
    }
}
