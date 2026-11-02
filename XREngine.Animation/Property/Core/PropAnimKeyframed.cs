using System.Collections;
using System.ComponentModel;
using MemoryPack;

namespace XREngine.Animation
{
    public abstract class PropAnimKeyframed<TKeyframe> : BasePropAnimKeyframed, IEnumerable<TKeyframe> where TKeyframe : Keyframe, new()
    {
        public delegate T2 DelGetValue<T2>(float second);

        protected KeyframeTrack<TKeyframe> _keyframes = [];

        [MemoryPackIgnore]
        private TKeyframe? _sampleKeyHint;
        [MemoryPackIgnore]
        private int _sampleKeyHintRevision;

        /// <summary>
        /// Sets the key that the next sample search starts from. The search validates this key,
        /// so any key gives the same result.
        /// </summary>
        internal void SetSampleKeyHint(TKeyframe? key)
        {
            _sampleKeyHint = key;
            _sampleKeyHintRevision = _keyframes.Revision;
        }

        /// <summary>
        /// Finds the last key at or before the given second. The hint only sets where the walk
        /// starts. It is ignored if the key is not in this track or the track changed.
        /// The hint is separate from playback state and has no effect on the result.
        /// </summary>
        protected TKeyframe? FindSampleKey(float second)
        {
            KeyframeTrack<TKeyframe> track = _keyframes;
            TKeyframe? start = _sampleKeyHint is { } hint
                && ReferenceEquals(hint.OwningTrack, track)
                && _sampleKeyHintRevision == track.Revision
                    ? hint
                    : null;

            TKeyframe? key = track.FindKeyBefore(second, start, out _);
            _sampleKeyHint = key;
            _sampleKeyHintRevision = track.Revision;
            return key;
        }

        /// <summary>
        /// Gets a key that gives the same interpolation as a walk from the first key. A key past
        /// the track length is outside the range of that walk, so it starts from the first key.
        /// </summary>
        protected TKeyframe? GetSampleStartKey(float second)
        {
            TKeyframe? key = FindSampleKey(second);
            return key is null || key.Second > _keyframes.LengthInSeconds
                ? _keyframes.First
                : key;
        }

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
