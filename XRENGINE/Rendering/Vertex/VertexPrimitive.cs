using System.Collections;
using System.Collections.ObjectModel;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;

namespace XREngine.Data.Rendering
{
    public abstract class VertexPrimitive : XRBase, IEnumerable<Vertex>
    {
        public abstract FaceType Type { get; }
        public ReadOnlyCollection<Vertex> Vertices => _vertices.AsReadOnly();

        protected List<Vertex> _vertices = [];
        
        public VertexPrimitive(IEnumerable<Vertex> vertices)
            => _vertices = [.. vertices];
        public VertexPrimitive(params Vertex[] vertices)
            => _vertices = [.. vertices];

        public AABB GetCullingVolume()
        {
            Vector3[] positions = _vertices.Select(x => x.Position).ToArray();
            return new AABB(XRMath.ComponentMin(positions), XRMath.ComponentMax(positions));
        }

        public IEnumerator<Vertex> GetEnumerator()
            => ((IEnumerable<Vertex>)_vertices).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable<Vertex>)_vertices).GetEnumerator();
    }
}