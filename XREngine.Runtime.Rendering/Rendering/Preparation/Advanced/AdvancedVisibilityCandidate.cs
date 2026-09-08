using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// GPU-readable early visibility candidate keyed by a stable draw handle.
/// Explicit offsets preserve the std430 padding before the first vec4; setting
/// only the total size does not align System.Numerics.Vector4 fields to 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public readonly record struct AdvancedVisibilityCandidate(
    [field: FieldOffset(0)] AdvancedGpuHandle Draw,
    [field: FieldOffset(16)] Vector4 BoundsSphere,
    [field: FieldOffset(32)] Vector4 BoundsMin,
    [field: FieldOffset(48)] Vector4 BoundsMax,
    [field: FieldOffset(64)] ulong ViewMask,
    [field: FieldOffset(72)] uint BvhLeaf,
    [field: FieldOffset(76)] EAdvancedVisibilityPreparationFlags Flags);
