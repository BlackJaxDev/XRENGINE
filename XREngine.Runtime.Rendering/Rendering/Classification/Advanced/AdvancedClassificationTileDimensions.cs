using System;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Tile dimension policies and grid coordinate calculations for GPU material work classification.
/// </summary>
public static class AdvancedClassificationTileDimensions
{
    /// <summary>
    /// Default classification tile width in pixels (16x16 = 256 threads per compute workgroup).
    /// </summary>
    public const uint DefaultTileWidth = 16u;

    /// <summary>
    /// Default classification tile height in pixels.
    /// </summary>
    public const uint DefaultTileHeight = 16u;

    /// <summary>
    /// Alternative fine tile dimension (8x8 = 64 threads) for high divergence or VR foveation.
    /// </summary>
    public const uint FineTileWidth = 8u;
    public const uint FineTileHeight = 8u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateTilesX(uint width, uint tileWidth = DefaultTileWidth)
        => (width + tileWidth - 1u) / tileWidth;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateTilesY(uint height, uint tileHeight = DefaultTileHeight)
        => (height + tileHeight - 1u) / tileHeight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateTotalTiles(uint width, uint height, uint viewCount = 1u, uint tileWidth = DefaultTileWidth, uint tileHeight = DefaultTileHeight)
        => CalculateTilesX(width, tileWidth) * CalculateTilesY(height, tileHeight) * Math.Max(1u, viewCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint PackTileCoord(uint tileX, uint tileY)
        => (tileX & 0xFFFFu) | ((tileY & 0xFFFFu) << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (uint TileX, uint TileY) UnpackTileCoord(uint packed)
        => (packed & 0xFFFFu, (packed >> 16) & 0xFFFFu);
}
