using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// One declared semantic in a packed material layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedMaterialLayoutMember(
    ulong SemanticHash,
    EAdvancedMaterialValueKind Kind,
    uint ElementOffset,
    uint ElementCount,
    uint Flags = 0u,
    uint Reserved0 = 0u,
    uint Reserved1 = 0u);
