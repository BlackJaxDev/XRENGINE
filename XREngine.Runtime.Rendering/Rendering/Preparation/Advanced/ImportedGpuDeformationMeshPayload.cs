using System.Numerics;
using XREngine.Data;

namespace XREngine.Rendering;

/// <summary>
/// Managed deformation input packed while an imported mesh is exclusively owned
/// by its build worker, before the mesh is attached to a scene.
/// </summary>
internal sealed class ImportedGpuDeformationMeshPayload
{
    internal required long GeometryRevision { get; init; }
    internal required int VertexCount { get; init; }
    internal required Vertex[] SourceVertices { get; init; }
    internal required string[] Names { get; init; }
    internal required int ActiveBlendshapeCount { get; init; }
    internal required AdvancedDeformedVertex[] Vertices { get; init; }
    internal required AdvancedBlendshapeRange[] Ranges { get; init; }
    internal required AdvancedBlendshapeSparseRecord[] Records { get; init; }
    internal required Vector4[] Deltas { get; init; }
}
