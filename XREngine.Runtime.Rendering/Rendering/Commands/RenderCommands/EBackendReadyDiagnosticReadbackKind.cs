namespace XREngine.Rendering.Commands;

/// <summary>
/// Backend diagnostic payload requested for a published frame.
/// </summary>
public enum EBackendReadyDiagnosticReadbackKind : byte
{
    None,
    IndirectDrawCount,
    MeshletVisibility,
    SubmissionValidation,
}
