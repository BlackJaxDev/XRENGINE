namespace XREngine.Animation
{
    public abstract class BasePropAnimKeyframed : BasePropAnimBakeable
    {
        public BasePropAnimKeyframed(float lengthInSeconds, bool looped, bool useKeyframes = false)
            : base(lengthInSeconds, looped, useKeyframes) { }
        public BasePropAnimKeyframed(int frameCount, float framesPerSecond, bool looped, bool useKeyframes = false)
            : base(frameCount, framesPerSecond, looped, useKeyframes) { }
        
        protected abstract BaseKeyframeTrack InternalKeyframes { get; }

        internal virtual void PublishExactLengthChange(float previousTrackLength,
            Action validateState)
            => throw new NotSupportedException("This animation cannot publish an exact length edit.");
        internal virtual bool HasExclusiveLengthRelay => false;
        internal virtual bool CanBeginExactLengthEdit => false;

        public override void SetLength(float lengthInSeconds, bool stretchAnimation, bool notifyChanged = true)
        {
            if (lengthInSeconds < 0.0f)
                return;
            InternalKeyframes.SetLength(lengthInSeconds, stretchAnimation, notifyChanged, notifyChanged);
            base.SetLength(lengthInSeconds, stretchAnimation, notifyChanged);
        }
    }
}
