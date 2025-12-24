namespace XREngine.Data.Rendering
{
    public class VertexTriangleFan : VertexPolygon
    {
        public VertexTriangleFan(params Vertex[] vertices) : base(vertices) { }
        public VertexTriangleFan(IEnumerable<Vertex> vertices) : base(vertices) { }

        public override FaceType Type => FaceType.TriangleFan;
    }
}
