// Copyright(C) David W. Jeske, 2014, and released to the public domain. 
//
// Dynamic BVH (Bounding Volume Hierarchy) using incremental refit and tree-rotations
//
// initial BVH build based on: Bounding Volume Hierarchies (BVH) – A brief tutorial on what they are and how to implement them
//              http://www.3dmuve.com/3dmblog/?p=182
//
// Dynamic Updates based on: "Fast, Effective BVH Updates for Animated Scenes" (Kopta, Ize, Spjut, Brunvand, David, Kensler)
//              http://www.cs.utah.edu/~thiago/papers/rotations.pdf
//
// see also:  Space Partitioning: Octree vs. BVH
//            http://thomasdiewald.com/blog/?p=1488
//
// TODO: pick the best axis to split based on SAH, instead of the biggest
// TODO: Switch SAH comparisons to use (SAH(A) * itemCount(A)) currently it just uses SAH(A)
// TODO: when inserting, compare parent node SAH(A) * itemCount to sum of children, to see if it is better to not split at all
// TODO: implement node merge/split, to handle updates when LEAF_OBJ_MAX > 1
// 
// TODO: implement SBVH spacial splits
//        http://www.nvidia.com/docs/IO/77714/sbvh.pdf

using System.Diagnostics;
using System.Numerics;
using XREngine.Data.Geometry;

namespace SimpleScene.Util.ssBVH
{
    public class BVHNode<GO>
    {
        public AABB box;

        public BVHNode<GO>? parent;
        public BVHNode<GO>? left;
        public BVHNode<GO>? right;

        public int depth;
        public int nodeNumber; // for debugging

        public List<GO>? gobjects; // only populated in leaf nodes

        public override string ToString()
            => string.Format("ssBVHNode<{0}>:{1}", typeof(GO), nodeNumber);

        private Axis PickSplitAxis()
        {
            float axis_x = box.Max.X - box.Min.X;
            float axis_y = box.Max.Y - box.Min.Y;
            float axis_z = box.Max.Z - box.Min.Z;

            // return the biggest axis
            return axis_x > axis_y 
                ? axis_x > axis_z 
                    ? Axis.X 
                    : Axis.Z 
                : axis_y > axis_z 
                    ? Axis.Y 
                    : Axis.Z;
        }

        public bool IsLeaf
        {
            get
            {
                bool isLeaf = gobjects != null;

                // if we're a leaf, then both left and right should be null..
                if (isLeaf && ((right != null) || (left != null)))
                    throw new Exception("ssBVH Leaf has objects and left/right pointers!");
                
                return isLeaf;
            }
        }

        private static Axis NextAxis(Axis cur) => cur switch
        {
            Axis.X => Axis.Y,
            Axis.Y => Axis.Z,
            Axis.Z => Axis.X,
            _ => throw new NotSupportedException(),
        };

        /// <summary>
        /// Call this to refit the bounding volume of this node and all parents
        /// </summary>
        /// <param name="nAda"></param>
        /// <param name="obj"></param>
        /// <exception cref="Exception"></exception>
        public void Refit_ObjectChanged(ISSBVHNodeAdaptor<GO> nAda)
        {
            if (gobjects == null)
                throw new Exception("dangling leaf!");

            if (!RefitVolume(nAda) || parent == null)
                return;

            nAda.BVH?._refitNodes?.Add(parent);

            // you can force an optimize every time something moves, but it's not very efficient
            // instead we do this per-frame after a bunch of updates.
            // nAda.BVH.optimize();                    
        }

        private void ExpandVolume(ISSBVHNodeAdaptor<GO> nAda, Vector3 objectpos, float radius)
        {
            bool expanded = false;

            var minX = box.Min.X;
            var maxX = box.Max.X;
            var minY = box.Min.Y;
            var maxY = box.Max.Y;
            var minZ = box.Min.Z;
            var maxZ = box.Max.Z;

            // test min X and max X against the current bounding volume
            if ((objectpos.X - radius) < box.Min.X)
            {
                minX = (objectpos.X - radius);
                expanded = true;
            }
            if ((objectpos.X + radius) > box.Max.X)
            {
                maxX = (objectpos.X + radius);
                expanded = true;
            }

            // test min Y and max Y against the current bounding volume
            if ((objectpos.Y - radius) < box.Min.Y)
            {
                minY = (objectpos.Y - radius);
                expanded = true;
            }
            if ((objectpos.Y + radius) > box.Max.Y)
            {
                maxY = (objectpos.Y + radius);
                expanded = true;
            }

            // test min Z and max Z against the current bounding volume
            if ((objectpos.Z - radius) < box.Min.Z)
            {
                minZ = (objectpos.Z - radius);
                expanded = true;
            }
            if ((objectpos.Z + radius) > box.Max.Z)
            {
                maxZ = (objectpos.Z + radius);
                expanded = true;
            }

            if (expanded && parent != null)
            {
                box = new AABB(
                    new Vector3(minX, minY, minZ),
                    new Vector3(maxX, maxY, maxZ));
                parent.ChildExpanded(nAda, this);
            }
        }

        private void AssignVolume(Vector3 objectpos, float radius)
        {
            var minX = objectpos.X - radius;
            var maxX = objectpos.X + radius;
            var minY = objectpos.Y - radius;
            var maxY = objectpos.Y + radius;
            var minZ = objectpos.Z - radius;
            var maxZ = objectpos.Z + radius;

            box = new AABB(
                new Vector3(minX, minY, minZ),
                new Vector3(maxX, maxY, maxZ));
        }

        internal void ComputeVolume(ISSBVHNodeAdaptor<GO> nAda)
        {
            AssignVolume(nAda.ObjectPos(gobjects![0]), nAda.Radius(gobjects[0]));
            for (int i = 1; i < gobjects.Count; i++)
                ExpandVolume(nAda, nAda.ObjectPos(gobjects[i]), nAda.Radius(gobjects[i]));
        }

        internal bool RefitVolume(ISSBVHNodeAdaptor<GO> nAda)
        {
            if (gobjects?.Count == 0)
                throw new NotImplementedException();  // TODO: fix this... we should never get called in this case...

            AABB oldbox = box;

            ComputeVolume(nAda);
            if (!box.Equals(oldbox))
            {
                parent?.ChildRefit(nAda);
                return true;
            }
            else
                return false;
        }

        internal static float SA(AABB box)
        {
            float x_size = box.Max.X - box.Min.X;
            float y_size = box.Max.Y - box.Min.Y;
            float z_size = box.Max.Z - box.Min.Z;

            return 2.0f * ((x_size * y_size) + (x_size * z_size) + (y_size * z_size));

        }
        internal static float SA(ref AABB box)
        {
            float x_size = box.Max.X - box.Min.X;
            float y_size = box.Max.Y - box.Min.Y;
            float z_size = box.Max.Z - box.Min.Z;

            return 2.0f * ((x_size * y_size) + (x_size * z_size) + (y_size * z_size));

        }
        internal static float SA(BVHNode<GO> node)
        {
            float x_size = node.box.Max.X - node.box.Min.X;
            float y_size = node.box.Max.Y - node.box.Min.Y;
            float z_size = node.box.Max.Z - node.box.Min.Z;

            return 2.0f * ((x_size * y_size) + (x_size * z_size) + (y_size * z_size));
        }
        internal static float SA(ISSBVHNodeAdaptor<GO> nAda, GO obj)
        {
            float radius = nAda.Radius(obj);

            float size = radius * 2;
            return 6.0f * (size * size);
        }

        internal static AABB AABBofPair(BVHNode<GO> nodea, BVHNode<GO> nodeb)
        {
            AABB box = nodea.box;
            box.ExpandToInclude(nodeb.box);
            return box;
        }

        internal static float SAofPair(BVHNode<GO> nodea, BVHNode<GO> nodeb)
        {
            AABB box = nodea.box;
            box.ExpandToInclude(nodeb.box);
            return SA(ref box);
        }
        internal static float SAofPair(AABB boxa, AABB boxb)
        {
            AABB pairbox = boxa;
            pairbox.ExpandToInclude(boxb);
            return SA(ref pairbox);
        }
        internal static AABB AABBofOBJ(ISSBVHNodeAdaptor<GO> nAda, GO obj)
        {
            float radius = nAda.Radius(obj);
            return new AABB(new Vector3(-radius), new Vector3(radius));
        }

        internal static float SAofList(ISSBVHNodeAdaptor<GO> nAda, List<GO> list)
        {
            var box = AABBofOBJ(nAda, list[0]);

            list.ToList().GetRange(1, list.Count - 1).ForEach(obj => {
                var newbox = AABBofOBJ(nAda, obj);
                box.ExpandToInclude(newbox);
            });

            return SA(box);
        }

        // The list of all candidate rotations, from "Fast, Effective BVH Updates for Animated Scenes", Figure 1.
        internal enum Rot
        {
            NONE, L_RL, L_RR, R_LL, R_LR, LL_RR, LL_RL,
        }

        internal class RotOpt : IComparable<RotOpt>
        {  // rotation option
            public float SAH;
            public Rot rot;
            internal RotOpt(float SAH, Rot rot)
            {
                this.SAH = SAH;
                this.rot = rot;
            }
            public int CompareTo(RotOpt? other)
            {
                return SAH.CompareTo(other?.SAH);
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static List<Rot> EachRot => new(Enum.GetValues<Rot>());

        private static BVH<GO> RequireBVH(ISSBVHNodeAdaptor<GO> adaptor)
            => adaptor.BVH ?? throw new InvalidOperationException("Adaptor must be bound to a BVH instance.");

        /// <summary>
        /// tryRotate looks at all candidate rotations, and executes the rotation with the best resulting SAH (if any)
        /// </summary>
        /// <param name="bvh"></param>
        internal void TryRotate(BVH<GO> bvh)
        {
            ISSBVHNodeAdaptor<GO> nAda = bvh._nAda;

            BVHNode<GO>? leftChild = left;
            BVHNode<GO>? rightChild = right;

            if (leftChild is null || rightChild is null)
            {
                if (parent != null)
                    bvh._refitNodes.Add(parent);
                return;
            }

            // if we are not a grandparent, then we can't rotate, so queue our parent and bail out
            if (leftChild.IsLeaf && rightChild.IsLeaf)
            {
                if (parent != null)
                {
                    bvh._refitNodes.Add(parent);
                }
                return;
            }

            // for each rotation, check that there are grandchildren as necessary (aka not a leaf)
            // then compute total SAH cost of our branches after the rotation.

            float mySA = SA(leftChild) + SA(rightChild);

            RotOpt bestRot = EachRot.Min((rot) =>
            {
                return rot switch
                {
                    Rot.NONE => new RotOpt(mySA, Rot.NONE),
                    Rot.L_RL => (rightChild.IsLeaf || rightChild.left is null || rightChild.right is null)
                        ? new RotOpt(float.MaxValue, Rot.NONE)
                        : new RotOpt(SA(rightChild.left) + SA(AABBofPair(leftChild, rightChild.right)), rot),
                    Rot.L_RR => (rightChild.IsLeaf || rightChild.left is null || rightChild.right is null)
                        ? new RotOpt(float.MaxValue, Rot.NONE)
                        : new RotOpt(SA(rightChild.right) + SA(AABBofPair(leftChild, rightChild.left)), rot),
                    Rot.R_LL => (leftChild.IsLeaf || leftChild.left is null || leftChild.right is null)
                        ? new RotOpt(float.MaxValue, Rot.NONE)
                        : new RotOpt(SA(AABBofPair(rightChild, leftChild.right)) + SA(leftChild.left), rot),
                    Rot.R_LR => (leftChild.IsLeaf || leftChild.left is null || leftChild.right is null)
                        ? new RotOpt(float.MaxValue, Rot.NONE)
                        : new RotOpt(SA(AABBofPair(rightChild, leftChild.left)) + SA(leftChild.right), rot),
                    Rot.LL_RR => (leftChild.IsLeaf || rightChild.IsLeaf || leftChild.left is null || leftChild.right is null || rightChild.left is null || rightChild.right is null)
                        ? new RotOpt(float.MaxValue, Rot.NONE)
                        : new RotOpt(SA(AABBofPair(rightChild.right, leftChild.right)) + SA(AABBofPair(rightChild.left, leftChild.left)), rot),
                    Rot.LL_RL => (leftChild.IsLeaf || rightChild.IsLeaf || leftChild.left is null || leftChild.right is null || rightChild.left is null || rightChild.right is null)
                        ? new RotOpt(float.MaxValue, Rot.NONE)
                        : new RotOpt(SA(AABBofPair(rightChild.left, leftChild.right)) + SA(AABBofPair(leftChild.left, rightChild.right)), rot),
                    _ => throw new NotImplementedException($"missing implementation for BVH Rotation SAH Computation .. {rot}"),
                };
            }) ?? new RotOpt(mySA, Rot.NONE);

            // perform the best rotation...
            if (bestRot.rot != Rot.NONE)
            {
                if (parent != null && (DateTime.Now.Ticks % 100) < 2)
                {
                    bvh._refitNodes.Add(parent);
                }
            }
            else
            {
                if (parent != null)
                    bvh._refitNodes.Add(parent);

                if (((mySA - bestRot.SAH) / mySA) < 0.3f)
                    return; // the benefit is not worth the cost

                Console.WriteLine("BVH swap {0} from {1} to {2}", bestRot.rot.ToString(), mySA, bestRot.SAH);

                // in order to swap we need to:
                //  1. swap the node locations
                //  2. update the depth (if child-to-grandchild)
                //  3. update the parent pointers
                //  4. refit the boundary box
                BVHNode<GO>? swap = null;
                switch (bestRot.rot)
                {
                    case Rot.NONE:
                        break;

                    // child to grandchild rotations
                    case Rot.L_RL:
                        swap = left!;
                        left = right!.left;
                        if (left != null)
                            left.parent = this;
                        right.left = swap;
                        swap.parent = right;
                        right.ChildRefit(nAda, propagate: false);
                        break;

                    case Rot.L_RR:
                        swap = left!;
                        left = right!.right;
                        if (left != null)
                            left.parent = this;
                        right.right = swap;
                        swap.parent = right;
                        right.ChildRefit(nAda, propagate: false);
                        break;

                    case Rot.R_LL:
                        swap = right!;
                        right = left!.left;
                        if (right != null)
                            right.parent = this;
                        left.left = swap;
                        swap.parent = left;
                        left.ChildRefit(nAda, propagate: false);
                        break;

                    case Rot.R_LR:
                        swap = right!;
                        right = left!.right;
                        if (right != null)
                            right.parent = this;
                        left.right = swap;
                        swap.parent = left;
                        left.ChildRefit(nAda, propagate: false);
                        break;

                    // grandchild to grandchild rotations
                    case Rot.LL_RR:
                        swap = left!.left;
                        left.left = right!.right;
                        right.right = swap;
                        if (left.left != null)
                            left.left.parent = left;
                        if (swap != null)
                            swap.parent = right;
                        left.ChildRefit(nAda, propagate: false);
                        right.ChildRefit(nAda, propagate: false);
                        break;

                    case Rot.LL_RL:
                        swap = left!.left;
                        left.left = right!.left;
                        right.left = swap;
                        if (left.left != null)
                            left.left.parent = left;
                        if (swap != null)
                            swap.parent = right;
                        left.ChildRefit(nAda, propagate: false);
                        right.ChildRefit(nAda, propagate: false);
                        break;

                    default:
                        throw new NotImplementedException($"missing implementation for BVH Rotation .. {bestRot.rot}");
                }

                // fix the depths if necessary....
                switch (bestRot.rot)
                {
                    case Rot.L_RL:
                    case Rot.L_RR:
                    case Rot.R_LL:
                    case Rot.R_LR:
                        SetDepth(nAda, depth);
                        break;
                }
            }

        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private static List<Axis> EachAxis => new(Enum.GetValues<Axis>());

        internal class SplitAxisOpt : IComparable<SplitAxisOpt>
        {
            // split Axis option
            public float SAH;
            public Axis axis;
            public List<GO> left, right;

            internal SplitAxisOpt(float SAH, Axis axis, List<GO> left, List<GO> right)
            {
                this.SAH = SAH;
                this.axis = axis;
                this.left = left;
                this.right = right;
            }

            public int CompareTo(SplitAxisOpt? other)
                => SAH.CompareTo(other?.SAH);
        }

        internal void SplitNode(ISSBVHNodeAdaptor<GO> nAda)
        {
            // second, decide which axis to split on, and sort..
            List<GO>? splitlistNullable = gobjects;
            if (splitlistNullable is null)
                throw new InvalidOperationException("Cannot split a BVH node without objects.");

            List<GO> splitlist = splitlistNullable;
            splitlist.ForEach(o => nAda.UnmapObject(o));
            int center = splitlist.Count / 2; // find the center object

            SplitAxisOpt? bestSplitOpt = EachAxis.Min((axis) =>
            {
                var orderedlist = new List<GO>(splitlist);
                switch (axis)
                {
                    case Axis.X:
                        orderedlist.Sort(delegate (GO go1, GO go2) { return nAda.ObjectPos(go1).X.CompareTo(nAda.ObjectPos(go2).X); });
                        break;
                    case Axis.Y:
                        orderedlist.Sort(delegate (GO go1, GO go2) { return nAda.ObjectPos(go1).Y.CompareTo(nAda.ObjectPos(go2).Y); });
                        break;
                    case Axis.Z:
                        orderedlist.Sort(delegate (GO go1, GO go2) { return nAda.ObjectPos(go1).Z.CompareTo(nAda.ObjectPos(go2).Z); });
                        break;
                    default:
                        throw new NotImplementedException("unknown split axis: " + axis.ToString());
                }

                var left_s = orderedlist.GetRange(0, center);
                var right_s = orderedlist.GetRange(center, splitlist.Count - center);

                float SAH = BVHNode<GO>.SAofList(nAda, left_s) * left_s.Count + BVHNode<GO>.SAofList(nAda, right_s) * right_s.Count;
                return new SplitAxisOpt(SAH, axis, left_s, right_s);
            });

            SplitAxisOpt bestSplit = bestSplitOpt ?? throw new InvalidOperationException("Unable to determine BVH split axis.");

            // perform the split
            gobjects = null;
            BVH<GO> bvh = RequireBVH(nAda);
            left = new BVHNode<GO>(bvh, this, bestSplit.left, bestSplit.axis, this.depth + 1); // Split the Hierarchy to the left
            right = new BVHNode<GO>(bvh, this, bestSplit.right, bestSplit.axis, this.depth + 1); // Split the Hierarchy to the right                                
        }

        internal void SplitIfNecessary(ISSBVHNodeAdaptor<GO> nAda)
        {
            if (gobjects is null)
                return;

            if (gobjects.Count > RequireBVH(nAda).LEAF_OBJ_MAX)
                SplitNode(nAda);
        }

        internal void AddObject(ISSBVHNodeAdaptor<GO> nAda, GO newOb, ref AABB newObBox, float newObSAH)
        {
            AddObject(nAda, this, newOb, ref newObBox, newObSAH);
        }

        internal static void AddObject_Pushdown(ISSBVHNodeAdaptor<GO> nAda, BVHNode<GO> curNode, GO newOb)
        {
            var left = curNode.left ?? throw new InvalidOperationException("Left child missing during pushdown.");
            var right = curNode.right ?? throw new InvalidOperationException("Right child missing during pushdown.");
            BVH<GO> bvh = RequireBVH(nAda);

            // merge and pushdown left and right as a new node..
            var mergedSubnode = new BVHNode<GO>(bvh)
            {
                left = left,
                right = right,
                parent = curNode,
                gobjects = null // we need to be an interior node... so null out our object list..
            };
            left.parent = mergedSubnode;
            right.parent = mergedSubnode;
            mergedSubnode.ChildRefit(nAda, propagate: false);

            // make new subnode for obj
            var newSubnode = new BVHNode<GO>(bvh)
            {
                parent = curNode,
                gobjects = [newOb]
            };
            nAda.MapObjectToBVHLeaf(newOb, newSubnode);
            newSubnode.ComputeVolume(nAda);

            // make assignments..
            curNode.left = mergedSubnode;
            curNode.right = newSubnode;
            curNode.SetDepth(nAda, curNode.depth); // propagate new depths to our children.
            curNode.ChildRefit(nAda);
        }
        internal static void AddObject(ISSBVHNodeAdaptor<GO> nAda, BVHNode<GO> curNode, GO newOb, ref AABB newObBox, float newObSAH)
        {
            // 1. first we traverse the node looking for the best leaf
            while (curNode.gobjects == null)
            {
                // find the best way to add this object.. 3 options..
                // 1. send to left node  (L+N,R)
                // 2. send to right node (L,R+N)
                // 3. merge and pushdown left-and-right node (L+R,N)

                var left = curNode.left;
                var right = curNode.right;
                if (left is null || right is null)
                    throw new InvalidOperationException("Interior BVH nodes must have both children.");

                float leftSAH = SA(left);
                float rightSAH = SA(right);
                float sendLeftSAH = rightSAH + SA(left.box.ExpandedToInclude(newObBox));    // (L+N,R)
                float sendRightSAH = leftSAH + SA(right.box.ExpandedToInclude(newObBox));   // (L,R+N)
                float mergedLeftAndRightSAH = SA(AABBofPair(left, right)) + newObSAH; // (L+R,N)

                // Doing a merge-and-pushdown can be expensive, so we only do it if it's notably better
                const float MERGE_DISCOUNT = 0.3f;

                if (mergedLeftAndRightSAH < (Math.Min(sendLeftSAH, sendRightSAH)) * MERGE_DISCOUNT)
                {
                    AddObject_Pushdown(nAda, curNode, newOb);
                    return;
                }
                else
                {
                    if (sendLeftSAH < sendRightSAH)
                    {
                        curNode = left;
                    }
                    else
                    {
                        curNode = right;
                    }
                }
            }

            // 2. then we add the object and map it to our leaf
            curNode.gobjects.Add(newOb);
            nAda.MapObjectToBVHLeaf(newOb, curNode);
            curNode.RefitVolume(nAda);
            // split if necessary...
            curNode.SplitIfNecessary(nAda);
        }

        internal int CountBVHNodes()
            => gobjects != null 
                ? 1 
                : (left?.CountBVHNodes() ?? 0) + (right?.CountBVHNodes() ?? 0);

        internal void RemoveObject(ISSBVHNodeAdaptor<GO> nAda, GO newOb)
        {
            if (gobjects == null)
                throw new Exception("removeObject() called on nonLeaf!");

            nAda.UnmapObject(newOb);
            gobjects.Remove(newOb);

            if (gobjects.Count > 0)
                RefitVolume(nAda);
            else if (parent != null) // our leaf is empty, so collapse it if we are not the root...
            {
                gobjects = null;
                parent.RemoveLeaf(nAda, this);
                parent = null;
            }
        }

        void SetDepth(ISSBVHNodeAdaptor<GO> nAda, int newdepth)
        {
            depth = newdepth;

            BVH<GO> bvh = RequireBVH(nAda);
            if (newdepth > bvh._maxDepth)
                bvh._maxDepth = newdepth;
            
            if (gobjects == null)
            {
                left?.SetDepth(nAda, newdepth + 1);
                right?.SetDepth(nAda, newdepth + 1);
            }
        }

        internal void RemoveLeaf(ISSBVHNodeAdaptor<GO> nAda, BVHNode<GO> removeLeaf)
        {
            if (left == null || right == null)
                throw new Exception("bad intermediate node");

            BVHNode<GO> keepLeaf;

            if (removeLeaf == left)
                keepLeaf = right;
            else if (removeLeaf == right)
                keepLeaf = left;
            else
                throw new Exception("removeLeaf doesn't match any leaf!");
            
            // "become" the leaf we are keeping.
            box = keepLeaf.box;
            left = keepLeaf.left; right = keepLeaf.right; gobjects = keepLeaf.gobjects;
            // clear the leaf..
            // keepLeaf.left = null; keepLeaf.right = null; keepLeaf.gobjects = null; keepLeaf.parent = null; 

            if (gobjects == null)
            {
                // reassign child parents
                if (left != null)
                    left.parent = this;
                if (right != null)
                    right.parent = this;

                // this reassigns depth for our children
                SetDepth(nAda, depth);
            }
            else
            {
                // map the objects we adopted to us...                                                
                gobjects.ForEach(o => nAda.MapObjectToBVHLeaf(o, this));
            }

            // propagate our new volume..
            parent?.ChildRefit(nAda);
        }

        internal BVHNode<GO> RootNode()
        {
            BVHNode<GO> cur = this;
            while (cur.parent != null) 
                cur = cur.parent;
            return cur;
        }


        internal void FindOverlappingLeaves(ISSBVHNodeAdaptor<GO> nAda, Vector3 origin, float radius, List<BVHNode<GO>> overlapList)
        {
            var box = ToAABB();
            if (!GeoUtil.AABBIntersectsSphere(box.Min, box.Max, origin, radius))
                return;
            
            if (gobjects != null)
                overlapList.Add(this);
            else
            {
                left?.FindOverlappingLeaves(nAda, origin, radius, overlapList);
                right?.FindOverlappingLeaves(nAda, origin, radius, overlapList);
            }
        }

        internal void FindOverlappingLeaves(ISSBVHNodeAdaptor<GO> nAda, AABB aabb, List<BVHNode<GO>> overlapList)
        {
            var box = ToAABB();
            if (!GeoUtil.AABBIntersectsAABB(box, aabb))
                return;
            
            if (gobjects != null)
                overlapList.Add(this);
            else
            {
                left?.FindOverlappingLeaves(nAda, aabb, overlapList);
                right?.FindOverlappingLeaves(nAda, aabb, overlapList);
            }
        }

        internal AABB ToAABB() => box;

        internal void ChildExpanded(ISSBVHNodeAdaptor<GO> nAda, BVHNode<GO> child)
        {
            bool expanded = false;

            var minX = box.Min.X;
            var maxX = box.Max.X;
            var minY = box.Min.Y;
            var maxY = box.Max.Y;
            var minZ = box.Min.Z;
            var maxZ = box.Max.Z;

            if (child.box.Min.X < box.Min.X)
            {
                minX = child.box.Min.X;
                expanded = true;
            }
            if (child.box.Max.X > box.Max.X)
            {
                maxX = child.box.Max.X;
                expanded = true;
            }
            if (child.box.Min.Y < box.Min.Y)
            {
                minY = child.box.Min.Y;
                expanded = true;
            }
            if (child.box.Max.Y > box.Max.Y)
            {
                maxY = child.box.Max.Y;
                expanded = true;
            }
            if (child.box.Min.Z < box.Min.Z)
            {
                minZ = child.box.Min.Z;
                expanded = true;
            }
            if (child.box.Max.Z > box.Max.Z)
            {
                maxZ = child.box.Max.Z;
                expanded = true;
            }

            if (expanded && parent != null)
            {
                box = new AABB(
                    new Vector3(minX, minY, minZ),
                    new Vector3(maxX, maxY, maxZ));
                parent.ChildExpanded(nAda, this);
            }
        }

        internal void ChildRefit(ISSBVHNodeAdaptor<GO> nAda, bool propagate = true)
            => ChildRefit(nAda, this, propagate: propagate);

        internal static void ChildRefit(ISSBVHNodeAdaptor<GO> nAda, BVHNode<GO>? curNode, bool propagate = true)
        {
            while (curNode != null)
            {
                BVHNode<GO>? left = curNode.left;
                BVHNode<GO>? right = curNode.right;
                if (left is null || right is null)
                    break;

                // start with the left box
                AABB newBox = left.box;

                float newMinX = newBox.Min.X;
                float newMinY = newBox.Min.Y;
                float newMinZ = newBox.Min.Z;
                float newMaxX = newBox.Max.X;
                float newMaxY = newBox.Max.Y;
                float newMaxZ = newBox.Max.Z;

                // expand any dimension bigger in the right node
                if (right.box.Min.X < newBox.Min.X)
                    newMinX = right.box.Min.X;
                if (right.box.Min.Y < newBox.Min.Y)
                    newMinY = right.box.Min.Y;
                if (right.box.Min.Z < newBox.Min.Z)
                    newMinZ = right.box.Min.Z;

                if (right.box.Max.X > newBox.Max.X)
                    newMaxX = right.box.Max.X;
                if (right.box.Max.Y > newBox.Max.Y)
                    newMaxY = right.box.Max.Y;
                if (right.box.Max.Z > newBox.Max.Z)
                    newMaxZ = right.box.Max.Z;

                newBox = new AABB(
                    new Vector3(newMinX, newMinY, newMinZ),
                    new Vector3(newMaxX, newMaxY, newMaxZ));

                curNode.box = newBox;

                if (!propagate)
                    break;

                curNode = curNode.parent;
            }
        }

        internal BVHNode(BVH<GO> bvh)
        {
            gobjects = [];
            left = right = null;
            parent = null;
            nodeNumber = bvh._nodeCount++;
        }

        internal BVHNode(BVH<GO> bvh, List<GO> gobjectlist)
            : this(bvh, null, gobjectlist, Axis.X, 0) { }

        private BVHNode(
            BVH<GO> bvh,
            BVHNode<GO>? lparent,
            List<GO> gobjectlist,
            Axis lastSplitAxis,
            int curdepth)
        {
            ISSBVHNodeAdaptor<GO> nAda = bvh._nAda;
            nodeNumber = bvh._nodeCount++;

            parent = lparent; // save off the parent BVHGObj Node
            depth = curdepth;

            if (bvh._maxDepth < curdepth)
                bvh._maxDepth = curdepth;
            
            // Early out check due to bad data
            // If the list is empty then we have no BVHGObj, or invalid parameters are passed in
            if (gobjectlist == null || gobjectlist.Count < 1)
                throw new Exception("ssBVHNode constructed with invalid paramaters");
            
            // Check if we’re at our LEAF node, and if so, save the objects and stop recursing.  Also store the min/max for the leaf node and update the parent appropriately
            if (gobjectlist.Count <= bvh.LEAF_OBJ_MAX)
            {
                // once we reach the leaf node, we must set prev/next to null to signify the end
                left = null;
                right = null;
                // at the leaf node we store the remaining objects, so initialize a list
                gobjects = gobjectlist;
                gobjects.ForEach(o => nAda.MapObjectToBVHLeaf(o, this));
                ComputeVolume(nAda);
                SplitIfNecessary(nAda);
            }
            else
            {
                // --------------------------------------------------------------------------------------------
                // if we have more than (bvh.LEAF_OBJECT_COUNT) objects, then compute the volume and split
                gobjects = gobjectlist;
                ComputeVolume(nAda);
                SplitNode(nAda);
                ChildRefit(nAda, propagate: false);
            }
        }
    }
}
