namespace XREngine.Rendering;

/// <summary>
/// Copies optional shader-program diagnostics out of a backend without exposing or
/// retaining backend wrapper instances in editor or application state.
/// </summary>
public interface IShaderProgramLinkDiagnosticsBackendCapability
{
    /// <summary>
    /// Appends value-only snapshots for the renderer's currently tracked programs.
    /// Callers own the destination and may retain the copied values across module unload.
    /// </summary>
    void CaptureShaderProgramLinkDiagnostics(List<ShaderProgramLinkDiagnostic> destination);

    /// <summary>
    /// Writes the backend's current shader lifecycle summary to its configured log.
    /// </summary>
    void LogShaderProgramLifecycleSummary();
}
