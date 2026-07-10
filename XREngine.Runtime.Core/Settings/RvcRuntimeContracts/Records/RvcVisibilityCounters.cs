namespace XREngine;

public readonly record struct RvcVisibilityCounters(
    ulong VisiblePixels,
    ulong CulledCandidates,
    ulong UncertainCandidates,
    ulong PostValidationCandidates,
    ulong PageRequests,
    ulong HardwareRasterCandidates,
    ulong MeshletCandidates,
    ulong SoftwareRasterCandidates)
{
    public static RvcVisibilityCounters Empty => default;
}
