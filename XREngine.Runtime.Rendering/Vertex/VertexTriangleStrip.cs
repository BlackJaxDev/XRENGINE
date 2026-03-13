namespace XREngine.Data.Rendering
{
    public class VertexTriangleStrip(params Vertex[] vertices) : VertexPolygon(vertices)
    {
        public int FaceCount => _vertices.Count - 2;
        public override FaceType Type => FaceType.TriangleStrip;

        public override VertexTriangle[] ToTriangles()
        {
            VertexTriangle[] triangles = new VertexTriangle[FaceCount];
            for (int i = 2, count = _vertices.Count, bit = 0; i < count; bit = ++i & 1)
                triangles[i - 2] = new VertexTriangle(
                    _vertices[i - 2],
                    _vertices[i - 1 + bit],
                    _vertices[i - bit]);
            return triangles;
        }
    }
}
