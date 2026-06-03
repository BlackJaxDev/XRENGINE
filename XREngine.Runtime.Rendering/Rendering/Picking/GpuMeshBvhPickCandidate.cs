using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Scene;

namespace XREngine.Rendering.Picking;

/// <summary>
/// Pending or completed editor pick against a mesh-owned GPU BVH.
/// </summary>
public sealed class GpuMeshBvhPickCandidate(
    RenderableComponent component,
    RenderableMesh mesh,
    RenderInfo3D renderInfo,
    Segment worldSegment,
    float candidateDistance,
    ERaycastHitMode hitMode)
{
    private Action<GpuMeshBvhPickCandidate>? _completed;

    public RenderableComponent Component { get; } = component;
    public RenderableMesh Mesh { get; } = mesh;
    public RenderInfo3D RenderInfo { get; } = renderInfo;
    public Segment WorldSegment { get; } = worldSegment;
    public float CandidateDistance { get; } = candidateDistance;
    public ERaycastHitMode HitMode { get; } = hitMode;
    public bool IsComplete { get; private set; }
    public bool HasHit { get; private set; }
    public float Distance { get; private set; }
    public Vector3 HitPoint { get; private set; }
    public Vector3 Barycentric { get; private set; }
    public uint ObjectId { get; private set; }
    public uint SortedTriangleIndex { get; private set; }
    public MeshPickResult? FaceHit { get; private set; }
    /// <summary>
    /// The hit-mode-resolved pick result (face, edge, or vertex) once the GPU readback completes.
    /// Null while pending or on a miss.
    /// </summary>
    public object? PickResult { get; private set; }
    public SceneNode? SceneNode => Component.SceneNode;

    public event Action<GpuMeshBvhPickCandidate> Completed
    {
        add
        {
            if (IsComplete)
            {
                value?.Invoke(this);
                return;
            }

            _completed += value;
        }
        remove => _completed -= value;
    }

    public void CompleteMiss()
        => Complete(hasHit: false, distance: 0.0f, hitPoint: default, barycentric: default, objectId: 0u, sortedTriangleIndex: uint.MaxValue, faceHit: null, pickResult: null);

    public void CompleteHit(
        float distance,
        Vector3 hitPoint,
        Vector3 barycentric,
        uint objectId,
        uint sortedTriangleIndex,
        MeshPickResult? faceHit,
        object? pickResult)
        => Complete(hasHit: true, distance, hitPoint, barycentric, objectId, sortedTriangleIndex, faceHit, pickResult);

    private void Complete(
        bool hasHit,
        float distance,
        Vector3 hitPoint,
        Vector3 barycentric,
        uint objectId,
        uint sortedTriangleIndex,
        MeshPickResult? faceHit,
        object? pickResult)
    {
        if (IsComplete)
            return;

        HasHit = hasHit;
        Distance = distance;
        HitPoint = hitPoint;
        Barycentric = barycentric;
        ObjectId = objectId;
        SortedTriangleIndex = sortedTriangleIndex;
        FaceHit = faceHit;
        PickResult = pickResult;
        IsComplete = true;

        Action<GpuMeshBvhPickCandidate>? completed = _completed;
        _completed = null;
        completed?.Invoke(this);
    }
}