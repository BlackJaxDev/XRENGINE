using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// GPU-atomic counter and diagnostic telemetry buffer for classification passes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct AdvancedClassificationGpuCounters
{
    public uint ActiveTileCount;
    public uint KernelTileCount;
    public uint ClassifiedPixelCount;
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
