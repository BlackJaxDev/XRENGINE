using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>Reserves one fixed member segment inside an indexed visibility range.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public readonly record struct AdvancedIndexedInstanceGroup(
    uint RangeIndex,
    uint FirstMember,
    uint MemberCapacity,
    uint RepresentativePayloadIndex);
