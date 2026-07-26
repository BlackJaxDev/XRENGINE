namespace XREngine.Rendering;

/// <summary>
/// Backend program readiness and diagnostics used by stable submission code.
/// </summary>
public interface IRenderProgramBackendCapability
{
    bool IsProgramReady(XRRenderProgram program);
    string DescribeProgramReadiness(XRRenderProgram program);
    void LogBackendErrors(string context);
}
