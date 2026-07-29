using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Describes one authored value submitted for layout validation without
/// retaining managed material identity.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedMaterialValueDescriptor(
    ulong SemanticHash,
    EAdvancedMaterialValueKind Kind,
    uint ElementCount);
