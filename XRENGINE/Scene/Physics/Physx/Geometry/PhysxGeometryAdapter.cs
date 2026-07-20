using XREngine.Scene.Physics.Physx;
using MagicPhysX;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Tools;
using static MagicPhysX.NativeMethods;

namespace XREngine.Rendering.Physics.Physx;

/// <summary>
/// Owns conversion from portable geometry authoring to scoped PhysX geometry data.
/// </summary>
internal static unsafe class PhysxGeometryAdapter
{
    public static DataSource CreatePhysxGeometryData(this IPhysicsGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        return geometry switch
        {
            IPhysicsGeometry.Sphere sphere => DataSource.FromStruct(PxSphereGeometry_new(sphere.Radius)),
            IPhysicsGeometry.Box box => DataSource.FromStruct(
                PxBoxGeometry_new(box.HalfExtents.X, box.HalfExtents.Y, box.HalfExtents.Z)),
            IPhysicsGeometry.Capsule capsule => DataSource.FromStruct(
                PxCapsuleGeometry_new(capsule.Radius, capsule.HalfHeight)),
            IPhysicsGeometry.Plane => DataSource.FromStruct(PxPlaneGeometry_new()),
            PhysicsConvexHullGeometry convex => CreateConvexHullGeometryData(convex),
            PhysicsTriangleMeshGeometry mesh => CreateTriangleMeshGeometryData(mesh),
            PhysicsHeightFieldGeometry heightField => CreateHeightFieldGeometryData(heightField),
            PhysxConvexMeshGeometryExtension convex => DataSource.FromStruct(CreateNativeGeometry(convex)),
            PhysxTriangleMeshGeometryExtension mesh => DataSource.FromStruct(CreateNativeGeometry(mesh)),
            PhysxHeightFieldGeometryExtension heightField => DataSource.FromStruct(CreateNativeGeometry(heightField)),
            PhysxParticleSystemGeometryExtension particleSystem => DataSource.FromStruct(CreateNativeGeometry(particleSystem)),
            PhysxTetrahedronMeshGeometryExtension tetrahedronMesh => DataSource.FromStruct(
                CreateNativeGeometry(tetrahedronMesh)),
            _ => throw new NotSupportedException(
                $"Geometry type '{geometry.GetType().FullName}' has no PhysX adapter."),
        };
    }

    public static PxGeometryType GetPhysxGeometryType(this IPhysicsGeometry geometry)
    {
        using DataSource data = geometry.CreatePhysxGeometryData();
        return PxGeometry_getType(data.Address.As<PxGeometry>());
    }

    public static bool IsValidPhysxGeometry(this IPhysicsGeometry geometry)
    {
        using DataSource data = geometry.CreatePhysxGeometryData();
        return data.Address.As<PxGeometry>()->QueryIsValid();
    }

    public static PxGeometryHolder CreatePhysxGeometryHolder(this IPhysicsGeometry geometry)
    {
        using DataSource data = geometry.CreatePhysxGeometryData();
        return data.Address.As<PxGeometry>()->HolderNew1();
    }

    public static bool OverlapsPhysxGeometry(
        this IPhysicsGeometry geometry,
        (Vector3 position, Quaternion rotation) pose,
        IPhysicsGeometry otherGeometry,
        (Vector3 position, Quaternion rotation) otherPose,
        PxGeometryQueryFlags queryFlags,
        PxQueryThreadContext* threadContext)
    {
        ArgumentNullException.ThrowIfNull(otherGeometry);
        using DataSource geometryData = geometry.CreatePhysxGeometryData();
        using DataSource otherData = otherGeometry.CreatePhysxGeometryData();
        PxTransform transform = PhysxScene.MakeTransform(pose.position, pose.rotation);
        PxTransform otherTransform = PhysxScene.MakeTransform(otherPose.position, otherPose.rotation);
        return PxGeometryQuery_overlap(
            geometryData.Address.As<PxGeometry>(),
            &transform,
            otherData.Address.As<PxGeometry>(),
            &otherTransform,
            queryFlags,
            threadContext);
    }

    public static (Matrix4x4 inertiaTensor, Vector3 centerOfMass, float mass) GetPhysxMassProperties(
        this IPhysicsGeometry geometry)
    {
        using DataSource data = geometry.CreatePhysxGeometryData();
        PxMassProperties properties = data.Address.As<PxGeometry>()->MassPropertiesNew2();
        Vector3 column0 = properties.inertiaTensor.column0;
        Vector3 column1 = properties.inertiaTensor.column1;
        Vector3 column2 = properties.inertiaTensor.column2;
        Matrix4x4 inertiaTensor = new(
            column0.X, column1.X, column2.X, 0.0f,
            column0.Y, column1.Y, column2.Y, 0.0f,
            column0.Z, column1.Z, column2.Z, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);
        return (inertiaTensor, properties.centerOfMass, properties.mass);
    }

    public static PxPoissonSampler* CreatePhysxPoissonSampler(
        this IPhysicsGeometry geometry,
        (Vector3 position, Quaternion rotation) transform,
        AABB worldBounds,
        float initialSamplingRadius,
        int attemptsAroundPoint)
    {
        using DataSource data = geometry.CreatePhysxGeometryData();
        PxTransform nativeTransform = PhysxScene.MakeTransform(transform.position, transform.rotation);
        PxVec3 minimum = worldBounds.Min;
        PxVec3 maximum = worldBounds.Max;
        PxBounds3 bounds = PxBounds3_new_1(&minimum, &maximum);
        return data.Address.As<PxGeometry>()->PhysPxCreateShapeSampler(
            &nativeTransform,
            &bounds,
            initialSamplingRadius,
            attemptsAroundPoint);
    }

    public static uint FindPhysxTriangleMeshOverlaps(
        this IPhysicsGeometry geometry,
        (Vector3 position, Quaternion rotation) pose,
        PxTriangleMeshGeometry* meshGeometry,
        (Vector3 position, Quaternion rotation) meshPose,
        uint* results,
        uint maximumResults,
        uint startIndex,
        bool* overflow,
        PxGeometryQueryFlags queryFlags)
    {
        using DataSource data = geometry.CreatePhysxGeometryData();
        PxTransform transform = PhysxScene.MakeTransform(pose.position, pose.rotation);
        PxTransform meshTransform = PhysxScene.MakeTransform(meshPose.position, meshPose.rotation);
        return data.Address.As<PxGeometry>()->MeshQueryFindOverlapTriangleMesh(
            &transform,
            meshGeometry,
            &meshTransform,
            results,
            maximumResults,
            startIndex,
            overflow,
            queryFlags);
    }

    public static uint FindPhysxHeightFieldOverlaps(
        this IPhysicsGeometry geometry,
        (Vector3 position, Quaternion rotation) pose,
        PxHeightFieldGeometry* heightFieldGeometry,
        (Vector3 position, Quaternion rotation) heightFieldPose,
        uint* results,
        uint maximumResults,
        uint startIndex,
        bool* overflow,
        PxGeometryQueryFlags queryFlags)
    {
        using DataSource data = geometry.CreatePhysxGeometryData();
        PxTransform transform = PhysxScene.MakeTransform(pose.position, pose.rotation);
        PxTransform heightFieldTransform = PhysxScene.MakeTransform(
            heightFieldPose.position,
            heightFieldPose.rotation);
        return data.Address.As<PxGeometry>()->MeshQueryFindOverlapHeightField(
            &transform,
            heightFieldGeometry,
            &heightFieldTransform,
            results,
            maximumResults,
            startIndex,
            overflow,
            queryFlags);
    }

    private static DataSource CreateConvexHullGeometryData(PhysicsConvexHullGeometry geometry)
    {
        geometry.Validate();
        int[] indices = new int[geometry.Indices.Length];
        for (int index = 0; index < indices.Length; index++)
            indices[index] = checked((int)geometry.Indices[index]);

        PhysxConvexMesh mesh = PhysxConvexHullCooker.CookHull(
            new CoACD.ConvexHullMesh(geometry.Vertices, indices),
            requestGpuData: false);
        PxConvexMeshGeometry nativeGeometry = mesh.NewGeometry(
            geometry.Scale,
            geometry.ScaleRotation,
            geometry.TightBounds);
        return new OwnedPhysxGeometryDataSource<PxConvexMeshGeometry>(nativeGeometry, mesh.Release);
    }

    private static DataSource CreateTriangleMeshGeometryData(PhysicsTriangleMeshGeometry geometry)
    {
        geometry.Validate();
        PxPhysics* physics = PhysxScene.PhysicsPtr;
        if (physics is null)
            throw new InvalidOperationException("PhysX must be initialized before cooking a triangle collider.");

        PxVec3[] points = new PxVec3[geometry.Vertices.Length];
        for (int index = 0; index < points.Length; index++)
            points[index] = geometry.Vertices[index];

        PxTriangleMeshDesc descriptor = PxTriangleMeshDesc_new();
        descriptor.points.count = (uint)points.Length;
        descriptor.points.stride = (uint)sizeof(PxVec3);
        descriptor.triangles.count = (uint)(geometry.Indices.Length / 3);
        descriptor.triangles.stride = 3u * sizeof(uint);

        PxCookingParams cooking = PxCookingParams_new(physics->GetTolerancesScale());
        cooking.meshPreprocessParams = PxMeshPreprocessingFlags.WeldVertices | PxMeshPreprocessingFlags.EnableInertia;
        cooking.suppressTriangleMeshRemapTable = false;
        PxTriangleMeshCookingResult result = PxTriangleMeshCookingResult.Success;
        PxTriangleMesh* mesh;
        fixed (PxVec3* pointPointer = points)
        fixed (uint* indexPointer = geometry.Indices)
        {
            descriptor.points.data = pointPointer;
            descriptor.triangles.data = indexPointer;
            if (!PxTriangleMeshDesc_isValid(&descriptor))
                throw new InvalidOperationException("PhysX rejected the authored triangle-mesh descriptor.");

            mesh = phys_PxCreateTriangleMesh(
                &cooking,
                &descriptor,
                physics->GetPhysicsInsertionCallbackMut(),
                &result);
        }

        if (mesh is null)
            throw new InvalidOperationException($"PhysX triangle-mesh cooking failed with result {result}.");

        PxVec3 scale = geometry.Scale;
        PxQuat rotation = geometry.ScaleRotation;
        PxMeshScale meshScale = PxMeshScale_new_3(&scale, &rotation);
        PxMeshGeometryFlags flags = (geometry.TightBounds ? PxMeshGeometryFlags.TightBounds : 0)
            | (geometry.DoubleSided ? PxMeshGeometryFlags.DoubleSided : 0);
        PxTriangleMeshGeometry nativeGeometry = PxTriangleMeshGeometry_new(mesh, &meshScale, flags);
        nint meshAddress = (nint)mesh;
        return new OwnedPhysxGeometryDataSource<PxTriangleMeshGeometry>(
            nativeGeometry,
            () => ((PxTriangleMesh*)meshAddress)->ReleaseMut());
    }

    private static DataSource CreateHeightFieldGeometryData(PhysicsHeightFieldGeometry geometry)
    {
        geometry.Validate();
        PxPhysics* physics = PhysxScene.PhysicsPtr;
        if (physics is null)
            throw new InvalidOperationException("PhysX must be initialized before cooking a height-field collider.");

        PxHeightFieldSample[] nativeSamples = new PxHeightFieldSample[geometry.Samples.Length];
        for (int row = 0; row < geometry.RowCount; row++)
        {
            for (int column = 0; column < geometry.ColumnCount; column++)
            {
                int sampleIndex = row * geometry.ColumnCount + column;
                nativeSamples[sampleIndex].height = geometry.Samples[sampleIndex];
                if (row == geometry.RowCount - 1 || column == geometry.ColumnCount - 1)
                    continue;

                PhysicsHeightFieldCell cell = geometry.Cells[row * (geometry.ColumnCount - 1) + column];
                fixed (PxHeightFieldSample* sample = &nativeSamples[sampleIndex])
                {
                    *(byte*)&sample->materialIndex0 = cell.LowerTriangleHole ? (byte)127 : (byte)0;
                    *(byte*)&sample->materialIndex1 = cell.UpperTriangleHole ? (byte)127 : (byte)0;
                    if (cell.TessellatedDiagonal)
                        PxHeightFieldSample_setTessFlag_mut(sample);
                    else
                        PxHeightFieldSample_clearTessFlag_mut(sample);
                }
            }
        }

        PxHeightFieldDesc descriptor = PxHeightFieldDesc_new();
        descriptor.nbRows = (uint)geometry.RowCount;
        descriptor.nbColumns = (uint)geometry.ColumnCount;
        descriptor.format = PxHeightFieldFormat.S16Tm;
        descriptor.samples.stride = (uint)sizeof(PxHeightFieldSample);
        PxHeightField* heightField;
        fixed (PxHeightFieldSample* samplePointer = nativeSamples)
        {
            descriptor.samples.data = samplePointer;
            if (!PxHeightFieldDesc_isValid(&descriptor))
                throw new InvalidOperationException("PhysX rejected the authored height-field descriptor.");
            heightField = phys_PxCreateHeightField(&descriptor, physics->GetPhysicsInsertionCallbackMut());
        }

        if (heightField is null)
            throw new InvalidOperationException("PhysX height-field cooking failed.");

        PxMeshGeometryFlags flags = (geometry.TightBounds ? PxMeshGeometryFlags.TightBounds : 0)
            | (geometry.DoubleSided ? PxMeshGeometryFlags.DoubleSided : 0);
        PxHeightFieldGeometry nativeGeometry = PxHeightFieldGeometry_new(
            heightField,
            flags,
            geometry.HeightScale,
            geometry.RowScale,
            geometry.ColumnScale);
        nint heightFieldAddress = (nint)heightField;
        return new OwnedPhysxGeometryDataSource<PxHeightFieldGeometry>(
            nativeGeometry,
            () => ((PxHeightField*)heightFieldAddress)->ReleaseMut());
    }

    private static PxConvexMeshGeometry CreateNativeGeometry(PhysxConvexMeshGeometryExtension geometry)
    {
        if (geometry.Mesh is null)
            throw new InvalidOperationException("A PhysX convex-mesh extension requires a valid cooked mesh pointer.");

        PxVec3 scale = geometry.Scale;
        PxQuat rotation = geometry.ScaleRotation;
        PxMeshScale meshScale = PxMeshScale_new_3(&scale, &rotation);
        PxConvexMeshGeometryFlags flags = geometry.TightBounds ? PxConvexMeshGeometryFlags.TightBounds : 0;
        return PxConvexMeshGeometry_new(geometry.Mesh, &meshScale, flags);
    }

    private static PxTriangleMeshGeometry CreateNativeGeometry(PhysxTriangleMeshGeometryExtension geometry)
    {
        if (geometry.Mesh is null)
            throw new InvalidOperationException("A PhysX triangle-mesh extension requires a valid cooked mesh pointer.");

        PxVec3 scale = geometry.Scale;
        PxQuat rotation = geometry.ScaleRotation;
        PxMeshScale meshScale = PxMeshScale_new_3(&scale, &rotation);
        PxMeshGeometryFlags flags = (geometry.TightBounds ? PxMeshGeometryFlags.TightBounds : 0)
            | (geometry.DoubleSided ? PxMeshGeometryFlags.DoubleSided : 0);
        return PxTriangleMeshGeometry_new(geometry.Mesh, &meshScale, flags);
    }

    private static PxHeightFieldGeometry CreateNativeGeometry(PhysxHeightFieldGeometryExtension geometry)
    {
        if (geometry.HeightField is null)
            throw new InvalidOperationException("A PhysX height-field extension requires a valid cooked height-field pointer.");

        PxMeshGeometryFlags flags = (geometry.TightBounds ? PxMeshGeometryFlags.TightBounds : 0)
            | (geometry.DoubleSided ? PxMeshGeometryFlags.DoubleSided : 0);
        return PxHeightFieldGeometry_new(
            geometry.HeightField,
            flags,
            geometry.HeightScale,
            geometry.RowScale,
            geometry.ColumnScale);
    }

    private static PxParticleSystemGeometry CreateNativeGeometry(PhysxParticleSystemGeometryExtension geometry)
    {
        PxParticleSystemGeometry nativeGeometry = PxParticleSystemGeometry_new();
        nativeGeometry.mSolverType = geometry.Solver;
        return nativeGeometry;
    }

    private static PxTetrahedronMeshGeometry CreateNativeGeometry(
        PhysxTetrahedronMeshGeometryExtension geometry)
    {
        if (geometry.Mesh is null)
            throw new InvalidOperationException("A PhysX tetrahedron-mesh extension requires a valid cooked mesh pointer.");
        return PxTetrahedronMeshGeometry_new(geometry.Mesh);
    }
}

internal sealed class OwnedPhysxGeometryDataSource<T> : DataSource
    where T : unmanaged
{
    private Action? _release;

    public unsafe OwnedPhysxGeometryDataSource(T geometry, Action release)
        : base((uint)sizeof(T))
    {
        ArgumentNullException.ThrowIfNull(release);
        Unsafe.WriteUnaligned((void*)Address, geometry);
        _release = release;
    }

    protected override void Dispose(bool disposing)
    {
        Action? release = Interlocked.Exchange(ref _release, null);
        try
        {
            release?.Invoke();
        }
        finally
        {
            base.Dispose(disposing);
        }
    }
}
