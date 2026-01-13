using MagicPhysX;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe class PhysxTriangleMesh(PhysxScene scene, PxTriangleMesh* triangleMesh) : PhysxRefCounted
    {
        [RuntimeOnly]
        public PxTriangleMesh* TriangleMeshPtr { get; private set; } = triangleMesh;

        [RuntimeOnly]
        public PhysxScene Scene { get; private set; } = scene;
        public override PxRefCounted* RefCountedPtr { get; }

        public override void Release() => TriangleMeshPtr->ReleaseMut();

        public PxTriangleMeshFlags MeshFlags => TriangleMeshPtr->GetTriangleMeshFlags();
        public uint NumTriangles => TriangleMeshPtr->GetNbTriangles();
        public uint NumVertices => TriangleMeshPtr->GetNbVertices();

        public unsafe PxTriangleMeshGeometry NewGeometry(PxMeshScale* scaling, PxMeshGeometryFlags flags)
            => TriangleMeshPtr->GeometryNew(scaling, flags);

        public unsafe Vector3[] GetVertices()
        {
            uint numVertices = TriangleMeshPtr->GetNbVertices();
            PxVec3* vertices = TriangleMeshPtr->GetVertices();
            Vector3[] result = new Vector3[numVertices];
            for (int i = 0; i < numVertices; i++)
                result[i] = vertices[i];
            return result;
        }

        public unsafe Vector3[] GetVerticesForModification()
        {
            uint numVertices = TriangleMeshPtr->GetNbVertices();
            PxVec3* vertices = TriangleMeshPtr->GetVerticesForModificationMut();
            Vector3[] result = new Vector3[numVertices];
            for (int i = 0; i < numVertices; i++)
                result[i] = vertices[i];
            return result;
        }

        public unsafe AABB RefitBVH()
        {
            PxBounds3 bounds = TriangleMeshPtr->RefitBVHMut();
            return new AABB(bounds.minimum, bounds.maximum);
        }

        public unsafe uint[] GetTriangles()
        {
            bool bit16 = MeshFlags.HasFlag(PxTriangleMeshFlags.E16BitIndices);
            uint numTriangleIndices = TriangleMeshPtr->GetNbTriangles() * 3;
            void* ptr = TriangleMeshPtr->GetTriangles();
            if (bit16)
            {
                ushort* triangles = (ushort*)TriangleMeshPtr->GetTriangles();
                uint[] result = new uint[numTriangleIndices];
                for (int i = 0; i < numTriangleIndices; i++)
                    result[i] = triangles[i];
                return result;
            }
            else
            {
                uint* triangles = (uint*)TriangleMeshPtr->GetTriangles();
                uint[] result = new uint[numTriangleIndices];
                for (int i = 0; i < numTriangleIndices; i++)
                    result[i] = triangles[i];
                return result;
            }
        }

        public unsafe uint[] GetTrianglesRemap()
        {
            uint numTriangles = TriangleMeshPtr->GetNbTriangles();
            uint* triangles = TriangleMeshPtr->GetTrianglesRemap();
            uint[] result = new uint[numTriangles];
            for (int i = 0; i < numTriangles; i++)
                result[i] = triangles[i];
            return result;
        }

        public unsafe ushort GetTriangleMaterialIndex(uint triangleIndex)
            => TriangleMeshPtr->GetTriangleMaterialIndex(triangleIndex);

        public unsafe AABB GetLocalBounds()
        {
            PxBounds3 bounds = TriangleMeshPtr->GetLocalBounds();
            return new AABB(bounds.minimum, bounds.maximum);
        }

        public unsafe float[] GetSDF()
        {
            UVector3 dims = GetSDFDimensions();
            uint size = dims.X * dims.Y * dims.Z;
            float* sdf = TriangleMeshPtr->GetSDF();
            float[] result = new float[size];
            for (int i = 0; i < size; i++)
                result[i] = sdf[i];
            return result;
        }

        //public XRTexture3D GetSDFTexture()
        //{
        //    XRTexture3D texture = new();

        //}

        public unsafe UVector3 GetSDFDimensions()
        {
            uint numX, numY, numZ;
            TriangleMeshPtr->GetSDFDimensions(&numX, &numY, &numZ);
            return new UVector3(numX, numY, numZ);
        }

        public unsafe void SetPreferSDFProjectionMut(bool preferProjection)
            => TriangleMeshPtr->SetPreferSDFProjectionMut(preferProjection);

        public unsafe bool GetPreferSDFProjection()
            => TriangleMeshPtr->GetPreferSDFProjection();

        public unsafe void GetMassInformation(out float mass, out Matrix4x4 localInertia, out Vector3 localCenterOfMass)
        {
            float m;
            PxMat33 it;
            PxVec3 centerOfMass;
            TriangleMeshPtr->GetMassInformation(&m, &it, &centerOfMass);
            mass = m;
            Vector3 col0 = it.column0;
            Vector3 col1 = it.column1;
            Vector3 col2 = it.column2;
            localInertia = new(
                col0.X, col1.X, col2.X, 0,
                col0.Y, col1.Y, col2.Y, 0,
                col0.Z, col1.Z, col2.Z, 0,
                0, 0, 0, 1);
            localCenterOfMass = centerOfMass;
        }
    }
}