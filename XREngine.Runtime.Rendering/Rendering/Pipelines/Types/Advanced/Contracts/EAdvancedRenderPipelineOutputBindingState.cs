namespace XREngine.Rendering;

/// <summary>
/// Describes how one physical pipeline instance is bound to its configured
/// advanced pipeline source.
/// </summary>
public enum EAdvancedRenderPipelineOutputBindingState
{
    Unconfigured = 0,
    Bound,
    Disabled,
    DiagnosticOnly,
    Rejected,
}
