namespace XREngine.Data.Rendering
{
    /// <summary>
    ///    2
    ///   / \
    ///  /   \
    /// 0-----1
    /// </summary>
    public class VertexTriangle(Vertex v0, Vertex v1, Vertex v2) : VertexPolygon(v0, v1, v2)
    {
        public Vertex Vertex0 => _vertices[0];
        public Vertex Vertex1 => _vertices[1];
        public Vertex Vertex2 => _vertices[2];

        public override FaceType Type => FaceType.Triangles;

        public override VertexTriangle[] ToTriangles()
            => [this];

        public override VertexLine[] ToLines()
            =>
            [
                new(Vertex0, Vertex1),
                new(Vertex1, Vertex2),
                new(Vertex2, Vertex0),
            ];
    }
}
