//This has been converted from C++ and modified.
//Original source:
//http://users.telenet.be/tfautre/softdev/tristripper/

using System.Collections;

namespace XREngine.TriangleConverter
{
    public class GraphArray<T> : IEnumerable
    {
        protected List<Node> _nodes = new();
        protected List<Arc> _arcs = new();

	    public class Arc
        {
            public Arc(Node Terminal) { _terminal = Terminal; }
            public Node Terminal { get { return _terminal; } }
            public Node _terminal;
	    }

	    public class Node
	    {
            public bool Marked { get { return _marker; } set { _marker = value; } }
            public bool Empty { get { return (_begin == _end); } }
            public uint Size { get { return (_end - _begin); } }
            public List<Arc> Arcs { get { return _graph._arcs; } }

            public GraphArray<T> _graph;
            public uint _begin;
            public uint _end;

            public T _elem = default!;
            private bool _marker;

            public Node(GraphArray<T> graph)
            {
                _graph = graph;
                _begin = uint.MaxValue;
                _end = uint.MaxValue;
                _marker = false;
                _elem = default!;
            }
        }

	    public GraphArray()
        {
            _nodes = new List<Node>();
            _arcs = new List<Arc>();
        }
	    public GraphArray(uint numNodes)
        {
            _nodes = new List<Node>();
            _arcs = new List<Arc>();
            for (int i = 0; i < numNodes; i++)
                _nodes.Add(new Node(this));
        }

	    //Node related member functions
	    public bool Empty { get { return _nodes.Count == 0; } }
	    public uint Count { get { return (uint)_nodes.Count; } }
	    public Node this[uint i]
        {
            get
            {
                System.Diagnostics.Debug.Assert(i < Count);
                return _nodes[(int)i];
            }
        }

	    // Arc related member functions
	    public Arc InsertArc(uint initial, uint terminal)
        {
            System.Diagnostics.Debug.Assert(initial < Count, "Initial is greater than count");
            System.Diagnostics.Debug.Assert(terminal < Count, "Terminal is greater than count");

            Arc r = new Arc(_nodes[(int)terminal]);
	        _arcs.Add(r);

            Node Node = _nodes[(int)initial];
	        if (Node.Empty)
            {
		        Node._begin = (uint)_arcs.Count - 1;
                Node._end = (uint)_arcs.Count;
	        }
            else
            {
                Node._end++;

                // we optimise here for make_connectivity_graph()
                // we know all the arcs for a given node are successively and sequentially added
                System.Diagnostics.Debug.Assert(Node._end == _arcs.Count);
	        }
            return r;
        }

	    // Optimized (overloaded) functions
	    public void Swap(GraphArray<T> right)
        {
            List<Node> n = _nodes;
            List<Arc> a = _arcs;
            _nodes = right._nodes;
            _arcs = right._arcs;
            right._nodes = n;
            right._arcs = a;
        }

        public IEnumerator GetEnumerator()
        {
            return _nodes.GetEnumerator();
        }
    }
}
