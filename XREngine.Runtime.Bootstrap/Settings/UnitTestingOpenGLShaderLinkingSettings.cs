namespace XREngine.Runtime.Bootstrap;

public class UnitTestingOpenGLShaderLinkingSettings
{
    public EOpenGLShaderLinkStrategy Strategy { get; set; } = EOpenGLShaderLinkStrategy.Auto;
    public bool AllowBinaryProgramCaching { get; set; } = true;
    public bool AsyncProgramBinaryUpload { get; set; } = true;
    public bool AsyncProgramCompilation { get; set; } = true;
    public int ProgramCompileLinkWorkerCount { get; set; } = 1;
    public int MaxAsyncShaderProgramsPerFrame { get; set; } = 16;
    public int DriverCompilerThreadCount { get; set; } = -1;
    public bool DriverParallelProbeEnabled { get; set; } = true;
    public int DriverParallelProbeTimeoutMs { get; set; } = 25;
}
