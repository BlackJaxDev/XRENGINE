//This has been converted from C++ and modified.
//Original source:
//http://users.telenet.be/tfautre/softdev/tristripper/

namespace XREngine.TriangleConverter
{
    public class Triangle(uint a, uint b, uint c)
    {
        public Triangle() : this(0, 0, 0) { }

        public void ResetStripID()
            => _stripID = 0;

        public void SetStripID(uint stripID)
            => _stripID = stripID;

        public uint StripID
            => _stripID;

        public uint A => a;
        public uint B => b;
        public uint C => c;

        private uint _stripID = 0;
        public uint _index;
    }

    public class TriangleEdge(uint a, uint b)
    {
        public uint A => a;
        public uint B => b;

        public static bool operator ==(TriangleEdge left, TriangleEdge right)
            => ((left.A == right.A) && (left.B == right.B));
        public static bool operator !=(TriangleEdge left, TriangleEdge right)
            => ((left.A != right.A) || (left.B != right.B));

        public override bool Equals(object? obj)
            => obj is not null && obj is TriangleEdge tri && this == tri;

        public override int GetHashCode()
            => A.GetHashCode() ^ B.GetHashCode();

        public override string ToString()
            => string.Format("{0} {1}", A, B);
    }

    public class TriEdge(uint A, uint B, uint triPos) : TriangleEdge(A, B)
    {
        public uint TriPos { get; } = triPos;

        public static bool operator ==(TriEdge left, TriEdge right)
            => ((left.A == right.A) && (left.B == right.B));
        public static bool operator !=(TriEdge left, TriEdge right)
            => ((left.A != right.A) || (left.B != right.B));

        public override bool Equals(object? obj)
            => obj is not null && obj is TriEdge tri && this == tri;

        public override int GetHashCode()
            => A.GetHashCode() ^ B.GetHashCode();

        public override string ToString()
            => string.Format("{0} {1} {2}", A, B, TriPos);
    }

    public enum TriOrder
    {
        ABC,
        BCA,
        CAB
    };

    public class Strip(uint start, TriOrder order, uint size)
    {
        public Strip() : this(uint.MaxValue, TriOrder.ABC, 0u) { }

        public uint Start => _start;
        public TriOrder Order => _order;
        public uint Size => _size;

        private readonly uint _start = start;
        private readonly TriOrder _order = order;
        private readonly uint _size = size;
    }
}
