namespace XREngine.Data.Rendering
{
    public class VertexLine(Vertex v0, Vertex v1) : VertexPrimitive(v0, v1)
    {
        public Vertex Vertex0 => _vertices[0];
        public Vertex Vertex1 => _vertices[1];
        public override FaceType Type => FaceType.Triangles;
    }
}
