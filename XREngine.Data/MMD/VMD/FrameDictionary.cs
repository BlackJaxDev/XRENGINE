using System.Collections;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Core;

namespace XREngine.Data.MMD
{
    public class FrameDictionary<T> : XRBase, IReadOnlyDictionary<uint, T> where T : class, IBinaryDataSource, IFramesKey, new()
    {
        private readonly SortedDictionary<uint, T> _dict = [];
        private readonly List<uint> _frameNumbers = [];

        public IEnumerable<uint> Keys => ((IReadOnlyDictionary<uint, T>)_dict).Keys;

        public IEnumerable<T> Values => ((IReadOnlyDictionary<uint, T>)_dict).Values;

        public int Count => ((IReadOnlyCollection<KeyValuePair<uint, T>>)_dict).Count;

        public T this[uint key] => ((IReadOnlyDictionary<uint, T>)_dict)[key];

        public void Add(T frameKey)
        {
            if (_dict.TryGetValue(frameKey.FrameNumber, out T? value))
                Merge(value, frameKey);
            else
            {
                _dict.Add(frameKey.FrameNumber, frameKey);
                _frameNumbers.Add(frameKey.FrameNumber);
            }
        }

        private void Merge(T existingFrame, T newFrame)
        {

        }

        public void Remove(T frameKey)
        {
            _dict.Remove(frameKey.FrameNumber);
            _frameNumbers.Remove(frameKey.FrameNumber);
        }

        public bool GetKeyframes(float t, uint maxFrameCount, out T? lastKey, out T? nextKey, out float inBetweenTime)
        {
            //binary search to find the key with the highest frame number that is less than or equal to t

            if (_frameNumbers.Count == 0)
            {
                lastKey = null;
                nextKey = null;
                inBetweenTime = 0;
                return false;
            }

            int left = -1;
            int right = _frameNumbers.Count - 1;

            while (left < right)
            {
                int mid = (left + right + 1) / 2;
                if ((float)_frameNumbers[mid] / maxFrameCount <= t)
                    left = mid;
                else
                    right = mid - 1;
            }

            if (left < 0)
            {
                lastKey = null;
                nextKey = _dict[_frameNumbers[0]];
                inBetweenTime = t / nextKey.FrameNumber;
                return true;
            }

            lastKey = _dict[_frameNumbers[left]];
            if (left == _frameNumbers.Count - 1)
            {
                nextKey = null;
                inBetweenTime = 0;
                return true;
            }

            nextKey = _dict[_frameNumbers[left + 1]];
            inBetweenTime = (t - lastKey.FrameNumber) / (nextKey.FrameNumber - lastKey.FrameNumber);
            return true;
        }

        public bool ContainsKey(uint key)
        {
            return ((IReadOnlyDictionary<uint, T>)_dict).ContainsKey(key);
        }

        public bool TryGetValue(uint key, [MaybeNullWhen(false)] out T value)
        {
            return ((IReadOnlyDictionary<uint, T>)_dict).TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<uint, T>> GetEnumerator()
        {
            return ((IEnumerable<KeyValuePair<uint, T>>)_dict).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)_dict).GetEnumerator();
        }
    }
}
