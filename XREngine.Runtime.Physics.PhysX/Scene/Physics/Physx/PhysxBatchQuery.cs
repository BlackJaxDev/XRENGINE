using MagicPhysX;
using System.Numerics;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Scene.Physics.Physx
{
    public unsafe class PhysxBatchQuery(PxBatchQueryExt* batchQuery)
    {
        public PxBatchQueryExt* BatchQueryPtr = batchQuery;

        public void Release()
            => BatchQueryPtr->ReleaseMut();
        public void Execute()
            => BatchQueryPtr->ExecuteMut();

        public PxRaycastBuffer* Raycast(
            Vector3 origin,
            Vector3 unitDir,
            float distance,
            ushort maxTouchCount,
            PxHitFlags hitFlags,
            PxQueryFilterData* filterData,
            PxQueryCache* cache)
        {
            PxVec3 o = origin;
            PxVec3 d = unitDir;
            return BatchQueryPtr->RaycastMut(&o, &d, distance, maxTouchCount, hitFlags, filterData, cache);
        }
        public PxSweepBuffer* Sweep(
            PxGeometry* geometry,
            (Vector3 position, Quaternion rotation) pose,
            Vector3 unitDir,
            float distance,
            ushort maxTouchCount,
            PxHitFlags hitFlags,
            PxQueryFilterData* filterData,
            PxQueryCache* cache,
            float inflation)
        {
            PxVec3 d = unitDir;
            var t = PhysxConversions.MakeTransform(pose.position, pose.rotation);
            return BatchQueryPtr->SweepMut(geometry, &t, &d, distance, maxTouchCount, hitFlags, filterData, cache, inflation);
        }
        public PxOverlapBuffer* Overlap(
            PxGeometry* geometry,
            (Vector3 position, Quaternion rotation) pose,
            ushort maxTouchCount,
            PxQueryFilterData* filterData,
            PxQueryCache* cache)
        {
            var t = PhysxConversions.MakeTransform(pose.position, pose.rotation);
            return BatchQueryPtr->OverlapMut(geometry, &t, maxTouchCount, filterData, cache);
        }
    }
}