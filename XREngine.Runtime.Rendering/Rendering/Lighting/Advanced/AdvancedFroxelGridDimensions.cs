using System;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Clustered lighting froxel grid dimension constants and coordinate calculations.
/// </summary>
public static class AdvancedFroxelGridDimensions
{
    /// <summary>
    /// Screen-space tile width in pixels for froxel clustering (matches 16x16 classification tile).
    /// </summary>
    public const uint TileWidth = 16u;

    /// <summary>
    /// Screen-space tile height in pixels for froxel clustering.
    /// </summary>
    public const uint TileHeight = 16u;

    /// <summary>
    /// Default number of exponential depth slices along view frustum.
    /// </summary>
    public const uint DefaultDepthSlices = 24u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateTilesX(uint width, uint tileWidth = TileWidth)
        => (width + tileWidth - 1u) / tileWidth;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateTilesY(uint height, uint tileHeight = TileHeight)
        => (height + tileHeight - 1u) / tileHeight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateTotalFroxels(uint width, uint height, uint depthSlices = DefaultDepthSlices, uint viewCount = 1u)
        => CalculateTilesX(width) * CalculateTilesY(height) * Math.Max(1u, depthSlices) * Math.Max(1u, viewCount);

    /// <summary>
    /// Computes the total view-slot capacity for clustered light and decal froxel grids,
    /// combining primary eye views with any active portal sub-view budget clamped to the
    /// engine's hard ceiling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateTotalViewCapacity(uint primaryViewCount, uint portalViewBudget)
    {
        uint clampedPrimary = Math.Max(1u, primaryViewCount);
        uint clampedPortal = Math.Min(portalViewBudget, (uint)AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame);
        return checked(clampedPrimary + clampedPortal);
    }

    /// <summary>
    /// Derives how many complete froxel view slices fit inside a physical froxel storage
    /// buffer of <paramref name="bufferByteSize"/> bytes for the given tile and depth-slice layout.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateAvailableViewCapacity(
        ulong bufferByteSize,
        uint tilesX,
        uint tilesY,
        uint depthSlices = DefaultDepthSlices,
        uint recordByteStride = 16u)
    {
        if (tilesX == 0u || tilesY == 0u || depthSlices == 0u || recordByteStride == 0u)
            return 0u;
        ulong bytesPerView = checked((ulong)tilesX * tilesY * depthSlices * recordByteStride);
        return checked((uint)(bufferByteSize / bytesPerView));
    }

    /// <summary>
    /// Calculates the exponential depth slice index for a view-space depth value z (positive, in camera space).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateDepthSlice(float linearDepth, float nearPlane, float farPlane, uint depthSlices = DefaultDepthSlices)
    {
        if (linearDepth <= nearPlane)
            return 0u;
        if (linearDepth >= farPlane)
            return depthSlices - 1u;

        float ratio = MathF.Log(linearDepth / nearPlane) / MathF.Log(farPlane / nearPlane);
        int slice = (int)(ratio * depthSlices);
        return (uint)Math.Clamp(slice, 0, (int)depthSlices - 1);
    }

    /// <summary>
    /// Computes the linear 1D index into the Froxel buffer for a given 3D grid coordinate and view index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetFroxelIndex(uint tileX, uint tileY, uint sliceZ, uint viewIndex, uint tilesX, uint tilesY, uint depthSlices = DefaultDepthSlices)
    {
        uint froxelsPerView = tilesX * tilesY * depthSlices;
        return (viewIndex * froxelsPerView) + (sliceZ * tilesX * tilesY) + (tileY * tilesX) + tileX;
    }
}
