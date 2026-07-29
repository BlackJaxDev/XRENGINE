using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Global sparse-record range for one mesh-local blendshape.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public readonly record struct AdvancedBlendshapeRange(
    uint RecordOffset,
    uint RecordCount,
    uint AttributeFlags,
    uint Reserved);
