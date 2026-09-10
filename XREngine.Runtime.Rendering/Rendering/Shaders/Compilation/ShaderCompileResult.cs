namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Bytecode and provenance emitted by a shader frontend.
/// </summary>
public sealed record ShaderCompileResult(
    byte[] SpirV,
    string EntryPoint,
    string ArtifactIdentity,
    string CompilerIdentity,
    string? ReflectionJson,
    IReadOnlyList<ShaderCompileDependency> Dependencies,
    IReadOnlyList<ShaderCompileDiagnostic> Diagnostics,
    bool LoadedFromCache,
    TimeSpan CompileDuration);
