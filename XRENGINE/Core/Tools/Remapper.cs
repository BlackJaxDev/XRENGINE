using System.Collections;
using System.Collections.Generic;

namespace System
{
    public class Remapper
    {
        private readonly struct RemapKey<T>(T value) : IEquatable<RemapKey<T>>
        {
            private readonly T _value = value;
            private readonly bool _hasValue = value is not null;

            public bool Equals(RemapKey<T> other)
                => _hasValue == other._hasValue && (!_hasValue || EqualityComparer<T>.Default.Equals(_value, other._value));

            public override bool Equals(object? obj)
                => obj is RemapKey<T> other && Equals(other);

            public override int GetHashCode()
                => !_hasValue ? 0 : EqualityComparer<T>.Default.GetHashCode(_value!);
        }

        internal object? _source;
        internal int[] _impTable = [];
        internal int[] _remapTable = [];

        /// <summary>
        /// Contains the indices of all first appearances.
        /// </summary>
        public int[] ImplementationTable => _impTable;
        /// <summary>
        /// Contains indices into the ImplementationTable, same length as original list
        /// </summary>
        public int[] RemapTable => _remapTable;
        public int ImplementationLength => _impTable.Length;

        public T[] GetFirstAppearanceBuffer<T>()
            => _source is null || _source is not IList<T> list
                ? throw new InvalidOperationException()
                : [.. ImplementationTable.Select(x => list[x])];

        public void Remap<T>(IList<T> source)
            => Remap(source, null);

        public void Remap<T>(IList<T> source, Comparison<T>? comp)
        {
            _source = source;
            int count = source.Count;
            int tmp;
            Dictionary<RemapKey<T>, int> cache = [];

            _remapTable = new int[count];
            _impTable = new int[count];

            //Build remap table by assigning first appearance
            int impIndex = 0;
            for (int i = 0; i < count; i++)
            {
                T t = source[i];
                var key = new RemapKey<T>(t);

                if (cache.TryGetValue(key, out int cachedIndex))
                    _remapTable[i] = cachedIndex;
                else
                {
                    _impTable[impIndex] = i;
                    _remapTable[i] = impIndex;
                    cache[key] = impIndex++;
                }
            }

            int impCount = impIndex;

            if (comp is null)
            {
                Array.Resize(ref _impTable, impCount);
                return;
            }

            //Create new remap table, which is a sorted index list into the imp table
            int[] sorted = new int[impCount];
            impIndex = 0;
            for (int i = 0; i < impCount; i++)
            {
                //Get implementation index/object
                int ind = _impTable[i];
                T t = source[ind];

                sorted[impIndex] = i; //Set last, just in case we don't find a match

                //Iterate entries in sorted list, comparing them
                for (int y = 0; y < impIndex; y++)
                {
                    tmp = sorted[y]; //Pull old value, will use it later
                    if (comp(t, source[_impTable[tmp]]) < 0)
                    {
                        sorted[y] = i;

                        //Rotate right
                        for (int z = y; z++ < impIndex;)
                        {
                            ind = sorted[z];
                            sorted[z] = tmp;
                            tmp = ind;
                        }

                        break;
                    }
                }

                impIndex++;
            }

            //Swap sorted list, creating a new remap table in the process
            for (int i = 0; i < impCount; i++)
            {
                tmp = sorted[i]; //Get index
                sorted[i] = _impTable[tmp]; //Set sorted entry to imp index
                _impTable[tmp] = i; //Set imp entry to remap index
            }

            //Re-index remap
            for (int i = 0; i < count; i++)
                _remapTable[i] = _impTable[_remapTable[i]];

            //Swap tables
            _impTable = sorted;
        }
    }
}
