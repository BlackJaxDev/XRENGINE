namespace XREngine.ShaderTranslation.Diagnostics;

/// <summary>
/// Identifies how a translation diagnostic affects the result.
/// </summary>
public enum ShaderTranslationSeverity
{
    /// <summary>The result is complete. The message records a decision.</summary>
    Info,

    /// <summary>The result renders, but a behavior differs from the source.</summary>
    Warning,

    /// <summary>The pass or shader did not translate.</summary>
    Error,
}
