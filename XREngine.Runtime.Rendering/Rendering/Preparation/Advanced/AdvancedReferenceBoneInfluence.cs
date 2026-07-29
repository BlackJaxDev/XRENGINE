using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// One diagnostic bone influence.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedReferenceBoneInfluence(
    uint BoneIndex,
    float Weight);
