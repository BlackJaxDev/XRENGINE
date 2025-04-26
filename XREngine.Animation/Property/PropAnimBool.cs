using Extensions;

namespace XREngine.Animation
{
    public class PropAnimBool : PropAnimKeyframed<BoolKeyframe>, IEnumerable<BoolKeyframe>
    {
        private DelGetValue<bool>? _getValue;
        
        private bool[]? _baked = null;
        /// <summary>
        /// The default value to return when no keyframes are set.
        /// </summary>
        public bool DefaultValue { get; set; } = false;

        public PropAnimBool() : base(0.0f, false) { }
        public PropAnimBool(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes) { }
        public PropAnimBool(int frameCount, float FPS, bool looped, bool useKeyframes) 
            : base(frameCount, FPS, looped, useKeyframes) { }

        protected override void BakedChanged()
            => _getValue = !IsBaked ? GetValueKeyframed : GetValueBaked;

        private bool _value = false;
        public override object GetCurrentValueGeneric() => _value;
        public bool GetValue(float second)
            => _getValue?.Invoke(second) ?? false;
        public override object GetValueGeneric(float second)
            => _getValue?.Invoke(second) ?? false;
        public bool GetValueBaked(float second)
            => GetValueBaked((int)Math.Floor(second * BakedFramesPerSecond));
        public bool GetValueBaked(int frameIndex)
            => _baked?.TryGet(frameIndex) ?? false;
        public bool GetValueKeyframed(float second)
        {
            BoolKeyframe? key = _keyframes.GetKeyBefore(second);
            return key != null ? key.Value : DefaultValue;
        }

        public override void Bake(float framesPerSecond)
        {
            _bakedFPS = framesPerSecond;
            _bakedFrameCount = (int)Math.Ceiling(LengthInSeconds * framesPerSecond);
            _baked = new bool[BakedFrameCount];
            float invFPS = 1.0f / _bakedFPS;
            for (int i = 0; i < BakedFrameCount; ++i)
                _baked[i] = GetValueKeyframed(i * invFPS);
        }

        protected override void OnProgressed(float delta)
        {
            _value = GetValue(CurrentTime);
        }
    }
}
