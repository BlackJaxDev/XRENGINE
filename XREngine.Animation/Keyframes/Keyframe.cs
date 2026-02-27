using Extensions;
using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    /// <summary>
    /// Represents a keyframe in an animation.
    /// Stored as a linked list, because animations will be playing forward and backward, but usually not seeking.
    /// </summary>
    public abstract class Keyframe : XRBase, IKeyframe
    {
        protected float _second;
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
                _second = value.ClampMin(0.0f);
                if (float.IsNaN(_second))
                    _second = 0.0f;
                Keyframe? kf = Prev ?? Next;
                kf?.UpdateLink(this, false);
                OwningTrack?.OnChanged();
            }
        }

        public Keyframe? Next => _next;
        public Keyframe? Prev => _prev;
        public bool IsFirst => _prev is null;
        public bool IsLast => _next is null;

        public BaseKeyframeTrack? OwningTrack
        {
            get => _owningTrack;
            internal set
            {
                _owningTrack = value;
                if (Next != null && Next.OwningTrack != _owningTrack)
                    Next.OwningTrack = _owningTrack;
                if (Prev != null && Prev.OwningTrack != _owningTrack)
                    Prev.OwningTrack = _owningTrack;
            }
        }

        [Browsable(false)]
        public abstract Type ValueType { get; }

        public void UpdateLink(Keyframe? key, bool notifyChange = true)
        {
            if (key is null || key == this)
                return;

            //if (_next == key)
            //    return;

            //resize track length if second is outside of range
            //if (key.Second > OwningTrack.LengthInSeconds)
            //    OwningTrack.LengthInSeconds = key.Second;

            key.Remove(key.OwningTrack != OwningTrack);
            key.Second = key.Second.RemapToRange(0.0f, (OwningTrack?.LengthInSeconds ?? 0.0f) + 0.0001f);

            //Second is within this keyframe and the next?
            if (key.Second >= Second)
            {
                //After current key
                if (Next is null || key.Second < Next.Second)
                {
                    if (_next != key)
                    {
                        key._next = _next;
                        _next?._prev = key;
                    }

                    key._prev = this;
                    _next = key;

                    PostKeyLinkUpdate(key, notifyChange);
                }
                else
                {
                    //Recursive link to next key
                    //next not null, greater than next, and next is not before this
                    if (_next != null && key.Second >= _next.Second && _next.Second >= Second)
                        _next.UpdateLink(key, notifyChange);
                }
            }
            else
            {
                //Before current key
                if (Prev is null || key.Second >= Prev.Second)
                {
                    if (_prev != key)
                    {
                        key._prev = _prev;
                        _prev?._next = key;
                    }

                    key._next = this;
                    _prev = key;

                    PostKeyLinkUpdate(key, notifyChange);
                }
                else
                {
                    //Recursive link to prev key
                    //prev not null, less than this, and prev is not after this
                    if (_prev != null && _prev.Second < Second)
                        _prev.UpdateLink(key, notifyChange);
                }
            }
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
