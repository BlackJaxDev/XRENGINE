using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// GPU-atomic counter and diagnostic telemetry buffer for classification passes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct AdvancedClassificationGpuCounters
{
    public uint ActiveTileCount;
    /// <summary>
    /// Kernel tile memberships of the frame: the sum of the per-kernel counts, which the
    /// indirect-argument pass writes after each view (the counts accumulate over the views).
    /// </summary>
    public uint KernelTileCount;
    /// <summary>
    /// Classified pixels of the frame. Counted only while the diagnostic counter readback is
    /// active (instrumented submission strategies); otherwise 0.
    /// </summary>
    public uint ClassifiedPixelCount;
    /// <summary>
    /// Background pixels of the frame. Counted only while the diagnostic counter readback is
    /// active (instrumented submission strategies); otherwise 0.
    /// </summary>
    public uint BackgroundPixelCount;
    public uint OverflowFlags;
    public uint DroppedPixelCount;
    public uint Reserved0;
    public uint Reserved1;

    public const uint OverflowActiveTiles = 1u << 0;
    public const uint OverflowKernelTiles = 1u << 1;
    public const uint OverflowCompactPixels = 1u << 2;
    public const uint OverflowDispatchArgs = 1u << 3;

    public readonly bool HasOverflow => OverflowFlags != 0;
}
