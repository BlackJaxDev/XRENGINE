using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Compact active shape index and weight copied once per shared render pose.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public readonly record struct AdvancedActiveBlendshape(
    uint ShapeIndex,
    float Weight);
