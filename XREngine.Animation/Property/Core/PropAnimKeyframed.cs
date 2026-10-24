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
        private BaseKeyframeTrack? _exactLengthTrack;
        private float _exactLength;
        private float _exactTrackLength;
        private int _exactLengthFrameCount;
        private ulong _exactLengthGeneration;
        private void KeyframesLengthChanged(float oldValue, BaseKeyframeTrack track)
        {
            if (_updatingLength)
                return;
            if (ReferenceEquals(track.ExactLengthNotificationOwner, this) &&
                ReferenceEquals(track, _exactLengthTrack) &&
                track.ExactEditGeneration == _exactLengthGeneration &&
                track.LengthInSeconds == _exactTrackLength &&
                _lengthInSeconds == _exactLength &&
                _bakedFrameCount == _exactLengthFrameCount)
                return;

            _updatingLength = true;
            try
            {
                SetLength(track.LengthInSeconds, false, true);
            }
            finally
            {
                _updatingLength = false;
            }
        }

        protected override BaseKeyframeTrack InternalKeyframes => Keyframes;

        internal override bool HasExclusiveLengthRelay
            => _keyframes.HasExclusiveLengthRelay(KeyframesLengthChanged);

        internal override bool CanBeginExactLengthEdit
            => !_updatingLength && _exactLengthTrack is null;

        internal override void PublishExactLengthChange(float previousTrackLength,
            Action validateState)
        {
            if (_updatingLength || _exactLengthTrack is not null)
                throw new InvalidOperationException("Animation length notification is already active.");
            BaseKeyframeTrack track = _keyframes;
            _exactLengthTrack = track;
            _exactLength = _lengthInSeconds;
            _exactTrackLength = track.LengthInSeconds;
            _exactLengthFrameCount = _bakedFrameCount;
            _exactLengthGeneration = track.ExactEditGeneration;
            try
            {
                track.PublishLengthChanged(previousTrackLength, this);
                validateState();
                track.OnChanged();
                validateState();
                OnLengthChanged();
                validateState();
            }
            finally
            {
                _exactLengthTrack = null;
            }
        }

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
