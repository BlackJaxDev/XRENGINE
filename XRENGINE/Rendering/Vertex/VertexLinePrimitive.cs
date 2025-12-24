namespace XREngine.Data.Rendering
{
    public abstract class VertexLinePrimitive(params Vertex[] vertices) : VertexPrimitive(vertices)
    {
        public abstract VertexLine[] ToLines();
    }
}
