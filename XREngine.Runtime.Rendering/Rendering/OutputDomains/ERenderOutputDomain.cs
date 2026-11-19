namespace XREngine.Rendering.OutputDomains;

/// <summary>
/// Identifies the output family that owns the render work on the current thread. An output
/// domain is not a view: the two eyes of an XR frame belong to one domain and share one set
/// of per-domain state.
/// </summary>
public enum ERenderOutputDomain : byte
{
    /// <summary>The work runs outside a domain entry point, for example startup or maintenance.</summary>
    None = 0,

    /// <summary>The desktop window frame: the desktop pipeline, its overlays and its present.</summary>
    Desktop = 1,

    /// <summary>The XR frame: the eye views, single-pass or per eye, and the eye mirror preview.</summary>
    Xr = 2,

    /// <summary>The number of domain values. Not a domain.</summary>
    Count = 3,
}
