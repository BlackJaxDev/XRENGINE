using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Delayed GPU telemetry. The fixed 64-byte row is frame-slot replicated.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct AdvancedVisibilityGpuCounters
{
    public uint EarlyDraws;
    public uint DeferredCandidates;
    public uint LateDraws;
    public uint RecoveredCandidates;
    public uint ValidPixels;
    public uint InvalidPixels;
    public uint PayloadOverflow;
    public uint MaskedCoveragePixels;
    public uint DecodeOutOfBounds;
    public uint DepthPyramidBuilds;
    public uint UnsupportedDisplacement;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;
}
