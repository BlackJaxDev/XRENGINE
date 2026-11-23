namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The counters of the <see cref="MeshletPageStreamingManager"/>. The "last update" values cover
/// the feedback and the work of the most recent <see cref="MeshletPageStreamingManager.Update"/>,
/// and the totals cover the lifetime of the manager. The Advanced publisher writes these values
/// to a <c>[GeometryStreaming]</c> rendering log line every two seconds.
/// </summary>
public sealed class MeshletPageStreamingTelemetry
{
    private readonly int[] _lastRequestsByBand = new int[3];

    /// <summary>The registered pages that have a pool slot now.</summary>
    public int ResidentPages { get; internal set; }

    /// <summary>The bytes of the page images of the resident pages.</summary>
    public long ResidentBytes { get; internal set; }

    /// <summary>The pages that failed and that the manager does not request again.</summary>
    public int FailedPages { get; internal set; }

    /// <summary>The distinct pages that the feedback of the last update requested, before the dependency chains were added.</summary>
    public int LastRequestedPages { get; internal set; }

    /// <summary>The requests that did not fit in the GPU request buffer, in the feedback of the last update.</summary>
    public long LastRequestOverflows { get; internal set; }

    /// <summary>The pages that the last update loaded.</summary>
    public int LastLoads { get; internal set; }

    /// <summary>The bytes of page images that the last update loaded.</summary>
    public long LastLoadedBytes { get; internal set; }

    /// <summary>The pages that the last update evicted.</summary>
    public int LastEvictions { get; internal set; }

    /// <summary>The pages that the last update evicted under the foreground floor (inside the normal cooldown).</summary>
    public int LastForegroundEvictions { get; internal set; }

    /// <summary>The loads of the last update that waited, because no slot was free.</summary>
    public int LastLoadsWithoutSlot { get; internal set; }

    /// <summary>The pages loaded since the manager was created.</summary>
    public long TotalLoads { get; internal set; }

    /// <summary>The pages evicted since the manager was created.</summary>
    public long TotalEvictions { get; internal set; }

    /// <summary>The request overflows since the manager was created.</summary>
    public long TotalRequestOverflows { get; internal set; }

    /// <summary>The geometries that are registered with the manager now.</summary>
    public int RegisteredGeometries { get; internal set; }

    /// <summary>The geometry registrations since the manager was created.</summary>
    public long TotalGeometryRegistrations { get; internal set; }

    /// <summary>The geometry removals since the manager was created.</summary>
    public long TotalGeometryRemovals { get; internal set; }

    /// <summary>
    /// The resident pages that left the pool because their geometry was removed, since the manager
    /// was created. These pages are not evictions: the publisher retired their geometry record, for
    /// example when the draw changed to another mesh or left the scene.
    /// </summary>
    public long TotalReleasedPages { get; internal set; }

    /// <summary>Returns the distinct pages of one band that the feedback of the last update requested.</summary>
    public int LastRequestsInBand(EMeshletPageRequestBand band)
        => _lastRequestsByBand[(int)band];

    internal void CountRequest(EMeshletPageRequestBand band)
        => ++_lastRequestsByBand[(int)band];

    internal void ResetFeedback()
    {
        Array.Clear(_lastRequestsByBand);
        LastRequestedPages = 0;
        LastRequestOverflows = 0;
    }

    internal void ResetUpdate()
    {
        LastLoads = 0;
        LastLoadedBytes = 0;
        LastEvictions = 0;
        LastForegroundEvictions = 0;
        LastLoadsWithoutSlot = 0;
    }
}
