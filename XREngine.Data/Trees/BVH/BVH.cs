// Copyright(C) David W. Jeske, 2014, and released to the public domain. 
//
// Dynamic BVH (Bounding Volume Hierarchy) using incremental refit and tree-rotations
//
// initial BVH build based on: Bounding Volume Hierarchies (BVH) – A brief tutorial on what they are and how to implement them
//              http://www.3dmuve.com/3dmblog/?p=182
//
// Dynamic Updates based on: "Fast, Effective BVH Updates for Animated Scenes" (Kopta, Ize, Spjut, Brunvand, David, Kensler)
//              https://github.com/jeske/SimpleScene/blob/master/SimpleScene/Util/ssBVH/docs/BVH_fast_effective_updates_for_animated_scenes.pdf
//
// see also:  Space Partitioning: Octree vs. BVH
//            http://thomasdiewald.com/blog/?p=1488
//
//

using System.Numerics;
using XREngine.Data.Geometry;

// TODO: handle merge/split when LEAF_OBJ_MAX > 1 and objects move
// TODO: add sphere traversal

namespace SimpleScene.Util.ssBVH
{
    public enum Axis
    {
        X, Y, Z,
    }

    public interface ISSBVHNodeAdaptor<GO>
    {
        BVH<GO>? BVH { get; }
        void SetBVH(BVH<GO> bvh);
        Vector3 ObjectPos(GO obj);
        float Radius(GO obj);
        void MapObjectToBVHLeaf(GO obj, BVHNode<GO> leaf);
        void UnmapObject(GO obj);
        void CheckMap(GO obj);
        BVHNode<GO>? GetLeaf(GO obj);
    }
    
    public class BVH<GO>
    {
        public readonly int LEAF_OBJ_MAX;

        public BVHNode<GO> _rootBVH;
        public ISSBVHNodeAdaptor<GO> _nAda;
        public int _nodeCount = 0;
        public int _maxDepth = 0;
        public HashSet<BVHNode<GO>> _refitNodes = [];

        public delegate bool NodeTest(AABB box);

        // internal functional traversal...
        private static void Traverse(BVHNode<GO>? curNode, NodeTest hitTest, List<BVHNode<GO>> hitlist)
        {
            if (curNode is null || !hitTest(curNode.box))
                return;

            hitlist.Add(curNode);
            BVH<GO>.Traverse(curNode.left, hitTest, hitlist);
            BVH<GO>.Traverse(curNode.right, hitTest, hitlist);
        }

        // public interface to traversal..
        public List<BVHNode<GO>> Traverse(NodeTest hitTest)
        {
            var hits = new List<BVHNode<GO>>();
            BVH<GO>.Traverse(_rootBVH, hitTest, hits);
            return hits;
        }

        // left in for compatibility..
        public List<BVHNode<GO>> TraverseRay(Ray ray)
        {
            float tnear = 0f, tfar = 0f;
            return Traverse(box => GeoUtil.Intersect.RayWithAABBDistance(ray, box, out tnear, out tfar));
        }

        public List<BVHNode<GO>> Traverse(Ray ray)
        {
            float tnear = 0f, tfar = 0f;
            return Traverse(box => GeoUtil.Intersect.RayWithAABBDistance(ray, box, out tnear, out tfar));
        }

        public List<BVHNode<GO>> Traverse(AABB volume)
            => Traverse(box => GeoUtil.Intersect.AABBWithAABB(box, volume));

        /// <summary>
        /// Call this to batch-optimize any object-changes notified through 
        /// ssBVHNode.refit_ObjectChanged(..). For example, in a game-loop, 
        /// call this once per frame.
        /// </summary>

        public void Optimize()
        {
            if (LEAF_OBJ_MAX != 1)
                throw new Exception("In order to use optimize, you must set LEAF_OBJ_MAX=1");
            
            while (_refitNodes.Count > 0)
            {
                int maxdepth = _refitNodes.Max(n => n.depth);
                var sweepNodes = _refitNodes.Where(n => n.depth == maxdepth).ToList();
                sweepNodes.ForEach(n => _refitNodes.Remove(n));
                sweepNodes.ForEach(n => n.TryRotate(this));
            }
        }

        public void AddObject(GO newOb)
        {
            AABB box = AABB.FromSphere(_nAda.ObjectPos(newOb), _nAda.Radius(newOb));
            float boxSAH = BVHNode<GO>.SA(ref box);
            _rootBVH.AddObject(_nAda, newOb, ref box, boxSAH);
        }

        public void RemoveObject(GO newObj)
        {
            var leaf = _nAda.GetLeaf(newObj);
            leaf?.RemoveObject(_nAda, newObj);
        }

        public int CountBVHNodes()
            => _rootBVH.CountBVHNodes();

        /// <summary>
        /// initializes a BVH with a given nodeAdaptor, and object list.
        /// </summary>
        /// <param name="nodeAdaptor"></param>
        /// <param name="objects"></param>
        /// <param name="LEAF_OBJ_MAX">WARNING! currently this must be 1 to use dynamic BVH updates</param>
        public BVH(ISSBVHNodeAdaptor<GO> nodeAdaptor, List<GO> objects, int LEAF_OBJ_MAX = 1)
        {
            this.LEAF_OBJ_MAX = LEAF_OBJ_MAX;
            nodeAdaptor.SetBVH(this);
            _nAda = nodeAdaptor;

            if (objects.Count > 0)
                _rootBVH = new BVHNode<GO>(this, objects);
            else
            {
                _rootBVH = new BVHNode<GO>(this)
                {
                    gobjects = [] // it's a leaf, so give it an empty object list
                };
            }
        }
    }
}
