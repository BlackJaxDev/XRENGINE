using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// One influence beyond the canonical four-inline-influence row.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public readonly record struct AdvancedSpillInfluence(
    uint Bone,
    float Weight);
