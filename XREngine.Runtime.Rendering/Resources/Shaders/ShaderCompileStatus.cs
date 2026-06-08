namespace XREngine.Rendering;

public enum EShaderCompileFailureKind
{
    None,
    SourceResolution,
    Preprocessing,
    SpirvCompilation,
    Reflection,
    ShaderModuleCreation,
    PipelineInterfaceMismatch,
}

public readonly record struct ShaderCompileStatus(
    bool IsCompiled,
    bool IsCompilePending,
    bool HasFailure,
    string? FailureReason,
    string? Backend,
    EShaderCompileFailureKind FailureKind,
    string? ArtifactIdentity,
    string? DiagnosticPath)
{
    public static ShaderCompileStatus Empty { get; } = new(
        IsCompiled: false,
        IsCompilePending: false,
        HasFailure: false,
        FailureReason: null,
        Backend: null,
        FailureKind: EShaderCompileFailureKind.None,
        ArtifactIdentity: null,
        DiagnosticPath: null);

    public static ShaderCompileStatus Pending(string backend, string? artifactIdentity)
        => new(false, true, false, null, backend, EShaderCompileFailureKind.None, artifactIdentity, null);

    public static ShaderCompileStatus Ready(string backend, string artifactIdentity)
        => new(true, false, false, null, backend, EShaderCompileFailureKind.None, artifactIdentity, null);

    public static ShaderCompileStatus Failed(
        string backend,
        EShaderCompileFailureKind failureKind,
        string failureReason,
        string? artifactIdentity,
        string? diagnosticPath)
        => new(false, false, true, failureReason, backend, failureKind, artifactIdentity, diagnosticPath);
}
