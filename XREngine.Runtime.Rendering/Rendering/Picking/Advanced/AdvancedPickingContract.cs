using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Packed asynchronous picking query sent to the GPU to sample single-pixel visibility payloads.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
public readonly record struct AdvancedPickingQuery(uint CoordX, uint CoordY, uint ViewIndex);

/// <summary>
/// Resolved editor picking result decoded asynchronously from visibility identity records.
/// </summary>
public readonly record struct AdvancedPickingResult(
    uint DrawId,
    uint PrimitiveId,
    ulong InstanceId,
    uint SelectionId,
    bool IsHit)
{
    public static AdvancedPickingResult Miss => new(0u, 0u, 0UL, 0u, false);

    public static AdvancedPickingResult FromPayload(uint drawId, uint primitiveId, ulong instanceId, uint selectionId)
    {
        if (drawId == 0u)
            return Miss;

        return new AdvancedPickingResult(drawId, primitiveId, instanceId, selectionId, true);
    }
}

/// <summary>
/// Operational helpers and contract for asynchronous editor picking.
/// </summary>
public static class AdvancedPickingContract
{
    public const string PickingBufferResourceName = "AdvancedEditor.PickingQueryBuffer";

    /// <summary>
    /// Validates whether a given pixel coordinate falls within the viewport boundary.
    /// </summary>
    public static bool IsInBounds(uint x, uint y, uint width, uint height)
        => x < width && y < height;
}
