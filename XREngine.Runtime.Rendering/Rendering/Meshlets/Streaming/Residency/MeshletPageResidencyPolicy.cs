namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The budgets and eviction rules of the <see cref="MeshletPageStreamingManager"/>.
/// </summary>
/// <remarks>
/// A load that needs a slot evicts the resident page with the oldest last-touch frame that has no
/// resident child pages, is not a root page, and was not touched in the current frame. A prefetch,
/// occluded or equal-priority load evicts only pages that were not touched for at least
/// <see cref="EvictionCooldownFrames"/>. A foreground load can also evict pages that were not
/// touched for at least <see cref="ForegroundEvictionFloorFrames"/>, so a teleport or a fast turn
/// does not stay at root detail while every slot is inside the cooldown. A load never evicts a page
/// that was loaded for a higher band.
/// </remarks>
public sealed class MeshletPageResidencyPolicy
{
    /// <summary>The default upload budget: 4 MiB of page images for each update.</summary>
    public const long DefaultUploadBytesPerUpdate = 4L * 1024L * 1024L;

    /// <summary>
    /// The frames that a page must stay untouched before a prefetch or occluded load can evict it.
    /// </summary>
    public int EvictionCooldownFrames { get; init; } = 120;

    /// <summary>
    /// The frames that a page must stay untouched before a foreground load can evict it. Keep it at
    /// least as large as the readback latency, so the touch bits of a page that is still drawn
    /// arrive before the page becomes a candidate.
    /// </summary>
    public int ForegroundEvictionFloorFrames { get; init; } = 8;

    /// <summary>
    /// The bytes of page images that one update can load. The first load of an update is always
    /// allowed, so a page larger than the budget still loads.
    /// </summary>
    public long UploadBytesPerUpdate { get; init; } = DefaultUploadBytesPerUpdate;

    /// <summary>Returns the frames that a page must stay untouched before a load of <paramref name="loadBand"/> can evict it.</summary>
    public int MinimumEvictionAge(EMeshletPageRequestBand loadBand)
        => loadBand == EMeshletPageRequestBand.Foreground
            ? Math.Min(ForegroundEvictionFloorFrames, EvictionCooldownFrames)
            : EvictionCooldownFrames;

    /// <summary>True when a load of <paramref name="loadBand"/> can evict a page that was loaded for <paramref name="victimBand"/>.</summary>
    public static bool MayEvict(EMeshletPageRequestBand victimBand, EMeshletPageRequestBand loadBand)
        => victimBand <= loadBand;
}
