using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Requested and resolved mesh submission strategy for the published frame.
/// </summary>
public readonly record struct BackendReadySubmissionResolution(
    EMeshSubmissionStrategy Requested,
    EMeshSubmissionStrategy Resolved,
    bool Downgraded,
    ulong ResolutionSignature,
    RuntimeGraphicsApiKind Backend,
    EMeshShaderDialect MeshShaderDialect,
    bool SupportsMeshletDispatch);
