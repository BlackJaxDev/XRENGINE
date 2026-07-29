using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Explicit table and immutable-arena capacities changed only at a frame boundary.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedGpuSceneCapacityProfile(
    uint DrawRecords,
    uint InstanceRecords,
    uint TransformRecords,
    uint DeformationRecords,
    uint RenderStateRecords,
    uint EditorIdentityRecords,
    uint GeometryRecords,
    uint StaticVertexBytes,
    uint IndexBytes,
    uint PreSkinnedCurrentBytes,
    uint PreSkinnedPreviousBytes,
    uint MeshletBytes);
