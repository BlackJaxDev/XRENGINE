using MagicPhysX;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Scene.Physics.Physx
{
    public unsafe class PhysxTetrahedronMesh(PxTetrahedronMesh* ptr) : PhysxRefCounted
    {
        public PxTetrahedronMesh* TetrahedronMeshPtr = ptr;

        public override PxRefCounted* RefCountedPtr => (PxRefCounted*)TetrahedronMeshPtr;

        public uint VertexCount => TetrahedronMeshPtr->GetNbVertices();
        public Vector3[] GetVertices()
        {
            Vector3[] vertices = new Vector3[VertexCount];
            PxVec3* ptr = TetrahedronMeshPtr->GetVertices();
            for (int i = 0; i < VertexCount; i++)
                vertices[i] = ptr[i];
            return vertices;
        }

        public uint TetrahedronCount => TetrahedronMeshPtr->GetNbTetrahedrons();
        public uint[] GetTetrahedronIndices()
        {
            uint[] indices = new uint[TetrahedronCount * 4];
            void* ptr = TetrahedronMeshPtr->GetTetrahedrons();
            if (Flags.HasFlag(PxTetrahedronMeshFlags.E16BitIndices))
            {
                ushort* ptr16 = (ushort*)ptr;
                for (int i = 0; i < TetrahedronCount * 4; i++)
                    indices[i] = ptr16[i];
            }
            else
            {
                uint* ptr32 = (uint*)ptr;
                for (int i = 0; i < TetrahedronCount * 4; i++)
                    indices[i] = ptr32[i];
            }
            return indices;
        }

        public override void Release()
            => TetrahedronMeshPtr->ReleaseMut();

        public PxTetrahedronMeshFlags Flags => TetrahedronMeshPtr->GetTetrahedronMeshFlags();

        public uint[] GetRemapTable()
        {
            uint[] remap = new uint[TetrahedronCount];
            uint* r = TetrahedronMeshPtr->GetTetrahedraRemap();
            for (int i = 0; i < TetrahedronCount; i++)
                remap[i] = r[i];
            return remap;
        }

        public AABB GetLocalBounds()
        {
            PxBounds3 bounds = TetrahedronMeshPtr->GetLocalBounds();
            return new AABB(bounds.minimum, bounds.maximum);
        }
    }
}