namespace XREngine.Rendering.Commands;

/// <summary>
/// Per-pass strategy diagnostics captured with a canonical package. These are
/// compact flags rather than strings so collection remains allocation-stable.
/// </summary>
[Flags]
public enum EBackendReadyPassDiagnosticFlags : byte
{
    None = 0,
    StrategyDowngraded = 1 << 0,
    InstrumentedReadbackRequested = 1 << 1,
    MeshletDispatchUnavailable = 1 << 2,
}
