namespace XREngine.Data.Rendering
{
    public class VertexLineStrip(bool closedLoop, params Vertex[] vertices) : VertexLinePrimitive(vertices)
    {
        public override FaceType Type => ClosedLoop ? FaceType.LineLoop : FaceType.LineStrip;

        public VertexLineStrip(bool closedLoop, IEnumerable<Vertex> vertices)
            : this(closedLoop, vertices.ToArray()) { }

        public bool ClosedLoop { get; set; } = closedLoop;

        public override VertexLine[] ToLines()
        {
            int count = _vertices.Count;
            if (!ClosedLoop && count > 0)
                --count;

            VertexLine[] lines = new VertexLine[count];
            for (int i = 0; i < count; ++i)
            {
                Vertex next = i + 1 == _vertices.Count ? _vertices[0] : _vertices[i + 1];
                lines[i] = new VertexLine(_vertices[i], next);
            }

            return lines;
        }
    }
}
