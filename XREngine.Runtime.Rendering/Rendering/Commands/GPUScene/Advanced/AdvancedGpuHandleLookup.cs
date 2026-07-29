using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Shader-visible logical-handle indirection row. Stable handles index this
/// table; the generation rejects stale references and the dense index locates
/// the current physical record after compaction.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedGpuHandleLookup(
    uint Generation,
    uint DenseIndex)
{
    public static AdvancedGpuHandleLookup Invalid
        => new(0u, AdvancedGpuHandleRemap.InvalidDenseIndex);

    public bool IsResident
        => Generation != 0u &&
           DenseIndex != AdvancedGpuHandleRemap.InvalidDenseIndex;
}
