using System.Numerics;

namespace XREngine.Rendering.Commands;

/// <summary>Exact command-level sorting inputs captured at its successful collection insertion.</summary>
public readonly record struct GpuMeshSubmissionOrderSource(
    IRenderCommandMesh Source,
    ulong InsertionOrder,
    Vector3 BoundsMin,
    Vector3 BoundsMax,
    Vector3 FallbackPosition,
    bool HasBounds,
    int Priority);
