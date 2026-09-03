using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// GPU-written record for one active screen tile that contains at least one visible surface pixel.
/// 16-byte packed layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct AdvancedActiveTileRecord
{
    /// <summary>
    /// Packed tile coordinates (low 16 bits: TileX, high 16 bits: TileY).
    /// </summary>
    public uint PackedTileCoord;

    /// <summary>
    /// View/eye layer index (0 for mono desktop, 0/1 for stereo VR).
    /// </summary>
    public uint ViewIndex;

    /// <summary>
    /// Number of active visible pixels in this tile (1..256 for 16x16 tiles).
    /// </summary>
    public uint ActivePixelCount;

    /// <summary>
    /// Dominant shading kernel ID for fast-path mono-kernel tile shading.
    /// </summary>
    public uint PrimaryKernelId;

    public AdvancedActiveTileRecord(uint tileX, uint tileY, uint viewIndex, uint activePixelCount, uint primaryKernelId)
    {
        PackedTileCoord = AdvancedClassificationTileDimensions.PackTileCoord(tileX, tileY);
        ViewIndex = viewIndex;
        ActivePixelCount = activePixelCount;
        PrimaryKernelId = primaryKernelId;
    }

    public readonly (uint TileX, uint TileY) TileCoord
        => AdvancedClassificationTileDimensions.UnpackTileCoord(PackedTileCoord);
}
