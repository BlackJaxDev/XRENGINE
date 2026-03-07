using System.Collections;
using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public delegate void DelLengthChange(float prevLength, BaseKeyframeTrack track);
    public abstract class BaseKeyframeTrack : XRBase, IEnumerable<Keyframe>
    {
        public event Action<BaseKeyframeTrack>? Changed;
        public event DelLengthChange? LengthChanged;

        protected internal void OnChanged() => Changed?.Invoke(this);
        protected internal void OnLengthChanged(float prevLength) => LengthChanged?.Invoke(prevLength, this);

        protected internal abstract Keyframe? FirstKey { get; internal set; }
        protected internal abstract Keyframe? LastKey { get; internal set; }

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
