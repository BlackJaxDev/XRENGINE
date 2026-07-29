using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Collision-safe shared-output key. Equality compares full handles and every
/// content generation after a hash-table probe.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformationJobKey(
    AdvancedGpuHandle Mesh,
    AdvancedGpuHandle SharedPose,
    uint MeshGeneration,
    uint PoseGeneration,
    uint PaletteGeneration,
    uint TopologyGeneration,
    ulong VertexLayoutId,
    EAdvancedDeformationFeatureFlags Features,
    EAdvancedDeformationPrecision Precision);
