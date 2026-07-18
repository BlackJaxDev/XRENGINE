using SimpleScene.Util.ssBVH;
using System.Numerics;
using Triangle = XREngine.Data.Geometry.Triangle;

namespace XREngine.Rendering
{
    public class TriangleAdapter : ISSBVHNodeAdaptor<Triangle>, IExactBvhBoundsAdaptor<Triangle>
    {
        public BVH<Triangle>? BVH { get; private set; }

        public void SetBVH(BVH<Triangle> bvh)
            => BVH = bvh;

        private readonly Dictionary<Triangle, BVHNode<Triangle>> _triangleToLeaf = [];

        public void UnmapObject(Triangle obj)
            => _triangleToLeaf.Remove(obj);

        public void CheckMap(Triangle obj)
        {
            if (!_triangleToLeaf.ContainsKey(obj))
                throw new Exception("missing map for a shuffled child");
        }

        public BVHNode<Triangle>? GetLeaf(Triangle obj)
            => _triangleToLeaf.TryGetValue(obj, out BVHNode<Triangle>? leaf) ? leaf : null;

        public void MapObjectToBVHLeaf(Triangle obj, BVHNode<Triangle> leaf)
            => _triangleToLeaf.TryAdd(obj, leaf);

        public Vector3 ObjectPos(Triangle obj)
            => (obj.A + obj.B + obj.C) / 3.0f;

        public float Radius(Triangle obj)
        {
            //Calc center of triangle
            Vector3 center = ObjectPos(obj);
            //Calc distance to each vertex
            float distA = (center - obj.A).Length();
            float distB = (center - obj.B).Length();
            float distC = (center - obj.C).Length();
            //Return the max distance
            return Math.Max(distA, Math.Max(distB, distC));
        }

        public AABB ObjectBounds(Triangle obj)
            => new(Vector3.Min(obj.A, Vector3.Min(obj.B, obj.C)), Vector3.Max(obj.A, Vector3.Max(obj.B, obj.C)));
    }
}
