//This has been converted from C++ and modified.
//Original source:
//http://users.telenet.be/tfautre/softdev/tristripper/

namespace XREngine.TriangleConverter
{
    public enum PrimType
    {
        TriangleList = 0x0004,
        TriangleStrip = 0x0005
    }

    public class Primitive
    {
        public Primitive(PrimType type)
        {
            Type = type;
            Indices = new List<uint>();
            NodeIDs = new List<ushort>();
        }

        public List<ushort> NodeIDs;
        public List<uint> Indices;
        public PrimType Type;
    }

    public class TriStripper
    {
        private List<Primitive> _PrimitivesVector;
        private GraphArray<Triangle> _Triangles;
        private HeapArray _TriHeap;
        private List<uint> _Candidates;
        private CacheSimulator _Cache;
        private CacheSimulator _BackCache;
        private uint _StripID;
        private uint _MinStripSize;
        private bool _BackwardSearch;
        private bool _FirstRun;
        private ushort[] _Nodes = [];
        private int[] _ImpTable = [];
        private List<ushort> _CurrentNodes = [];

        public TriStripper(uint[] TriIndices, ushort[] NodeIds, int[] ImpTable)
        {
            _ImpTable = ImpTable;
            _Nodes = NodeIds;
            _Triangles = new GraphArray<Triangle>((uint)TriIndices.Length / 3);
            _StripID = 0;
            _FirstRun = true;
            _PrimitivesVector = new List<Primitive>();
            _TriHeap = new HeapArray(CompareType.Less);
            _Candidates = new List<uint>();
            _Cache = new CacheSimulator();
            _BackCache = new CacheSimulator();

            SetCacheSize();
            SetMinStripSize();
            SetBackwardSearch();
            SetPushCacheHits();

            MakeConnectivityGraph(_Triangles, TriIndices);
        }

        private bool Cache { get { return (CacheSize != 0); } }
        private uint CacheSize { get { return _Cache.Size; } }

	    public List<Primitive> Strip()
        {
	        if (!_FirstRun) 
            {
		        UnmarkNodes(_Triangles);
		        ResetStripIDs();
		        _Cache.Reset();
		        _TriHeap.Clear();
		        _Candidates.Clear();
		        _StripID = 0;
	        }

	        InitTriHeap();

	        Stripify();
	        AddRemainingTriangles();

            _FirstRun = false;
	        return _PrimitivesVector.ToList();
        }

        #region Stripifier Algorithm Settings
	
	    //Set the post-T&L cache size (0 disables the cache optimizer).
	    public void SetCacheSize(uint CacheSize = 10)
        {
            _Cache.Resize(CacheSize);
            _BackCache.Resize(CacheSize);
        }

	    //Set the minimum size of a triangle strip (should be at least 2 triangles).
	    //The stripifier discard any candidate strips that does not satisfy the minimum size condition.
        public void SetMinStripSize(uint MinStripSize = 2)
        {
            if (MinStripSize < 2)
                _MinStripSize = 2;
            else
                _MinStripSize = MinStripSize;
        }

	    //Set the backward search mode in addition to the forward search mode.
	    //In forward mode, the candidate strips are built with the current candidate triangle being the first
	    //triangle of the strip. When the backward mode is enabled, the stripifier also tests candidate strips
	    //where the current candidate triangle is the last triangle of the strip.
	    //Enable this if you want better results at the expense of being slightly slower.
	    //Note: Do *NOT* use this when the cache optimizer is enabled; it only gives worse results.
        public void SetBackwardSearch(bool Enabled = false)
        {
            _BackwardSearch = Enabled;
        }

        //Set the cache simulator FIFO behavior (does nothing if the cache optimizer is disabled).
	    //When enabled, the cache is simulated as a simple FIFO structure. However, when
	    //disabled, indices that trigger cache hits are not pushed into the FIFO structure.
	    //This allows simulating some GPUs that do not duplicate cache entries (e.g. NV25 or greater).
        public void SetPushCacheHits(bool Enabled = true)
        {
            _Cache.PushCacheHits(Enabled);
        }

    #endregion

        private	void InitTriHeap()
        {
            _TriHeap.Reserve(_Triangles.Count);

	        //Set up the triangles priority queue
	        //The lower the number of available neighbour triangles, the higher the priority.
	        for (uint i = 0; i < _Triangles.Count; i++)
		        _TriHeap.Push(_Triangles[i].Size);

	        //We're not going to add new elements anymore
            _TriHeap.Lock();

	        //Remove useless triangles
	        //Note: we had to put all of them into the heap before to ensure coherency of the heap_array object
	        while ((!_TriHeap.Empty) && (_TriHeap.Top == 0))
		        _TriHeap.Pop();
        }
        private	void Stripify()
        {
            while (!_TriHeap.Empty)
            {
                //There is no triangle in the candidates list, refill it with the loneliest triangle
                uint HeapTop = _TriHeap.Position(0);
                _Candidates.Add(HeapTop);

                while (_Candidates.Count != 0)
                {
                    //Note: FindBestStrip empties the candidate list, while BuildStrip refills it
                    Strip TriStrip = FindBestStrip();

                    if (TriStrip.Size >= _MinStripSize)
                        BuildStrip(TriStrip);
                }

                if (!_TriHeap.Removed(HeapTop))
                    _TriHeap.Erase(HeapTop);

                //Eliminate all the triangles that have now become useless
                while ((!_TriHeap.Empty) && (_TriHeap.Top == 0))
                    _TriHeap.Pop();
            }
        }
        private	void AddRemainingTriangles()
        {
            //Create the last indices array and fill it with all the triangles that couldn't be stripped
            Primitive p = new Primitive(PrimType.TriangleList);

            for (uint i = 0; i < _Triangles.Count; i++)
                if (!_Triangles[i].Marked)
                {
                    p.Indices.Add(_Triangles[i]._elem.A);
                    p.Indices.Add(_Triangles[i]._elem.B);
                    p.Indices.Add(_Triangles[i]._elem.C);
                }

            if (p.Indices.Count > 0)
                _PrimitivesVector.Add(p);
        }
        private	void ResetStripIDs()
        {
            foreach (Triangle r in _Triangles)
		        r.ResetStripID();
        }

        private	Strip FindBestStrip()
        {
            //Allow to restore the cache (modified by ExtendTriToStrip) and implicitly reset the cache hit count
	        CacheSimulator CacheBackup = _Cache;

	        Policy policy = new Policy(_MinStripSize, Cache);

	        while (_Candidates.Count != 0) 
            {
		        uint Candidate = _Candidates[_Candidates.Count - 1];
                _Candidates.RemoveAt(_Candidates.Count - 1);

		        //Discard useless triangles from the candidate list
		        if ((_Triangles[Candidate].Marked) || (_TriHeap[Candidate] == 0))
			        continue;

		        //Try to extend the triangle in the 3 possible forward directions
		        for (uint i = 0; i < 3; i++)
                {
			        Strip Strip = ExtendToStrip(Candidate, (TriOrder)i);
                    policy.Challenge(Strip, _TriHeap[Strip.Start], _Cache.HitCount);

                    _Cache = CacheBackup;
		        }

		        //Try to extend the triangle in the 6 possible backward directions
		        if (_BackwardSearch) 
                {
			        for (uint i = 0; i < 3; i++) 
                    {
				        Strip Strip = BackExtendToStrip(Candidate, (TriOrder)i, false);
                        if (Strip != null)
				            policy.Challenge(Strip, _TriHeap[Strip.Start], _Cache.HitCount);

                        _Cache = CacheBackup;
			        }

			        for (uint i = 0; i < 3; i++)
                    {
				        Strip Strip = BackExtendToStrip(Candidate, (TriOrder)i, true);
                        if (Strip != null)
                            policy.Challenge(Strip, _TriHeap[Strip.Start], _Cache.HitCount);

                        _Cache = CacheBackup;
			        }
		        }
	        }
	        return policy.BestStrip;
        }
        private	Strip ExtendToStrip(uint Start, TriOrder Order)
        {
            TriOrder StartOrder = Order;

            _CurrentNodes = new List<ushort>();
            _checkNodes = true;

            //Begin a new strip
            Triangle tri = _Triangles[Start]._elem;
            tri.SetStripID(++_StripID);
            AddTriangle(tri, Order, false);

            TryAddNode(tri.A);
            TryAddNode(tri.B);
            TryAddNode(tri.C);

            uint Size = 1;
            bool ClockWise = false;

            //Loop while we can further extend the strip
            for (uint i = Start; (i < _Triangles.Count) && (!Cache || ((Size + 2) < CacheSize)); Size++)
            {
                var Node = _Triangles[i];
                var Link = LinkToNeighbour(Node, ClockWise, ref Order, false);

                // Is it the end of the strip?
                if (Link is null)
                {
                    Size--;
                    i = _Triangles.Count;
                }
                else
                {
                    i = (Node = Link.Terminal)._elem._index;

                    Node._elem.SetStripID(_StripID);
                    ClockWise = !ClockWise;
                }
            }

            _checkNodes = false;
            _CurrentNodes.Clear();

            return new Strip(Start, StartOrder, Size);
        }
        private	Strip BackExtendToStrip(uint Start, TriOrder Order, bool ClockWise)
        {
            _CurrentNodes = new List<ushort>();
            _checkNodes = true;

            //Begin a new strip
            Triangle tri = _Triangles[Start]._elem;
            uint b = LastEdge(tri, Order).B;
            if (TryAddNode(b))
            {
                tri.SetStripID(++_StripID);
                BackAddIndex(b);
            }
            else
            {
                _checkNodes = false;
                _CurrentNodes.Clear();
                return null;
            }

            uint Size = 1;
            GraphArray<Triangle>.Node Node = null;

            //Loop while we can further extend the strip
            for (uint i = Start; !Cache || ((Size + 2) < CacheSize); Size++)
            {
                Node = _Triangles[i];
                var Link = BackLinkToNeighbour(Node, ClockWise, ref Order);

                //Is it the end of the strip?
                if (Link is null)
                    break;
                else
                {
                    i = (Node = Link.Terminal)._elem._index;

                    Node._elem.SetStripID(_StripID);
                    ClockWise = !ClockWise;
                }
            }

            _checkNodes = false;
            _CurrentNodes.Clear();

            //We have to start from a counterclockwise triangle.
            //Simply return an empty strip in the case where the first triangle is clockwise.
            //Even though we could discard the first triangle and start from the next counterclockwise triangle,
            //this often leads to more lonely triangles afterward.
            if (ClockWise)
                return null;

            if (Cache)
            {
                _Cache.Merge(_BackCache, Size);
                _BackCache.Reset();
            }

            return new Strip(Node._elem._index, Order, Size);
        }
        private bool _checkNodes = false;
        private bool TryAddNode(uint index)
        {
            if (!_checkNodes)
                return true;

            //ushort node = _Nodes[_ImpTable[index]];
            //if (!_CurrentNodes.Contains(node))
            //{
            //    if (_CurrentNodes.Count == PrimitiveGroup._nodeCountMax)
            //        return false;
            //    _CurrentNodes.Add(node);
            //}
            return true;
        }
        private GraphArray<Triangle>.Arc LinkToNeighbour(GraphArray<Triangle>.Node Node, bool ClockWise, ref TriOrder Order, bool NotSimulation)
        {
            TriangleEdge Edge = LastEdge(Node._elem, Order);
            for (uint i = Node._begin; i < Node._end; i++)
            {
                var Link = Node.Arcs[(int)i];

                //Get the reference to the possible next triangle
                Triangle Tri = Link.Terminal._elem;

                //Check whether it's already been used
                if ((NotSimulation || (Tri.StripID != _StripID)) && !Link.Terminal.Marked)
                {
                    //Does the current candidate triangle match the required position for the strip?
                    if (Edge.B == Tri.A && Edge.A == Tri.B && TryAddNode(Tri.C))
                    {
                        Order = ClockWise ? TriOrder.ABC : TriOrder.BCA;
                        AddIndex(Tri.C, NotSimulation);
                        return Link;
                    }
                    else if (Edge.B == Tri.B && Edge.A == Tri.C && TryAddNode(Tri.A))
                    {
                        Order = ClockWise ? TriOrder.BCA : TriOrder.CAB;
                        AddIndex(Tri.A, NotSimulation);
                        return Link;
                    }
                    else if (Edge.B == Tri.C && Edge.A == Tri.A && TryAddNode(Tri.B))
                    {
                        Order = ClockWise ? TriOrder.CAB : TriOrder.ABC;
                        AddIndex(Tri.B, NotSimulation);
                        return Link;
                    }
                }
            }
            return null;
        }
        private GraphArray<Triangle>.Arc BackLinkToNeighbour(GraphArray<Triangle>.Node Node, bool ClockWise, ref TriOrder Order)
        {
            TriangleEdge Edge = FirstEdge(Node._elem, Order);
            for (uint i = Node._begin; i < Node._end; i++)
            {
                var Link = Node.Arcs[(int)i];

		        //Get the reference to the possible previous triangle
		        Triangle Tri = Link.Terminal._elem;

		        //Check whether it's already been used
                if ((Tri.StripID != _StripID) && !Link.Terminal.Marked) 
                {
			        //Does the current candidate triangle match the required position for the strip?
                    if (Edge.B == Tri.A && Edge.A == Tri.B && TryAddNode(Tri.C)) 
                    {
                        Order = ClockWise ? TriOrder.CAB : TriOrder.BCA;
				        BackAddIndex(Tri.C);
				        return Link;
			        }
                    else if (Edge.B == Tri.B && Edge.A == Tri.C && TryAddNode(Tri.A)) 
                    {
                        Order = ClockWise ? TriOrder.ABC : TriOrder.CAB;
				        BackAddIndex(Tri.A);
				        return Link;
			        }
                    else if (Edge.B == Tri.C && Edge.A == Tri.A && TryAddNode(Tri.B)) 
                    {
                        Order = ClockWise ? TriOrder.BCA : TriOrder.ABC;
				        BackAddIndex(Tri.B);
				        return Link;
			        }
		        }
	        }
	        return null;
        }
        private	void BuildStrip(Strip Strip)
        {
            uint Start = Strip.Start;

            bool ClockWise = false;
            TriOrder Order = Strip.Order;

            //Create a new strip
            Primitive p = new Primitive(PrimType.TriangleStrip);
            _PrimitivesVector.Add(p);
            AddTriangle(_Triangles[Start]._elem, Order, true);
            MarkTriAsTaken(Start);

            //Loop while we can further extend the strip
            var Node = _Triangles[Start];

            for (uint Size = 1; Size < Strip.Size; Size++)
            {
                var Link = LinkToNeighbour(Node, ClockWise, ref Order, true);

                System.Diagnostics.Debug.Assert(Link != null);

                //Go to the next triangle
                Node = Link.Terminal;
                MarkTriAsTaken(Node._elem._index);
                ClockWise = !ClockWise;
            }
        }

        private	void MarkTriAsTaken(uint i)
        {
	        //Mark the triangle node
	        _Triangles[i].Marked = true;

	        //Remove triangle from priority queue if it isn't yet
	        if (!_TriHeap.Removed(i))
		        _TriHeap.Erase(i);

	        //Adjust the degree of available neighbour triangles
            var Node = _Triangles[i];
            for (uint x = Node._begin; x < Node._end; x++)
            {
                var Link = Node.Arcs[(int)x];

		        uint j = Link.Terminal._elem._index;
		        if ((!_Triangles[j].Marked) && (!_TriHeap.Removed(j))) 
                {
			        uint NewDegree = _TriHeap[j] - 1;
			        _TriHeap.Update(j, NewDegree);

			        //Update the candidate list if cache is enabled
			        if (Cache && (NewDegree > 0))
				        _Candidates.Add(j);
		        }
	        }
        }

        private	void AddIndex(uint i, bool NotSimulation)
        {
            if (Cache)
                _Cache.Push(i, !NotSimulation);

            if (NotSimulation)
                _PrimitivesVector[_PrimitivesVector.Count - 1].Indices.Add(i);
        }

        private	void BackAddIndex(uint i)
        {
            if (Cache)
                _BackCache.Push(i, true);
        }

	    private void AddTriangle(Triangle Tri, TriOrder Order, bool NotSimulation)
        {
            switch (Order)
            {
                case TriOrder.ABC:
                    AddIndex(Tri.A, NotSimulation);
                    AddIndex(Tri.B, NotSimulation);
                    AddIndex(Tri.C, NotSimulation);
                    break;

                case TriOrder.BCA:
                    AddIndex(Tri.B, NotSimulation);
                    AddIndex(Tri.C, NotSimulation);
                    AddIndex(Tri.A, NotSimulation);
                    break;

                case TriOrder.CAB:
                    AddIndex(Tri.C, NotSimulation);
                    AddIndex(Tri.A, NotSimulation);
                    AddIndex(Tri.B, NotSimulation);
                    break;
            }
        }

        private	void BackAddTriangle(Triangle Tri, TriOrder Order)
        {
            switch (Order)
            {
                case TriOrder.ABC:
                    BackAddIndex(Tri.C);
                    BackAddIndex(Tri.B);
                    BackAddIndex(Tri.A);
                    break;

                case TriOrder.BCA:
                    BackAddIndex(Tri.A);
                    BackAddIndex(Tri.C);
                    BackAddIndex(Tri.B);
                    break;

                case TriOrder.CAB:
                    BackAddIndex(Tri.B);
                    BackAddIndex(Tri.A);
                    BackAddIndex(Tri.C);
                    break;
            }
        }

        private static TriangleEdge FirstEdge(Triangle Triangle, TriOrder Order)
            => Order switch
            {
                TriOrder.ABC => new TriangleEdge(Triangle.A, Triangle.B),
                TriOrder.BCA => new TriangleEdge(Triangle.B, Triangle.C),
                TriOrder.CAB => new TriangleEdge(Triangle.C, Triangle.A),
                _ => new TriangleEdge(0, 0),
            };

        private static TriangleEdge LastEdge(Triangle Triangle, TriOrder Order)
        {
            switch (Order)
            {
                case TriOrder.ABC: return new TriangleEdge(Triangle.B, Triangle.C);
                case TriOrder.BCA: return new TriangleEdge(Triangle.C, Triangle.A);
                case TriOrder.CAB: return new TriangleEdge(Triangle.A, Triangle.B);
                default: return new TriangleEdge(0, 0);
            }
        }

        public void UnmarkNodes(GraphArray<Triangle> G)
        {
            foreach (GraphArray<Triangle>.Node t in G)
                t.Marked = false;
        }

        public static int EdgeComp(TriEdge x, TriEdge y)
        {
            uint xa = x.A;
            uint xb = x.B;
            uint ya = y.A;
            uint yb = y.B;

            return ((xa < ya) || ((xa == ya) && (xb < yb))) ? -1 : 1;
        }

        void MakeConnectivityGraph(GraphArray<Triangle> Triangles, uint[] Indices)
        {
            System.Diagnostics.Debug.Assert(Triangles.Count == (Indices.Length / 3));

            //Fill the triangle data
            for (int i = 0; i < Triangles.Count; i++)
                Triangles[(uint)i]._elem = new Triangle(
                    Indices[i * 3 + 0],
                    Indices[i * 3 + 1],
                    Indices[i * 3 + 2]) { _index = (uint)i };

            //Build an edge lookup table
            List<TriEdge> EdgeMap = new List<TriEdge>();
            for (uint i = 0; i < Triangles.Count; i++)
            {
                Triangle Tri = Triangles[i]._elem;
                EdgeMap.Add(new TriEdge(Tri.A, Tri.B, i));
                EdgeMap.Add(new TriEdge(Tri.B, Tri.C, i));
                EdgeMap.Add(new TriEdge(Tri.C, Tri.A, i));
            }
            EdgeMap.Sort(EdgeComp);

            //Link neighbour triangles together using the lookup table
            for (uint i = 0; i < Triangles.Count; i++)
            {
                Triangle Tri = Triangles[i]._elem;
                LinkNeighbours(Triangles, EdgeMap, new TriEdge(Tri.B, Tri.A, i));
                LinkNeighbours(Triangles, EdgeMap, new TriEdge(Tri.C, Tri.B, i));
                LinkNeighbours(Triangles, EdgeMap, new TriEdge(Tri.A, Tri.C, i));
            }
        }

        private static int BinarySearch<T>(IList<T> list, T value, Comparison<T> comp)
        {
            int lo = 0, hi = list.Count - 1;
            while (lo < hi)
            {
                int m = (hi + lo) / 2;
                if (comp(list[m], value) < 0) lo = m + 1;
                else hi = m - 1;
            }
            if (comp(list[lo], value) < 0) lo++;
            return lo;
        }

        void LinkNeighbours(GraphArray<Triangle> Triangles, List<TriEdge> EdgeMap, TriEdge Edge)
        {
            //Find the first edge equal to Edge
            //See if there are any other edges that are equal
            //(if so, it means that more than 2 triangles are sharing the same edge,
            //which is unlikely but not impossible)
            for (int i = BinarySearch<TriEdge>(EdgeMap, Edge, EdgeComp);
                i < EdgeMap.Count && Edge == EdgeMap[i]; i++)
                Triangles.InsertArc(Edge.TriPos, EdgeMap[i].TriPos);
            //Note: degenerated triangles will also point themselves as neighbour triangles
        }
    }
}
