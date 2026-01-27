using System.Collections;
using System.ComponentModel;

namespace XREngine.Animation
{
    public abstract class PropAnimKeyframed<TKeyframe> : BasePropAnimKeyframed, IEnumerable<TKeyframe> where TKeyframe : Keyframe, new()
    {
        public delegate T2 DelGetValue<T2>(float second);

        protected KeyframeTrack<TKeyframe> _keyframes = [];

        public PropAnimKeyframed()
            : this(0.0f, false) { }
        public PropAnimKeyframed(float lengthInSeconds, bool looped, bool useKeyframes = true)
            : base(lengthInSeconds, looped, useKeyframes) => LinkKeyframeEvents();
        public PropAnimKeyframed(int frameCount, float framesPerSecond, bool looped, bool useKeyframes = true)
            : base(frameCount, framesPerSecond, looped, useKeyframes) => LinkKeyframeEvents();

        public override float LengthInSeconds
        {
            get => base.LengthInSeconds;
            set
            {
                base.LengthInSeconds = value;
                _keyframes.LengthInSeconds = value;
            }
        }

        private void LinkKeyframeEvents()
        {
            if (_keyframes is null)
                return;

            _keyframes.LengthChanged += KeyframesLengthChanged;
            _keyframes.LengthInSeconds = LengthInSeconds;
        }
        private void UnlinkKeyframeEvents()
        {
            if (_keyframes is null)
                return;

            _keyframes.LengthChanged -= KeyframesLengthChanged;
        }

        private bool _updatingLength = false;
        private void KeyframesLengthChanged(float oldValue, BaseKeyframeTrack track)
        {
            if (_updatingLength)
                return;

            _updatingLength = true;
            SetLength(track.LengthInSeconds, false, true);
            _updatingLength = false;
        }

        protected override BaseKeyframeTrack InternalKeyframes => Keyframes;

        [Category("Keyframed Property Animation")]
        public KeyframeTrack<TKeyframe> Keyframes
        {
            get => _keyframes;
            set => SetField(ref _keyframes, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                if (propName == nameof(Keyframes))
                    UnlinkKeyframeEvents();
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(Keyframes))
                LinkKeyframeEvents();
        }

        /// <summary>
        /// Appends the keyframes of the given animation to the end of this one.
        /// Basically, where this animation currently ends is where the given will begin, all in one animation.
        /// </summary>
        public void Append(PropAnimKeyframed<TKeyframe> other)
            => Keyframes.Append(other.Keyframes);

        public IEnumerator<TKeyframe> GetEnumerator() => ((IEnumerable<TKeyframe>)Keyframes).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<TKeyframe>)Keyframes).GetEnumerator();
    }
}
