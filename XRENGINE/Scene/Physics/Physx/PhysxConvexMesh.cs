using System;
using MagicPhysX;
using System.Numerics;
using XREngine.Data.Geometry;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class PhysxConvexMesh : PhysxRefCounted
    {
        public PhysxConvexMesh(PxConvexMesh* meshPtr)
        {
            ArgumentNullException.ThrowIfNull(meshPtr);
            ConvexMeshPtr = meshPtr;
        }

        public PxConvexMesh* ConvexMeshPtr { get; }
        public override PxRefCounted* RefCountedPtr => (PxRefCounted*)ConvexMeshPtr;

        public uint VertexCount => ConvexMeshPtr->GetNbVertices();
        public uint PolygonCount => ConvexMeshPtr->GetNbPolygons();
        public bool IsGpuCompatible => ConvexMeshPtr->IsGpuCompatible();

        public Vector3[] GetVertices()
        {
            uint count = ConvexMeshPtr->GetNbVertices();
            if (count == 0)
                return Array.Empty<Vector3>();

            var result = new Vector3[count];
            var vertices = ConvexMeshPtr->GetVertices();
            for (int i = 0; i < count; i++)
                result[i] = vertices[i];
            return result;
        }

        public AABB GetLocalBounds()
        {
            PxBounds3 bounds = ConvexMeshPtr->GetLocalBounds();
            return new AABB(bounds.minimum, bounds.maximum);
        }

        public void GetMassInformation(out float mass, out Matrix4x4 localInertia, out Vector3 localCenterOfMass)
        {
            float m;
            PxMat33 inertia;
            PxVec3 centerOfMass;
            ConvexMeshPtr->GetMassInformation(&m, &inertia, &centerOfMass);
            mass = m;

            Vector3 col0 = inertia.column0;
            Vector3 col1 = inertia.column1;
            Vector3 col2 = inertia.column2;
            localInertia = new Matrix4x4(
                col0.X, col1.X, col2.X, 0.0f,
                col0.Y, col1.Y, col2.Y, 0.0f,
                col0.Z, col1.Z, col2.Z, 0.0f,
                0.0f, 0.0f, 0.0f, 1.0f);
            localCenterOfMass = centerOfMass;
        }

        public PxConvexMeshGeometry NewGeometry(Vector3 scale, Quaternion rotation, bool tightBounds = false)
        {
            PxVec3 s = scale;
            PxQuat r = rotation;
            PxMeshScale meshScale = PxMeshScale_new_3(&s, &r);
            PxConvexMeshGeometryFlags flags = tightBounds ? PxConvexMeshGeometryFlags.TightBounds : 0;
            return PxConvexMeshGeometry_new(ConvexMeshPtr, &meshScale, flags);
        }

        public override void Release()
        {
            if (ConvexMeshPtr != null)
                ConvexMeshPtr->ReleaseMut();
        }
    }
}
