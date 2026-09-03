using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// GPU-written record linking a specific tile to a specific shading kernel execution.
/// 16-byte packed layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct AdvancedKernelTileRecord
{
    /// <summary>
    /// Index into the ActiveTiles buffer.
    /// </summary>
    public uint ActiveTileIndex;

    /// <summary>
    /// The target shading kernel ID to execute for this tile.
    /// </summary>
    public uint ShadingKernelId;

    /// <summary>
    /// Number of pixels in this tile belonging to this shading kernel.
    /// </summary>
    public uint PixelCount;

    /// <summary>
    /// Classification flags (e.g., full-tile uniform kernel vs. multi-kernel mixed tile).
    /// </summary>
    public uint Flags;

    public const uint FlagFullTile = 1u << 0;
    public const uint FlagMixedKernels = 1u << 1;
    public const uint FlagHasDerivatives = 1u << 2;
    public const uint FlagConservativeFallback = 1u << 3;

    public AdvancedKernelTileRecord(uint activeTileIndex, uint shadingKernelId, uint pixelCount, uint flags)
    {
        ActiveTileIndex = activeTileIndex;
        ShadingKernelId = shadingKernelId;
        PixelCount = pixelCount;
        Flags = flags;
    }
}
