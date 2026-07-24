namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IRenderProgramBackendCapability
{
    /// <inheritdoc />
    public bool IsProgramReady(XRRenderProgram program)
        => FindOwnedProgram(program)?.IsLinked == true;

    /// <inheritdoc />
    public string DescribeProgramReadiness(XRRenderProgram program)
        => FindOwnedProgram(program)?.IsLinked.ToString() ?? "missing-wrapper";

    /// <inheritdoc />
    public void LogBackendErrors(string context)
        => LogGLErrors(context);

    private GLRenderProgram? FindOwnedProgram(XRRenderProgram program)
    {
        foreach (IRenderAPIObject wrapper in program.APIWrappers)
            if (wrapper is GLRenderProgram apiProgram && ReferenceEquals(apiProgram.Owner, this))
                return apiProgram;
        return null;
    }
}
