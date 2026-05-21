using System.Drawing;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Data.Trees
{
    public delegate void DelRenderAABB(Vector3 extents, Vector3 center, Color color);
    public interface I3DRenderTree
    {
        void Remake(AABB newBounds);
        void DebugRender(IVolume? cullingVolume, DelRenderAABB render, bool onlyContainingItems = false);
        void CollectVisible(IVolume? volume, bool onlyContainingItems, Action<IOctreeItem> action, OctreeNode<IOctreeItem>.DelIntersectionTestGeneric intersectionTest);
        void CollectAll(Action<IOctreeItem> action);
        void CollectVisibleNodes(IVolume? cullingVolume, bool containsOnly, Action<(OctreeNodeBase node, bool intersects)> action);
        SpatialTreeOccupancyStats GetOccupancyStats();
    }
    public interface I3DRenderTree<T> : I3DRenderTree, IRenderTree<T> where T : class, IOctreeItem
    {
        void CollectVisible(IVolume? volume, bool onlyContainingItems, Action<T> action, OctreeNode<T>.DelIntersectionTest intersectionTest);
    }
}
