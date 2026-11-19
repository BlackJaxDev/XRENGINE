namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Identifies a worker or import failure class.
/// </summary>
public enum UnityDiagnosticCode
{
    /// <summary>The failure has no more specific code.</summary>
    General,
    /// <summary>A required member has no executable implementation.</summary>
    UnsupportedOperation,
    /// <summary>Compilation failed.</summary>
    CompilationFailed,
    /// <summary>Execution failed.</summary>
    ExecutionFailed,
    /// <summary>Export failed.</summary>
    ExportFailed,
    /// <summary>Worker output failed parent validation.</summary>
    InvalidOutput,
    /// <summary>Native conversion or publication failed.</summary>
    ImportFailed
}
