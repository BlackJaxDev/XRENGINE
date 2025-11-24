using System.Collections;
using System.Collections.Generic;

namespace XREngine
{
    public class Remapper
    {
        internal object? _source;
        internal int[]? _impTable;
        internal int[]? _remapTable;

        /// <summary>
        /// Contains the indices of all first appearances in the original list.
        /// Shorter than the original list and may contain duplicate indices.
        /// </summary>
        public int[]? ImplementationTable => _impTable;
        /// <summary>
        /// Contains indices into the ImplementationTable, same length as original list
        /// </summary>
        public int[]? RemapTable => _remapTable;
        /// <summary>
        /// 
        /// </summary>
        public uint ImplementationLength => (uint)(_impTable?.Length ?? 0);

        public T[]? GetFirstAppearanceBuffer<T>()
        {
            if (_source is null || _source is not IList<T> list)
                throw new InvalidOperationException();

            return ImplementationTable?.Select(x => list[x])?.ToArray();
        }

        public void Remap<T>(IList<T> source) => Remap(source, null);
        private static readonly object NullKey = new();

        public void Remap<T>(IList<T> source, Comparison<T>? comp)
        {
            int count = source.Count;
            Dictionary<object, int> cache = new();

            _source = source;
            _remapTable = new int[count];
            _impTable = new int[count];

            //Build remap table by assigning first appearance
            int impIndex = 0;
            for (int i = 0; i < count; i++)
            {
                T t = source[i];

                object key = t is null ? NullKey : t;

                if (cache.TryGetValue(key, out int cachedIndex))
                {
                    _remapTable[i] = cachedIndex;
                    continue;
                }

                // New unique entry path
                _impTable[impIndex] = i;
                _remapTable[i] = impIndex;
                cache[key] = impIndex;
                impIndex++;
            }

            int impCount = impIndex;

            if (comp is null)
            {
                Array.Resize(ref _impTable, impCount);
                return;
            }

            int tmp;
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
