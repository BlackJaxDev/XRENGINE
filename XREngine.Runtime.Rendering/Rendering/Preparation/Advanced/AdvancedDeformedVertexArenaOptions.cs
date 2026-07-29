using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Explicit deformation-arena sizing and retirement policy.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformedVertexArenaOptions(
    uint InitialVertexCapacity,
    int FrameSlotCount,
    int OwnerCapacity,
    int RetiredGenerationCapacity)
{
    public static AdvancedDeformedVertexArenaOptions Default => new(
        InitialVertexCapacity: 262_144u,
        FrameSlotCount: 3,
        OwnerCapacity: 16_384,
        RetiredGenerationCapacity: 4);
}
