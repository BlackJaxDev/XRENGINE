namespace XREngine.Rendering.Commands;

/// <summary>
/// Canonical pass state projected independently from visible mesh selections.
/// </summary>
public readonly record struct BackendReadyCanonicalPassRecord(
    int PassIndex,
    ulong PassGeneration,
    ulong DependencySignature,
    ulong MembershipSignature,
    BackendReadySubmissionResolution SubmissionResolution,
    EBackendReadyPassDiagnosticFlags Diagnostics);
