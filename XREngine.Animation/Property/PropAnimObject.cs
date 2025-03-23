using Extensions;

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

        public object? GetValue(float second)
            => _getValue(second);
        protected override object? GetValueGeneric(float second)
            => _getValue(second);
        public object? GetValueBaked(float second)
            => GetValueBaked((int)Math.Floor(second * BakedFramesPerSecond));
        public object? GetValueBaked(int frameIndex)
            => _baked?.TryGet(frameIndex) ?? string.Empty;
        public object? GetValueKeyframed(float second)
        {
            ObjectKeyframe? key = Keyframes?.GetKeyBefore(second);
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

        protected override object? GetCurrentValueGeneric()
            => GetValue(CurrentTime);

        protected override void OnProgressed(float delta)
        {

        }
    }
}
