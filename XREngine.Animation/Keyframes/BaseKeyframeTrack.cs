using System.Collections;
using System.ComponentModel;
using MemoryPack;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Animation
{
    public delegate void DelLengthChange(float prevLength, BaseKeyframeTrack track);
    public abstract class BaseKeyframeTrack : XRBase, IEnumerable<Keyframe>
    {
        public event Action<BaseKeyframeTrack>? Changed;
        public event DelLengthChange? LengthChanged;

        [NonSerialized]
        private ulong _changedNotificationRevision;
        [NonSerialized]
        private bool _changedNotificationRevisionExhausted;

        [MemoryPackIgnore, YamlIgnore, Browsable(false)]
        internal ulong ChangedNotificationRevision => _changedNotificationRevision;

        [MemoryPackIgnore, YamlIgnore, Browsable(false)]
        internal bool CanAdvanceChangedNotificationRevision =>
            !_changedNotificationRevisionExhausted &&
            _changedNotificationRevision < ulong.MaxValue;

        internal bool CanReserveChangedNotifications(int advances)
            => advances >= 0 && !_changedNotificationRevisionExhausted &&
                _changedNotificationRevision < ulong.MaxValue - (ulong)advances;

        protected internal void OnChanged()
        {
            if (!CanAdvanceChangedNotificationRevision)
            {
                _changedNotificationRevisionExhausted = true;
                throw new InvalidOperationException("The animation track changed notification revision is exhausted.");
            }
            _changedNotificationRevision++;
            Changed?.Invoke(this);
        }
        [MemoryPackIgnore]
        internal object? ExactLengthNotificationOwner { get; private set; }

        protected internal void OnLengthChanged(float prevLength)
            => PublishLengthChanged(prevLength, null);

        internal void PublishLengthChanged(float prevLength, object? exactOwner)
        {
            object? previousOwner = ExactLengthNotificationOwner;
            ExactLengthNotificationOwner = exactOwner;
            try { LengthChanged?.Invoke(prevLength, this); }
            finally { ExactLengthNotificationOwner = previousOwner; }
        }

        internal bool HasExclusiveLengthRelay(DelLengthChange expected)
        {
            int matches = 0;
            if (LengthChanged is null)
                return false;
            foreach (Delegate handler in LengthChanged.GetInvocationList())
            {
                if (handler.Equals(expected))
                    matches++;
                else if (handler.Target is BasePropAnimKeyframed)
                    return false;
            }
            return matches == 1;
        }

        protected internal abstract Keyframe? FirstKey { get; internal set; }
        protected internal abstract Keyframe? LastKey { get; internal set; }

        [MemoryPackIgnore]
        internal ulong ExactEditGeneration { get; private set; }

        internal bool CanAdvanceExactEditGeneration => ExactEditGeneration < ulong.MaxValue;

        internal void AdvanceExactEditGeneration() => ExactEditGeneration++;

        internal bool TrySetExactLength(float expectedLength, float desiredLength,
            ReadOnlySpan<Keyframe> order, ReadOnlySpan<float> expectedSeconds,
            ReadOnlySpan<int> expectedFrames, ReadOnlySpan<float> desiredSeconds,
            ReadOnlySpan<int> desiredFrames)
        {
            if (!CanAdvanceExactEditGeneration || _lengthInSeconds != expectedLength ||
                !float.IsFinite(desiredLength) || desiredLength < 0 ||
                order.Length != expectedSeconds.Length || order.Length != expectedFrames.Length ||
                order.Length != desiredSeconds.Length || order.Length != desiredFrames.Length ||
                !HasExactOrder(order))
                return false;
            float lastSecond = float.NegativeInfinity;
            for (int index = 0; index < order.Length; index++)
            {
                Keyframe key = order[index];
                if (key.Second != expectedSeconds[index] ||
                    key.AuthoredFrameIndex != expectedFrames[index] ||
                    !float.IsFinite(desiredSeconds[index]) || desiredSeconds[index] < 0 ||
                    desiredSeconds[index] < lastSecond || desiredFrames[index] < -1)
                    return false;
                lastSecond = desiredSeconds[index];
            }
            SetField(ref _lengthInSeconds, desiredLength, publishNotifications: false,
                propertyName: nameof(LengthInSeconds));
            for (int index = 0; index < order.Length; index++)
                order[index].SetExactEditState(this,
                    index == 0 ? null : order[index - 1],
                    index + 1 == order.Length ? null : order[index + 1],
                    desiredSeconds[index], desiredFrames[index]);
            AdvanceExactEditGeneration();
            return true;
        }

        internal bool HasExactOrder(ReadOnlySpan<Keyframe> order)
        {
            if (Count != order.Length ||
                !ReferenceEquals(FirstKey,
                    order.Length == 0 ? null : order[0]) ||
                !ReferenceEquals(LastKey,
                    order.Length == 0 ? null : order[^1]))
                return false;
            Keyframe? previous = null;
            Keyframe? current = FirstKey;
            for (int index = 0; index < order.Length; index++)
            {
                if (current is null || !ReferenceEquals(current, order[index]) ||
                    !ReferenceEquals(current.OwningTrack, this) ||
                    !ReferenceEquals(current.Prev, previous) ||
                    !ReferenceEquals(current.Next,
                        index + 1 < order.Length ? order[index + 1] : null))
                    return false;
                previous = current;
                current = current.Next;
            }
            return current is null;
        }

        internal virtual bool TryApplyExactKeyEdit(
            ReadOnlySpan<Keyframe> expected, ReadOnlySpan<Keyframe> desired,
            Keyframe key, float expectedSecond, int expectedFrame,
            float desiredSecond, int desiredFrame) => false;

        private float _lengthInSeconds = 0.0f;
        private EKeyframeInfinityMode _preInfinityMode = EKeyframeInfinityMode.Loop;
        private EKeyframeInfinityMode _postInfinityMode = EKeyframeInfinityMode.Loop;

        [Browsable(false)]
        public int Count { get; internal set; } = 0;
        [Browsable(false)]
        public float LengthInSeconds
        {
            get => _lengthInSeconds;
            set => SetLength(value, false);
        }
        [Browsable(false)]
        public EKeyframeInfinityMode PreInfinityMode
        {
            get => _preInfinityMode;
            set => SetField(ref _preInfinityMode, value);
        }
        [Browsable(false)]
        public EKeyframeInfinityMode PostInfinityMode
        {
            get => _postInfinityMode;
            set => SetField(ref _postInfinityMode, value);
        }
        [Browsable(false)]
        public bool LoopsBeforeFirstKey => _preInfinityMode == EKeyframeInfinityMode.Loop;
        [Browsable(false)]
        public bool LoopsAfterLastKey => _postInfinityMode == EKeyframeInfinityMode.Loop;

        /// <summary>
        /// Resolves an arbitrary sample time against the authored key range.
        /// The returned velocity scale is negative on the reflected half of a
        /// ping-pong cycle and zero when the result was clamped.
        /// </summary>
        public float ResolveSampleTime(float second, out float velocityScale, out bool clamped)
        {
            velocityScale = 1.0f;
            clamped = false;

            if (FirstKey is null || LastKey is null)
                return 0.0f;

            float first = FirstKey.Second;
            float last = LastKey.Second;
            if (!float.IsFinite(second))
            {
                clamped = true;
                velocityScale = 0.0f;
                return first;
            }

            if (second >= first && second <= last)
                return second;

            EKeyframeInfinityMode mode = second < first ? PreInfinityMode : PostInfinityMode;
            float duration = last - first;
            if (duration <= 0.0f || mode is EKeyframeInfinityMode.Default
                or EKeyframeInfinityMode.Once
                or EKeyframeInfinityMode.Clamp
                or EKeyframeInfinityMode.ClampForever)
            {
                clamped = true;
                velocityScale = 0.0f;
                return second < first ? first : last;
            }

            double relative = second - first;
            if (mode == EKeyframeInfinityMode.Loop)
                return first + PositiveModulo(relative, duration);

            if (mode == EKeyframeInfinityMode.PingPong)
            {
                float cycle = PositiveModulo(relative, duration * 2.0f);
                if (cycle <= duration)
                    return first + cycle;

                velocityScale = -1.0f;
                return last - (cycle - duration);
            }

            clamped = true;
            velocityScale = 0.0f;
            return second < first ? first : last;
        }

        private static float PositiveModulo(double value, float modulus)
        {
            double remainder = value % modulus;
            if (remainder < 0.0)
                remainder += modulus;
            return (float)remainder;
        }

        public void SetLength(float seconds, bool stretch, bool notifyLengthChanged = true, bool notifyChanged = true)
        {
            float prevLength = LengthInSeconds;
            _lengthInSeconds = seconds;
            if (stretch && prevLength > 0.0f)
            {
                float ratio = seconds / prevLength;
                Keyframe? key = FirstKey;
                while (key != null)
                {
                    key.Second *= ratio;
                    key = key.Next;
                }
            }
            //else
            //{
            //    //Keyframe key = FirstKey;
            //    //while (key != null)
            //    //{
            //    //    if (key.Second < 0 || key.Second > LengthInSeconds)
            //    //        key.Remove();
            //    //    if (key.Next == FirstKey)
            //    //        break;
            //    //    key = key.Next;
            //    //}
            //}

            if (notifyLengthChanged)
                OnLengthChanged(prevLength);
            if (notifyChanged)
                OnChanged();
        }

        public void SetFrameCount(int numFrames, float framesPerSecond, bool stretchAnimation, bool notifyLengthChanged = true, bool notifyChanged = true)
            => SetLength(numFrames / framesPerSecond, stretchAnimation, notifyLengthChanged, notifyChanged);

        public Keyframe? GetKeyBeforeGeneric(float second)
        {
            Keyframe? bestKey = null;
            foreach (Keyframe key in this)
                if (second >= key.Second)
                    bestKey = key;
                else
                    break;
            return bestKey;
        }

        public abstract IEnumerator<Keyframe> GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
