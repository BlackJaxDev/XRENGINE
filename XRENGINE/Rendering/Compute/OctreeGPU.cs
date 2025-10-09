using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Data.Trees
{
    public class OctreeGPU<T> : OctreeBase, I3DRenderTree<T> where T : class, IOctreeItem
    {
        struct OctreeNode
        {
            public Vector3 Center;    // x,y,z
            public float Radius;    // bounding sphere
            public uint Offset;    // start in MeshletIndices[]
            public uint Count;     // number of meshlets
        }

        private XRDataBuffer? _drawCommandsBuffer;
        public XRDataBuffer? DrawCommandsBuffer => _drawCommandsBuffer;

        public void Add(T value)
        {
            throw new NotImplementedException();
        }

        public void Add(ITreeItem item)
        {
            throw new NotImplementedException();
        }

        public void AddRange(IEnumerable<T> value)
        {
            throw new NotImplementedException();
        }

        public void AddRange(IEnumerable<ITreeItem> renderedObjects)
        {
            throw new NotImplementedException();
        }

        public void CollectAll(Action<IOctreeItem> action)
        {
            throw new NotImplementedException();
        }

        public void CollectAll(Action<T> action)
        {
            throw new NotImplementedException();
        }

        public void CollectVisible(IVolume? volume, bool onlyContainingItems, Action<T> action, OctreeNode<T>.DelIntersectionTest intersectionTest)
        {
            throw new NotImplementedException();
        }

        public void CollectVisible(IVolume? volume, bool onlyContainingItems, Action<IOctreeItem> action, OctreeNode<IOctreeItem>.DelIntersectionTestGeneric intersectionTest)
        {
            throw new NotImplementedException();
        }

        public void CollectVisibleNodes(IVolume? cullingVolume, bool containsOnly, Action<(OctreeNodeBase node, bool intersects)> action)
        {
            throw new NotImplementedException();
        }

        public void DebugRender(IVolume? cullingVolume, DelRenderAABB render, bool onlyContainingItems = false)
        {
            throw new NotImplementedException();
        }

        public void Remake(AABB newBounds)
        {
            throw new NotImplementedException();
        }

        public void Remake()
        {
            throw new NotImplementedException();
        }

        public void Remove(T value)
        {
            throw new NotImplementedException();
        }

        public void Remove(ITreeItem item)
        {
            throw new NotImplementedException();
        }

        public void RemoveRange(IEnumerable<T> value)
        {
            throw new NotImplementedException();
        }

        public void RemoveRange(IEnumerable<ITreeItem> renderedObjects)
        {
            throw new NotImplementedException();
        }

        public void Swap()
        {
            throw new NotImplementedException();
        }
    }
}
