using MagicPhysX;
using System.Numerics;
using XREngine.Scene.Physics;
using static MagicPhysX.NativeMethods;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Scene.Physics.Physx
{
    public unsafe struct PhysxPlane(PxPlane plane) : IAbstractPhysicsShape
    {
        public PxPlane InternalPlane = plane;

        public void Normalize()
        {
            fixed (PxPlane* p = &InternalPlane)
                PxPlane_normalize_mut(p);
        }
        public readonly PhysxPlane Normalized()
        {
            PxPlane p = InternalPlane;
            PxPlane_normalize_mut(&p);
            return new PhysxPlane(p);
        }
        public float DistanceTo(Vector3 point)
        {
            PxVec3 p = point;
            return InternalPlane.Distance(&p);
        }
        public bool Contains(Vector3 point)
        {
            PxVec3 p = point;
            return InternalPlane.Contains(&p);
        }
        public Vector3 Project(Vector3 point)
        {
            PxVec3 p = point;
            return InternalPlane.Project(&p);
        }
        public Vector3 PointInPlane()
            => InternalPlane.PointInPlane();
        public Vector3 GetPlanePoint()
            => InternalPlane.PointInPlane();
        public void InverseTransform(Vector3 position, Quaternion rotation)
        {
            var tfm = PhysxConversions.MakeTransform(position, rotation);
            InternalPlane = InternalPlane.InverseTransform(&tfm);
        }
        public void Transform(Vector3 position, Quaternion rotation)
        {
            var tfm = PhysxConversions.MakeTransform(position, rotation);
            InternalPlane = InternalPlane.Transform(&tfm);
        }
        public PhysxPlane Transformed(Vector3 position, Quaternion rotation)
        {
            var tfm = PhysxConversions.MakeTransform(position, rotation);
            return new PhysxPlane(InternalPlane.Transform(&tfm));
        }
        public PhysxPlane InverseTransformed(Vector3 position, Quaternion rotation)
        {
            var tfm = PhysxConversions.MakeTransform(position, rotation);
            return new PhysxPlane(InternalPlane.InverseTransform(&tfm));
        }
        public (Vector3 position, Quaternion rotation) TransformFromPlaneEquation()
        {
            var tfm = InternalPlane.PhysPxTransformFromPlaneEquation();
            return (tfm.p, tfm.q);
        }
    }
}