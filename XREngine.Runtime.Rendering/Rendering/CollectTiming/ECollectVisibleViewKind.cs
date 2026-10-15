namespace XREngine.Rendering;

/// <summary>
/// Identifies the kind of view that the collect thread collects at this time.
/// <see cref="CollectVisibleStageTimings"/> keeps one set of view and path times for each kind.
/// </summary>
internal enum ECollectVisibleViewKind
{
    /// <summary>A collection outside a named view, for example an XR eye, a capture or a UI collection.</summary>
    Other = 0,

    /// <summary>An automatic viewport collection from the collect event.</summary>
    MainView,

    /// <summary>The primary shadow view of a directional light.</summary>
    PrimaryShadow,

    /// <summary>The cascade shadow views of a directional light.</summary>
    Cascade,

    /// <summary>The shadow views of a spot light or a point light.</summary>
    LocalShadow,

    /// <summary>The number of view kinds. This value is not a view kind.</summary>
    Count,
}
