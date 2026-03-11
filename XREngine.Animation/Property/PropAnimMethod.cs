namespace XREngine.Animation
{
    /// <summary>
    /// Retrieves an animated value by executing a method with the current animation time.
    /// </summary>
    /// <typeparam name="T">The type of value to animate.</typeparam>
    public class PropAnimMethod<T> : BasePropAnimBakeable
    {
        public delegate T? DelGetValue(float second);

        private DelGetValue? _tickMethod = null;
        public DelGetValue? TickMethod
        {
            get => _tickMethod;
            set
            {
                _tickMethod = value;
                if (!IsBaked)
                    GetValue = _tickMethod;
            }
        }
        public DelGetValue? GetValue { get; private set; }
        
        private T?[]? _baked = null;
        /// <summary>
        /// The default value to return when the tick method is not set.
        /// </summary>
        public T? DefaultValue { get; set; }
        
        public PropAnimMethod() 
            : base(0.0f, false) { }
        public PropAnimMethod(float lengthInSeconds, bool looped)
            : base(lengthInSeconds, looped) { }
        public PropAnimMethod(int frameCount, float FPS, bool looped)
            : base(frameCount, FPS, looped) { }

        public PropAnimMethod(DelGetValue method) 
            : base(0.0f, false) => TickMethod = method;
        public PropAnimMethod(float lengthInSeconds, bool looped, DelGetValue method)
            : base(lengthInSeconds, looped) => TickMethod = method;
        public PropAnimMethod(int frameCount, float FPS, bool looped, DelGetValue method)
            : base(frameCount, FPS, looped) => TickMethod = method;

        public T? GetValueMethod(float second)
            => TickMethod != null ? TickMethod(second) : DefaultValue;
        public override object? GetCurrentValueGeneric()
            => GetValueGeneric(CurrentTime);
        public override object? GetValueGeneric(float second)
            => GetValue != null ? GetValue(second) : DefaultValue;
        public T? GetValueBaked(float second)
            => _baked is { Length: > 0 } ? _baked[Math.Clamp(GetBakedFrameIndex(second), 0, _baked.Length - 1)] : DefaultValue;
        public T? GetValueBaked(int frameIndex)
            => _baked != null ? _baked[frameIndex] : DefaultValue;

        protected override void BakedChanged()
            => GetValue = !IsBaked ? GetValueMethod : GetValueBaked;

        public override void Bake(int framesPerSecond)
        {
            _bakedFPS = Math.Max(0, framesPerSecond);
            _bakedFrameCount = _bakedFPS <= 0 ? 0 : (int)Math.Ceiling(LengthInSeconds * _bakedFPS);
            _baked = new T[BakedFrameCount];
            if (_bakedFPS <= 0)
                return;

            float invFPS = 1.0f / _bakedFPS;
            for (int i = 0; i < BakedFrameCount; ++i)
                _baked[i] = GetValueMethod(i * invFPS);
        }
        protected override void OnProgressed(float delta) { }
    }
}