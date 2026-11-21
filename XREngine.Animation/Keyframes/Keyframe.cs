using XREngine.Extensions;
using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Animation
{
    /// <summary>
    /// Represents a keyframe in an animation.
    /// Stored as a linked list, because animations will be playing forward and backward, but usually not seeking.
    /// </summary>
    public abstract class Keyframe : XRBase, IKeyframe
    {
        protected float _second;
        protected int _authoredFrameIndex = -1;
        protected Keyframe? _next;
        protected Keyframe? _prev;
        protected BaseKeyframeTrack? _owningTrack = null;

        public Keyframe()
        {
            _next = null;
            _prev = null;
            OwningTrack = null;
        }

        [Category("Keyframe")]
        public float Second
        {
            get => _second;
            set
            {
                float nextSecond = value.ClampMin(0.0f);
                if (!float.IsFinite(nextSecond))
                    nextSecond = 0.0f;

                bool timeChanged = MathF.Abs(_second - nextSecond) > 0.000001f;
                _second = nextSecond;
                if (timeChanged)
                    _authoredFrameIndex = -1;

                Keyframe? kf = Prev ?? Next;
                kf?.UpdateLink(this, false);
                OwningTrack?.OnChanged();
            }
        }

        [Category("Keyframe")]
        public int AuthoredFrameIndex
        {
            get => _authoredFrameIndex;
            set => _authoredFrameIndex = value < 0 ? -1 : value;
        }

        // Next and Prev are read-only in every keyframe type. Only the track changes the links, through UpdateLink.
        // The reflection and YAML serializers write every property that has a setter. A Next or Prev setter makes them
        // write the whole list with each key, and the read sets links on keys that are only partly read.
        public Keyframe? Next => _next;
        public Keyframe? Prev => _prev;
        public bool IsFirst => _prev is null;
        public bool IsLast => _next is null;

        /// <summary>
        /// Runtime linkage rebuilt when a keyframe is inserted into a track.
        /// Serializing it would recursively walk the owning list for every item.
        /// </summary>
        [Browsable(false)]
        [MemoryPackIgnore]
        [YamlIgnore]
        public BaseKeyframeTrack? OwningTrack
        {
            get => _owningTrack;
            internal set
            {
                // Walks the list in loops, not recursion: a long track would overflow the stack.
                _owningTrack = value;
                for (Keyframe? key = _next; key is not null && key._owningTrack != value; key = key._next)
                    key._owningTrack = value;
                for (Keyframe? key = _prev; key is not null && key._owningTrack != value; key = key._prev)
                    key._owningTrack = value;
            }
        }

        [Browsable(false)]
        public abstract Type ValueType { get; }

        protected static float GetSecondForAuthoredFrame(int frameIndex, float framesPerSecond)
            => framesPerSecond > 0.0f && float.IsFinite(framesPerSecond)
                ? Math.Max(0, frameIndex) / framesPerSecond
                : 0.0f;

        protected void TrySetAuthoredFrameIndex(int frameIndex, float framesPerSecond)
            => AuthoredFrameIndex = frameIndex >= 0 && AuthoredCadence.TryNormalizeFramesPerSecond(framesPerSecond, out _)
                ? frameIndex
                : -1;

        /// <summary>
        /// Moves <paramref name="key"/> into this keyframe's list at the position of its time.
        /// The search starts at this keyframe and walks forward or backward until it finds the
        /// neighbors of the key's time, then links the key between them. The walk is a loop, so a
        /// track with many keyframes (for example a long clip read from YAML) cannot overflow the stack.
        /// </summary>
        public void UpdateLink(Keyframe? key, bool notifyChange = true)
        {
            if (key is null || key == this)
                return;

            key.Remove(key.OwningTrack != OwningTrack);
            key.Second = key.Second.RemapToRange(0.0f, (OwningTrack?.LengthInSeconds ?? 0.0f) + 0.0001f);

            Keyframe anchor = this;
            if (key.Second >= anchor.Second)
            {
                // After the anchor: walk forward past every keyframe at or before the key's time.
                while (anchor._next is { } next && key.Second >= next.Second)
                {
                    // An out-of-order list has no valid position for the key; leave it unlinked.
                    if (next.Second < anchor.Second)
                        return;
                    anchor = next;
                }
                anchor.LinkAfter(key, notifyChange);
            }
            else
            {
                // Before the anchor: walk backward past every keyframe after the key's time.
                while (anchor._prev is { } prev && key.Second < prev.Second)
                {
                    if (prev.Second >= anchor.Second)
                        return;
                    anchor = prev;
                }
                anchor.LinkBefore(key, notifyChange);
            }
        }

        /// <summary>Links the unlinked <paramref name="key"/> between this keyframe and its next keyframe.</summary>
        private void LinkAfter(Keyframe key, bool notifyChange)
        {
            key._next = _next;
            _next?._prev = key;
            key._prev = this;
            _next = key;
            PostKeyLinkUpdate(key, notifyChange);
        }

        /// <summary>Links the unlinked <paramref name="key"/> between this keyframe and its previous keyframe.</summary>
        private void LinkBefore(Keyframe key, bool notifyChange)
        {
            key._prev = _prev;
            _prev?._next = key;
            key._next = this;
            _prev = key;
            PostKeyLinkUpdate(key, notifyChange);
        }

        private void PostKeyLinkUpdate(Keyframe key, bool notifyChange)
        {
            //Get owning track from next or prev
            key.OwningTrack = !key.IsFirst 
                ? (key.Prev?.OwningTrack) 
                : !key.IsLast 
                    ? (key.Next?.OwningTrack) 
                    : throw new Exception("Keyframe is not linked to any track after linking.");

            //Update owning track first and last references
            if (key.OwningTrack != null)
            {
                if (key.IsFirst)
                    key.OwningTrack.FirstKey = key;
                if (key.IsLast)
                    key.OwningTrack.LastKey = key;
            }

            if (OwningTrack != null)
            {
                ++OwningTrack.Count;
                if (notifyChange)
                    OwningTrack.OnChanged();
            }

            if (key._prev == key ||
                key._next == key ||
                (key._next == key._prev && key._next != null))
                throw new Exception("Keyframe is linked to itself or has invalid next/prev references.");
        }

        public abstract string WriteToString();
        public abstract void ReadFromString(string str);
        public override string ToString() => WriteToString();

        public void Remove(bool notifyChange = true)
        {
            _next?._prev = _prev;
            _prev?._next = _next;

            if (OwningTrack != null)
            {
                if (IsFirst)
                    OwningTrack.FirstKey = _next;
                if (IsLast)
                    OwningTrack.LastKey = _prev;

                --OwningTrack.Count;
                if (notifyChange)
                    OwningTrack.OnChanged();
            }

            _next = _prev = null;
            OwningTrack = null;
        }

        public Keyframe GetFirstInSequence()
        {
            Keyframe temp = this;
            while (temp.Prev != null)
                temp = temp.Prev;
            return temp;
        }
        public Keyframe GetLastInSequence()
        {
            Keyframe temp = this;
            while (temp.Next != null)
                temp = temp.Next;
            return temp;
        }
        public int GetCountInSequence()
        {
            int count = 1;
            Keyframe? temp = Prev;
            while (temp != null)
            {
                ++count;
                temp = temp.Prev;
            }
            temp = Next;
            while (temp != null)
            {
                ++count;
                temp = temp.Next;
            }
            return count;
        }
        public int GetSequence(out Keyframe first, out Keyframe last)
        {
            int count = 1;
            first = this;
            last = this;
            Keyframe? temp = Prev;
            while (temp != null)
            {
                ++count;
                first = temp;
                temp = temp.Prev;
            }
            temp = Next;
            while (temp != null)
            {
                ++count;
                last = temp;
                temp = temp.Next;
            }
            return count;
        }
        //public static bool operator ==(Keyframe left, Keyframe right)
        //    => left?.Equals(right) ?? right is null;
        //public static bool operator !=(Keyframe left, Keyframe right)
        //    => left is null ? !(right is null) : !left.Equals(right);
        //public override bool Equals(object obj)
        //{
        //    if (obj is null)
        //        return false;
        //    if (obj.GetType() != GetType())
        //        return false;

        //    return Second == ((Keyframe)obj).Second;
        //}
        //public override int GetHashCode()
        //{
        //    return base.GetHashCode();
        //}
    }
}
