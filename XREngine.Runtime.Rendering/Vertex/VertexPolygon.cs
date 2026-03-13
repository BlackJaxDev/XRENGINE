namespace XREngine.Data.Rendering
{
    public class VertexPolygon : VertexPrimitive
    {
        public override FaceType Type => FaceType.Ngon;
        public VertexPolygon(params Vertex[] vertices) : base(vertices)
        {
            if (vertices.Length < 3)
                throw new InvalidOperationException("Not enough vertices for a polygon.");
        }
        public VertexPolygon(IEnumerable<Vertex> vertices) : base(vertices)
        {
            if (Vertices.Count < 3)
                throw new InvalidOperationException("Not enough vertices for a polygon.");
        }
        /// <summary>
        /// Example polygons:
        ///   4----3
        ///  /      \
        /// 5        2
        ///  \      /
        ///   0----1
        /// Converted: 012, 023, 034, 045
        /// 3---2
        /// |   |
        /// 0---1
        /// Converted: 012, 023
        /// </summary>
        public virtual VertexTriangle[] ToTriangles()
        {
            int triangleCount = Vertices.Count - 2;
            if (triangleCount < 1)
                return [];

            VertexTriangle[] list = new VertexTriangle[triangleCount];
            for (int i = 0; i < triangleCount; ++i)
                list[i] = new VertexTriangle(
                    Vertices[0].HardCopy(),
                    Vertices[i + 1].HardCopy(),
                    Vertices[i + 2].HardCopy());

            return list;
        }
        public virtual VertexLine[] ToLines()
        {
            VertexLine[] lines = new VertexLine[Vertices.Count];

            for (int i = 0; i < Vertices.Count - 1; ++i)
                lines[i] = new VertexLine(Vertices[i].HardCopy(), Vertices[i + 1].HardCopy());

            lines[Vertices.Count - 1] = new VertexLine(Vertices[^1].HardCopy(), Vertices[0].HardCopy());

            return lines;
        }
    }
}
