using JoltPhysicsSharp;
using MagicPhysX;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Geometry;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx
{
    public unsafe interface IPhysicsGeometry
    {
        DataSource GetPhysxStruct();
        public PxGeometry* AsPhysxPtr() => GetPhysxStruct().Address.As<PxGeometry>();
        public Shape AsJoltShape();

        public unsafe struct Sphere(float radius) : IPhysicsGeometry
        {
            public float Radius = radius;

            public readonly PxSphereGeometry GetGeometry() => PxSphereGeometry_new(Radius);
            public readonly DataSource GetPhysxStruct() => DataSource.FromStruct(GetGeometry());
            public readonly Shape AsJoltShape() => new SphereShape(Radius);
        }
        public unsafe struct Box(Vector3 halfExtents) : IPhysicsGeometry
        {
            public Vector3 HalfExtents = halfExtents;

            public readonly PxBoxGeometry GetGeometry() => PxBoxGeometry_new(HalfExtents.X, HalfExtents.Y, HalfExtents.Z);
            public readonly DataSource GetPhysxStruct() => DataSource.FromStruct(GetGeometry());
            public readonly Shape AsJoltShape() => new BoxShape(HalfExtents);
        }
        public unsafe struct Capsule(float radius, float halfHeight) : IPhysicsGeometry
        {
            public float Radius = radius;
            public float HalfHeight = halfHeight;

            public readonly PxCapsuleGeometry GetGeometry() => PxCapsuleGeometry_new(Radius, HalfHeight);
            public readonly DataSource GetPhysxStruct() => DataSource.FromStruct(GetGeometry());
            public readonly Shape AsJoltShape() => new CapsuleShape(HalfHeight, Radius);
        }
        public unsafe struct ConvexMesh(PxConvexMesh* mesh, Vector3 scale, Quaternion rotation, bool tightBounds) : IPhysicsGeometry
        {
            public PxConvexMesh* Mesh = mesh;
            public Vector3 Scale = scale;
            public Quaternion Rotation = rotation;
            public bool TightBounds = tightBounds;

            public readonly PxConvexMeshGeometry GetGeometry()
            {
                PxVec3 scale = Scale;
                PxQuat rotation = Rotation;
                PxMeshScale scaleRot = PxMeshScale_new_3(&scale, &rotation);
                PxConvexMeshGeometryFlags flags = TightBounds ? PxConvexMeshGeometryFlags.TightBounds : 0;
                return PxConvexMeshGeometry_new(Mesh, &scaleRot, flags);
            }
            public readonly DataSource GetPhysxStruct() => DataSource.FromStruct(GetGeometry());
            public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new ConvexMeshShape(Mesh, Scale, Rotation, TightBounds);
        }
        public unsafe struct TriangleMesh(PxTriangleMesh* mesh, Vector3 scale, Quaternion rotation, bool tightBounds, bool doubleSided) : IPhysicsGeometry
        {
            public PxTriangleMesh* Mesh = mesh;
            public Vector3 Scale = scale;
            public Quaternion Rotation = rotation;
            public bool TightBounds = tightBounds;
            public bool DoubleSided = doubleSided;

            public readonly PxTriangleMeshGeometry GetGeometry()
            {
                PxVec3 scale = Scale;
                PxQuat rotation = Rotation;
                PxMeshScale scaleRot = PxMeshScale_new_3(&scale, &rotation);
                PxMeshGeometryFlags flags = (TightBounds ? PxMeshGeometryFlags.TightBounds : 0) | (DoubleSided ? PxMeshGeometryFlags.DoubleSided : 0);
                return PxTriangleMeshGeometry_new(Mesh, &scaleRot, flags);
            }
            public readonly DataSource GetPhysxStruct() => DataSource.FromStruct(GetGeometry());
            public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new TriangleMeshShape(Mesh, Scale, Rotation, TightBounds, DoubleSided);
        }
        public unsafe struct HeightField(PxHeightField* field, float heightScale, float rowScale, float columnScale, bool tightBounds, bool doubleSided) : IPhysicsGeometry
        {
            public PxHeightField* Field = field;
            public float HeightScale = heightScale;
            public float RowScale = rowScale;
            public float ColumnScale = columnScale;
            public bool TightBounds = tightBounds;
            public bool DoubleSided = doubleSided;

            public readonly PxHeightFieldGeometry GetGeometry()
            {
                PxMeshGeometryFlags flags = (TightBounds ? PxMeshGeometryFlags.TightBounds : 0) | (DoubleSided ? PxMeshGeometryFlags.DoubleSided : 0);
                return PxHeightFieldGeometry_new(Field, flags, HeightScale, RowScale, ColumnScale);
            }
            public readonly DataSource GetPhysxStruct() => DataSource.FromStruct(GetGeometry());
            public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new HeightFieldShape(Field, HeightScale, RowScale, ColumnScale, TightBounds, DoubleSided);
        }
        public unsafe struct Plane() : IPhysicsGeometry
        {
            public System.Numerics.Plane PlaneData = new System.Numerics.Plane(Globals.Up, 0.0f);
            
            public readonly PxPlaneGeometry GetGeometry() => PxPlaneGeometry_new();
            public readonly DataSource GetPhysxStruct() => DataSource.FromStruct(GetGeometry());
            public readonly Shape AsJoltShape() => new PlaneShape(PlaneData);
        }
        public unsafe struct ParticleSystem(PxParticleSolverType solver) : IPhysicsGeometry
        {
            public PxParticleSolverType Solver = solver;

            public readonly PxParticleSystemGeometry GetGeometry()
            {
                var g = PxParticleSystemGeometry_new();
                g.mSolverType = Solver;
                return g;
            }
            public readonly DataSource GetPhysxStruct() => DataSource.FromStruct(GetGeometry());
            public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new ParticleSystemShape(Solver);
        }
        public unsafe struct TetrahedronMesh(PxTetrahedronMesh* mesh) : IPhysicsGeometry
        {
            public PxTetrahedronMesh* Mesh = mesh;

            public readonly PxTetrahedronMeshGeometry GetGeometry()
                => PxTetrahedronMeshGeometry_new(Mesh);

            public readonly DataSource GetPhysxStruct() => DataSource.FromStruct(GetGeometry());
            public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new TetrahedronMeshShape(Mesh);
        }

        #region PhysX

        public PxGeometry* PhysxGeometry
            => (PxGeometry*)GetPhysxStruct().Address;

        public PxGeometryType PhysxType
            => PxGeometry_getType(PhysxGeometry);

        public PxGeometryHolder NewHolder()
            => PhysxGeometry->HolderNew1();

        public bool PhysxQueryIsValid()
            => PhysxGeometry->QueryIsValid();

        public bool PhysxQueryOverlap(
            (Vector3 position, Quaternion rotation) pose,
            IPhysicsGeometry otherGeom,
            (Vector3 position, Quaternion rotation) pose1,
            PxGeometryQueryFlags queryFlags,
            PxQueryThreadContext* threadContext)
        {
            PxTransform p0 = PhysxScene.MakeTransform(pose.position, pose.rotation);
            PxTransform p1 = PhysxScene.MakeTransform(pose1.position, pose1.rotation);
            return PxGeometryQuery_overlap(PhysxGeometry, &p0, otherGeom.PhysxGeometry, &p1, queryFlags, threadContext);
        }

        public (Matrix4x4 inertiaTensor, Vector3 centerOfMass, float mass) MassProperties
        {
            get
            {
                PxMassProperties props = PhysxGeometry->MassPropertiesNew2();
                var it = props.inertiaTensor;
                Vector3 col0 = it.column0;
                Vector3 col1 = it.column1;
                Vector3 col2 = it.column2;
                Matrix4x4 itMtx = new(
                    col0.X, col1.X, col2.X, 0,
                    col0.Y, col1.Y, col2.Y, 0,
                    col0.Z, col1.Z, col2.Z, 0,
                    0, 0, 0, 1);
                return (itMtx, props.centerOfMass, props.mass);
            }
        }

        public PxPoissonSampler* PoissonSamplerNew(
            (Vector3 position, Quaternion rotation) transform,
            AABB worldBounds,
            float initialSamplingRadius,
            int numSampleAttemptsAroundPoint)
        {
            PxTransform tfm = PhysxScene.MakeTransform(transform.position, transform.rotation);
            PxVec3 min = PxVec3_new_3(worldBounds.Min.X, worldBounds.Min.Y, worldBounds.Min.Z);
            PxVec3 max = PxVec3_new_3(worldBounds.Max.X, worldBounds.Max.Y, worldBounds.Max.Z);
            PxBounds3 bounds = PxBounds3_new_1(&min, &max);
            return PhysxGeometry->PhysPxCreateShapeSampler(&tfm, &bounds, initialSamplingRadius, numSampleAttemptsAroundPoint);
        }

        public uint MeshQueryFindOverlapTriangleMesh(
            (Vector3 position, Quaternion rotation) pose,
            PxTriangleMeshGeometry* meshGeom,
            (Vector3 position, Quaternion rotation) meshPose,
            uint* results,
            uint maxResults,
            uint startIndex,
            bool* overflow,
            PxGeometryQueryFlags queryFlags)
        {
            PxTransform tfm = PhysxScene.MakeTransform(pose.position, pose.rotation);
            PxTransform meshTfm = PhysxScene.MakeTransform(meshPose.position, meshPose.rotation);
            return PhysxGeometry->MeshQueryFindOverlapTriangleMesh(&tfm, meshGeom, &meshTfm, results, maxResults, startIndex, overflow, queryFlags);
        }

        public uint MeshQueryFindOverlapHeightField(
            (Vector3 position, Quaternion rotation) pose,
            PxHeightFieldGeometry* heightField,
            (Vector3 position, Quaternion rotation) heightFieldPose,
            uint* results,
            uint maxResults,
            uint startIndex,
            bool* overflow,
            PxGeometryQueryFlags queryFlags)
        {
            PxTransform p = PhysxScene.MakeTransform(pose.position, pose.rotation);
            PxTransform hfPose = PhysxScene.MakeTransform(heightFieldPose.position, heightFieldPose.rotation);
            return PhysxGeometry->MeshQueryFindOverlapHeightField(&p, heightField, &hfPose, results, maxResults, startIndex, overflow, queryFlags);
        }

        #endregion
    }
}