using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// One bounded aggregate compute dispatch and its indirection range.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformationDispatchBatch(
    AdvancedDeformationDispatchKey Key,
    uint FirstJobIndex,
    uint JobCount,
    ulong VertexCount,
    uint WorkGroupCount);
