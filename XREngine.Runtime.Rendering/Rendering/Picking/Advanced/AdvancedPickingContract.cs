using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Packed asynchronous picking query sent to the GPU to sample single-pixel visibility payloads.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
public readonly record struct AdvancedPickingQuery(uint CoordX, uint CoordY, uint ViewIndex);

/// <summary>
/// Operational helpers and contract for asynchronous editor picking.
/// </summary>
public static class AdvancedPickingContract
{
    public const string PickingBufferResourceName = "AdvancedEditor.PickingQueryBuffer";
    public const uint ReadbackWordCount = 4u;
    public const uint ReadbackByteCount = ReadbackWordCount * sizeof(uint);

    /// <summary>
    /// Validates whether a given pixel coordinate falls within the viewport boundary.
    /// </summary>
    public static bool IsInBounds(uint x, uint y, uint width, uint height)
        => x < width && y < height;
}
