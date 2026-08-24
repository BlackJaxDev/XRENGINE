namespace XREngine.Rendering.Commands;

/// <summary>
/// Bounded backend diagnostic readback request.
/// </summary>
public readonly record struct BackendReadyDiagnosticReadbackRequest(
    EBackendReadyDiagnosticReadbackKind Kind,
    uint ViewId,
    int PassIndex,
    uint MaximumByteCount);
