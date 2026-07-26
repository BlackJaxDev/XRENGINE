namespace XREngine;

/// <summary>
/// Captures the effective OpenGL runtime configuration.
/// </summary>
public readonly record struct EffectiveOpenGLRenderSettings(
    bool AllowProgramPipelines,
    bool AllowBinaryProgramCaching,
    bool AsyncProgramBinaryUpload,
    bool AsyncProgramCompilation,
    int ProgramCompileLinkWorkerCount,
    int MaxAsyncShaderProgramsPerFrame,
    EOpenGLShaderLinkStrategy ShaderLinkStrategy,
    int DriverCompilerThreadCount,
    bool DriverParallelProbeEnabled,
    int DriverParallelProbeTimeoutMs,
    bool UseDetailPreservingComputeMipmaps);
