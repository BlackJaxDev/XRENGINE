using MagicPhysX;
using System.Numerics;
using XREngine.Scene.Physics;

namespace XREngine.Scene.Physics.Physx;

/// <summary>
/// Explicit PhysX-only view of an already-cooked convex mesh.
/// Prefer <see cref="PhysicsConvexHullGeometry"/> for portable authored content.
/// </summary>
public readonly unsafe struct PhysxConvexMeshGeometryExtension(
    PxConvexMesh* mesh,
    Vector3 scale,
    Quaternion scaleRotation,
    bool tightBounds) : IPhysicsGeometry
{
    public PxConvexMesh* Mesh { get; } = mesh;
    public Vector3 Scale { get; } = scale;
    public Quaternion ScaleRotation { get; } = scaleRotation;
    public bool TightBounds { get; } = tightBounds;
}

/// <summary>
/// Explicit PhysX-only view of an already-cooked triangle mesh.
/// Prefer <see cref="PhysicsTriangleMeshGeometry"/> for portable authored content.
/// </summary>
public readonly unsafe struct PhysxTriangleMeshGeometryExtension(
    PxTriangleMesh* mesh,
    Vector3 scale,
    Quaternion scaleRotation,
    bool tightBounds,
    bool doubleSided) : IPhysicsGeometry
{
    public PxTriangleMesh* Mesh { get; } = mesh;
    public Vector3 Scale { get; } = scale;
    public Quaternion ScaleRotation { get; } = scaleRotation;
    public bool TightBounds { get; } = tightBounds;
    public bool DoubleSided { get; } = doubleSided;
}

/// <summary>
/// Explicit PhysX-only view of an already-cooked height field.
/// Prefer <see cref="PhysicsHeightFieldGeometry"/> for portable authored content.
/// </summary>
public readonly unsafe struct PhysxHeightFieldGeometryExtension(
    PxHeightField* heightField,
    float heightScale,
    float rowScale,
    float columnScale,
    bool tightBounds,
    bool doubleSided) : IPhysicsGeometry
{
    public PxHeightField* HeightField { get; } = heightField;
    public float HeightScale { get; } = heightScale;
    public float RowScale { get; } = rowScale;
    public float ColumnScale { get; } = columnScale;
    public bool TightBounds { get; } = tightBounds;
    public bool DoubleSided { get; } = doubleSided;
}

/// <summary>
/// Explicit PhysX-only particle-system geometry.
/// </summary>
public readonly struct PhysxParticleSystemGeometryExtension(PxParticleSolverType solver) : IPhysicsGeometry
{
    public PxParticleSolverType Solver { get; } = solver;
}

/// <summary>
/// Explicit PhysX-only view of an already-cooked tetrahedron mesh.
/// </summary>
public readonly unsafe struct PhysxTetrahedronMeshGeometryExtension(PxTetrahedronMesh* mesh) : IPhysicsGeometry
{
    public PxTetrahedronMesh* Mesh { get; } = mesh;
}
