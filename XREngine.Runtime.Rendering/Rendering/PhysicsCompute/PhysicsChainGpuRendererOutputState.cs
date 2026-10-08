using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Compute;

/// <summary>Certifies a renderer's palette and authored envelope on one output page.</summary>
public readonly record struct PhysicsChainGpuRendererOutputState(
    PhysicsChainGpuBoundsSource BoundsSource,
    uint PaletteBase,
    uint PaletteCount,
    PhysicsChainMeshEnvelopeStamp Envelope,
    bool PreviousPaletteValid,
    int MorphWeightOffset,
    int MorphWeightCount)
{
    public PhysicsChainSpatialSourceIdentity SpatialSource { get; init; }
    public PhysicsChainPaletteSpatialState CurrentSpatialState { get; init; }
    public PhysicsChainPaletteSpatialState PreviousSpatialState { get; init; }
    public bool PaletteSpatialStateValid { get; init; }
    /// <summary>Committed CPU bound: an enlarged proxy that contains the exact spatial bound.</summary>
    public AABB CpuSpatialBounds { get; init; }
    public bool CpuSpatialBoundsValid { get; init; }

    /// <summary>
    /// Nonzero version of <see cref="CpuSpatialBounds"/> when it is valid. A later page keeps the
    /// version while it keeps the same proxy, so the version changes only when this renderer's
    /// committed bound changes.
    /// </summary>
    public ulong CpuSpatialBoundsVersion { get; init; }
    public float MaterialPadding { get; init; }
    public bool MaterialBoundsSupported { get; init; }
    public GPUScene? MaterialRouteScene { get; init; }
}
