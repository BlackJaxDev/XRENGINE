//This has been converted from C++ and modified.
//Original source:
//http://users.telenet.be/tfautre/softdev/tristripper/

namespace XREngine.TriangleConverter
{
    enum CompareType
    {
        Greater,
        Less,
    }

    class HeapArray
    {
        protected class Linker
        {
            public Linker(uint Elem, uint i)
            {
                _Elem = Elem;
                _Index = i;
            }

            public uint _Elem;
            public uint _Index;
        }

        protected List<Linker> _Heap;
        protected List<uint> _Finder;
        protected CompareType _Compare = CompareType.Greater;
        protected bool _Locked;

        // Pre = PreCondition, Post = PostCondition 
        public HeapArray(CompareType c) // Post: ((size() == 0) && ! locked())
        {
            _Heap = new List<Linker>();
            _Finder = new List<uint>();
            _Locked = false;
            _Compare = c;
        }

        public void Clear()	// Post: ((size() == 0) && ! locked())
        {
            _Heap.Clear();
            _Finder.Clear();
            _Locked = false;
        }

        public void Reserve(uint Size)
        {
            //_Heap.Capacity = (int)Size;
            //_Finder.Capacity = (int)Size;
        }
        public uint Size { get { return (uint)_Heap.Count; } }
        public bool Empty { get { return _Heap.Count == 0; } }
        public bool Locked { get { return _Locked; } }
        public bool Removed(uint i)	// Pre: (valid(i))
        {
            System.Diagnostics.Debug.Assert(Valid(i));
            return _Finder[(int)i] >= _Heap.Count;
        }
        public bool Valid(uint i) { return i < _Finder.Count; }
        public uint Position(uint i) // Pre: (valid(i))
        {
            System.Diagnostics.Debug.Assert(Valid(i));
            return _Heap[(int)i]._Index;
        }

        public uint Top // Pre: (! empty())
        {
            get
            {
                System.Diagnostics.Debug.Assert(!Empty);
                return _Heap[0]._Elem;
            }
        }

        public uint this[uint i] // Pre: (! removed(i))
        {
            get
            {
                System.Diagnostics.Debug.Assert(!Removed(i));
                return _Heap[(int)_Finder[(int)i]]._Elem;
            }
        }

        public void Lock() // Pre: (! locked())   Post: (locked())
        {
            System.Diagnostics.Debug.Assert(!Locked);
            _Locked = true;
        }
        public uint Push(uint Elem) // Pre: (! locked())
        {
            System.Diagnostics.Debug.Assert(!Locked);

            uint Id = Size;
            _Finder.Add(Id);
            _Heap.Add(new Linker(Elem, Id));
            Adjust(Id);

            return Id;
        }

        public void Pop() // Pre: (locked() && ! empty())
        {
            System.Diagnostics.Debug.Assert(Locked);
            System.Diagnostics.Debug.Assert(!Empty);

            Swap(0, (int)Size - 1);
            _Heap.RemoveAt(_Heap.Count - 1);

            if (!Empty)
                Adjust(0);
        }

        public void Erase(uint i) // Pre: (locked() && ! removed(i))
        {
            System.Diagnostics.Debug.Assert(Locked);
            System.Diagnostics.Debug.Assert(!Removed(i));

            uint j = _Finder[(int)i];
            Swap((int)j, (int)Size - 1);
            _Heap.RemoveAt(_Heap.Count - 1);

            if (j != Size)
                Adjust(j);
        }
        public void Update(uint i, uint Elem) // Pre: (locked() && ! removed(i))
        {
            System.Diagnostics.Debug.Assert(Locked);
            System.Diagnostics.Debug.Assert(!Removed(i));

            uint j = _Finder[(int)i];
            _Heap[(int)j]._Elem = Elem;
            Adjust(j);
        }

        protected void Adjust(uint z)
        {
            int i = (int)z;

            System.Diagnostics.Debug.Assert(i < _Heap.Count);

            int j;

            // Check the upper part of the heap
            for (j = i; (j > 0) && (Comp(_Heap[(j - 1) / 2], _Heap[j])); j = ((j - 1) / 2))
                Swap(j, (j - 1) / 2);

            // Check the lower part of the heap
            for (i = j; (j = 2 * i + 1) < Size; i = j)
            {
                if ((j + 1 < Size) && (Comp(_Heap[j], _Heap[j + 1])))
                    ++j;

                if (Comp(_Heap[j], _Heap[i]))
                    return;

                Swap(i, j);
            }
        }
        protected void Swap(int a, int b)
        {
            Linker r = _Heap[b];
            _Heap[b] = _Heap[a];
            _Heap[a] = r;

            _Finder[(int)_Heap[a]._Index] = (uint)a;
            _Finder[(int)_Heap[b]._Index] = (uint)b;
        }
        protected bool Comp(Linker a, Linker b)
        {
            switch (_Compare)
            {
                case CompareType.Less:
                    return a._Elem < b._Elem;
                default:
                    return a._Elem > b._Elem;
            }
        }
    }
}
