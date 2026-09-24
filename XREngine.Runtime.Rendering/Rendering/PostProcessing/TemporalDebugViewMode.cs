namespace XREngine.Rendering.PostProcessing;

/// <summary>Visualizes temporal reconstruction inputs and history decisions.</summary>
public enum TemporalDebugViewMode
{
    Disabled = 0,
    HistoryWeight = 1,
    Velocity = 2,
    GeometryInstability = 3,
    ReactiveMask = 4,
    HistoryAcceptance = 5,
    /// <summary>Advanced TSR surface rejection and partial history support.</summary>
    TsrSurfaceRejection = 6,
    /// <summary>Advanced TSR current and accumulated thin-geometry coverage.</summary>
    TsrThinFeatureCoverage = 7,
    /// <summary>Advanced TSR recurring luminance changes and thin-feature retention.</summary>
    TsrFlicker = 8,
    /// <summary>Advanced TSR amount of history color removed by neighborhood clipping.</summary>
    TsrHistoryClipping = 9,
}
