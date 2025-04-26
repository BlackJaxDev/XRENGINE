using Extensions;
using YamlDotNet.Core.Tokens;

namespace XREngine.Animation
{
    public class PropAnimObject : PropAnimKeyframed<ObjectKeyframe>, IEnumerable<ObjectKeyframe>
    {
        private DelGetValue<object?> _getValue;

        private object?[]? _baked = null;
        /// <summary>
        /// The default value to return when no keyframes are set.
        /// </summary>
        public object? DefaultValue { get; set; } = null;

        public PropAnimObject() : base(0.0f, false)
            => _getValue = GetValueKeyframed;
        public PropAnimObject(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes)
            => _getValue = GetValueKeyframed;
        public PropAnimObject(int frameCount, float FPS, bool looped, bool useKeyframes)
            : base(frameCount, FPS, looped, useKeyframes)
            => _getValue = GetValueKeyframed;

        protected override void BakedChanged()
            => _getValue = !IsBaked ? GetValueKeyframed : GetValueBaked;

        private object? _currentValue = null;
        private ObjectKeyframe? _prevKeyframe;

        public enum EDiscreteValueRounding
        {
            /// <summary>
            /// Uses the last keyframe value.
            /// </summary>
            Floor,
            /// <summary>
            /// Uses the next keyframe value.
            /// </summary>
            Ceiling,
            /// <summary>
            /// Uses the nearest keyframe value; for example, if over halfway between two keyframes it will use the next keyframe value, and if under halfway it will use the previous keyframe value.
            /// </summary>
            Nearest
        }

        private EDiscreteValueRounding _discreteValueRounding = EDiscreteValueRounding.Nearest;
        public EDiscreteValueRounding DiscreteValueRounding
        {
            get => _discreteValueRounding;
            set => SetField(ref _discreteValueRounding, value);
        }

        public object? GetValue(float second)
            => _getValue(second);
        public override object? GetCurrentValueGeneric()
            => _currentValue;
        public override object? GetValueGeneric(float second)
            => _getValue(second);
        public object? GetValueBaked(float second)
            => GetValueBaked((int)Math.Floor(second * BakedFramesPerSecond));
        public object? GetValueBaked(int frameIndex)
            => _baked?.TryGet(frameIndex) ?? string.Empty;
        public object? GetValueKeyframed(float second)
        {
            ObjectKeyframe? key = Keyframes?.GetKeyBefore(second);
            return key != null ? key.Value : DefaultValue;
        }
        public override void Bake(float framesPerSecond)
        {
            _bakedFPS = framesPerSecond;
            _bakedFrameCount = (int)Math.Ceiling(LengthInSeconds * framesPerSecond);
            _baked = new string[BakedFrameCount];
            float invFPS = 1.0f / _bakedFPS;
            for (int i = 0; i < BakedFrameCount; ++i)
                _baked[i] = GetValueKeyframed(i * invFPS);
        }
        protected override void OnProgressed(float delta)
        {
            if (IsBaked)
            {
                _currentValue = GetValueBaked(_currentTime);
                return;
            }

            _prevKeyframe ??= Keyframes.GetKeyBefore(_currentTime);

            if (Keyframes.Count == 0)
            {
                _currentValue = DefaultValue;
                return;
            }

            //Discrete value rounding
            switch (DiscreteValueRounding)
            {
                default:
                case EDiscreteValueRounding.Floor:
                    _currentValue = _prevKeyframe?.Value;
                    break;
                case EDiscreteValueRounding.Ceiling:
                    _currentValue = _prevKeyframe?.Next?.Value;
                    break;
                case EDiscreteValueRounding.Nearest:
                    {
                        float prevSec = _prevKeyframe?.Second ?? 0.0f;
                        float nextSec = _prevKeyframe?.Next?.Second ?? 0.0f;
                        float range = nextSec - prevSec;
                        if (range == 0.0f)
                            _currentValue = _prevKeyframe?.Value;
                        else
                        {
                            float t = (_currentTime - prevSec) / range;
                            if (t > 0.5f)
                                _currentValue = _prevKeyframe?.Next?.Value;
                            else
                                _currentValue = _prevKeyframe?.Value;
                        }
                    }
                    break;
            }
        }
    }
}
