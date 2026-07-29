namespace XREngine.Rendering;

/// <summary>
/// Value-only description of one logical shader program and its current backend link state.
/// </summary>
public readonly record struct ShaderProgramLinkDiagnostic(
    string LogicalProgramName,
    string ProgramName,
    string ProgramUse,
    string ShaderSourceSummary,
    ShaderProgramLinkDiagnosticsSnapshot Snapshot);
