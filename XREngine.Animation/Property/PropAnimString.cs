using Extensions;

namespace XREngine.Animation
{
    public class PropAnimString : PropAnimKeyframed<StringKeyframe>, IEnumerable<StringKeyframe>
    {
        private DelGetValue<string?> _getValue;

        private string?[]? _baked = null;
        /// <summary>
        /// The default value to return when no keyframes are set.
        /// </summary>
        public string DefaultValue { get; set; } = string.Empty;

        public PropAnimString() : base(0.0f, false)
        {
            _getValue = GetValueKeyframed;
        }
        public PropAnimString(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes)
        {
            _getValue = GetValueKeyframed;
        }
        public PropAnimString(int frameCount, float FPS, bool looped, bool useKeyframes) 
            : base(frameCount, FPS, looped, useKeyframes)
        {
            _getValue = GetValueKeyframed;
        }

        protected override void BakedChanged()
            => _getValue = !IsBaked ? GetValueKeyframed : GetValueBaked;

        public string? GetValue(float second)
            => _getValue(second);
        public override object? GetCurrentValueGeneric()
            => GetValue(CurrentTime);
        public override object? GetValueGeneric(float second)
            => _getValue(second);
        public string? GetValueBaked(float second)
            => GetValueBaked((int)Math.Floor(second * BakedFramesPerSecond));
        public string? GetValueBaked(int frameIndex)
            => _baked?.TryGet(frameIndex);
        public string? GetValueKeyframed(float second)
        {
            StringKeyframe? key = Keyframes?.GetKeyBefore(second);
            if (key != null)
                return key.Value;
            return DefaultValue;
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
            //if (IsBaked)
            //{
            //    CurrentValue = GetValueBakedBySecond(_currentTime);
            //    return;
            //}

            //_prevKeyframe ??= Keyframes.GetKeyBefore(_currentTime);

            //if (Keyframes.Count == 0)
            //{
            //    CurrentValue = DefaultValue;
            //    return;
            //}

            //float second = _currentTime;
            //CurrentValue = _prevKeyframe?.Interpolate(second,
            //    out _prevKeyframe,
            //    out _,
            //    out _) ?? DefaultValue;
        }
    }
}
