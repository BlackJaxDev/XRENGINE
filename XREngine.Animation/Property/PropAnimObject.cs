using XREngine.Extensions;
using YamlDotNet.Core.Tokens;

namespace XREngine.Animation
{
    public class PropAnimObject : PropAnimKeyframed<ObjectKeyframe>, IEnumerable<ObjectKeyframe>
    {
        private DelGetValue<object?> _getValue;

        private BakedValueStore<object?>? _baked = null;

        internal object?[] CapturePublishedBakedValues() => _baked?.CaptureValues() ?? [];

        internal void RestorePublishedBakedValues(object?[] values, EAnimationValueCompressionAlgorithm encodedCompression)
            => _baked = BakedValueStore<object?>.Encode(values, encodedCompression);
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
            => GetValueBaked(GetBakedFrameIndex(second));
        public object? GetValueBaked(int frameIndex)
            => _baked is not null && (uint)frameIndex < (uint)_baked.Count
                ? _baked.GetValue(frameIndex)
                : string.Empty;
        public object? GetValueKeyframed(float second)
        {
            second = Keyframes.ResolveSampleTime(second, out _, out _);
            ObjectKeyframe? key = Keyframes?.GetKeyBefore(second);
            return key != null ? key.Value : DefaultValue;
        }
        public override void Bake(int framesPerSecond)
        {
            SetBakeCadence(framesPerSecond);
            if (_bakedFPS <= 0)
            {
                _baked = EncodeBakedValues(Array.Empty<object?>());
                return;
            }

            object?[] baked = new object?[BakedFrameCount];
            float invFPS = 1.0f / _bakedFPS;
            for (int i = 0; i < BakedFrameCount; ++i)
                baked[i] = GetValueKeyframed(i * invFPS);

            _baked = EncodeBakedValues(baked);
        }
        protected override void OnProgressed(float delta)
            => _currentValue = GetPlaybackValue(_currentTime);

        /// <inheritdoc />
        public override void WriteSampleToStore(float second, AnimationValueStore store, in AnimSlot slot)
            => store.SetValue(slot, GetPlaybackValue(ResolveSampleSecond(second)));

        /// <summary>
        /// Gets the playback value for a time, with the discrete value rounding applied.
        /// </summary>
        private object? GetPlaybackValue(float second)
        {
            if (IsBaked)
                return GetValueBaked(second);

            if (Keyframes.Count == 0)
                return DefaultValue;

            float sampleTime = Keyframes.ResolveSampleTime(second, out _, out _);
            ObjectKeyframe? key = Keyframes.GetKeyBefore(sampleTime);

            //Discrete value rounding
            switch (DiscreteValueRounding)
            {
                default:
                case EDiscreteValueRounding.Floor:
                    return key?.Value;
                case EDiscreteValueRounding.Ceiling:
                    return key?.Next?.Value;
                case EDiscreteValueRounding.Nearest:
                    {
                        float prevSec = key?.Second ?? 0.0f;
                        float nextSec = key?.Next?.Second ?? 0.0f;
                        float range = nextSec - prevSec;
                        if (range == 0.0f)
                            return key?.Value;

                        float t = (sampleTime - prevSec) / range;
                        return t > 0.5f ? key?.Next?.Value : key?.Value;
                    }
            }
        }
    }
}
