using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>Immutable submission/control row retained with a canonical scene publication.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedDrawSubmissionRecord
{
    public AdvancedGpuHandle Draw;
    public AdvancedGpuHandle Geometry;
    public AdvancedGpuHandle Material;
    public AdvancedGpuHandle Deformation;
    public uint StableQueryKey;
    public uint LegacyCommandIndex;
    public uint PrimitiveIndex;
    public uint PassIndex;
    public uint InstanceCount;
    public uint Flags;
    public uint StateClass;
    public EAdvancedCanonicalCompatibilityReason CompatibilityReason;
    /// <summary>
    /// Frame-local temporal discontinuity observed while publishing this
    /// submission. This control-row field never changes the canonical draw ABI.
    /// </summary>
    public EAdvancedVelocityValidityReason TemporalEventReason;
    public ulong SourceOrder;
    public ulong DependencySignature;
}
