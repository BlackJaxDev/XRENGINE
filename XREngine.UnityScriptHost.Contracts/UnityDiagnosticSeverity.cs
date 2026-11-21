namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Classifies the severity of a build diagnostic.
/// </summary>
public enum UnityDiagnosticSeverity
{
    /// <summary>The diagnostic gives information.</summary>
    Info,
    /// <summary>The diagnostic gives a warning.</summary>
    Warning,
    /// <summary>The diagnostic reports a failure.</summary>
    Error
}
