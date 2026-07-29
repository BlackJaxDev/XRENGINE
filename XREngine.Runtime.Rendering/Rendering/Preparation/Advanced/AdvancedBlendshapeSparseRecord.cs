using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Sparse per-vertex blendshape record. Delta index zero is the shared zero
/// vector, so absent normal or tangent deltas require no special allocation.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public readonly record struct AdvancedBlendshapeSparseRecord(
    uint VertexIndex,
    uint PositionDelta,
    uint NormalDelta,
    uint TangentDelta);
