namespace XREngine;

public readonly record struct RvcPixelToShadeletEncoding(
    int TileWidth,
    int TileHeight,
    int LocalIndexBits,
    string TileBaseFormat,
    string LocalIndexFormat)
{
    public static RvcPixelToShadeletEncoding Default => new(
        TileWidth: 8,
        TileHeight: 8,
        LocalIndexBits: 16,
        TileBaseFormat: "R32_UINT",
        LocalIndexFormat: "R16_UINT");
}
