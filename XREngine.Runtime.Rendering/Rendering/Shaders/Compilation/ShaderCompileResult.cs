namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Target-tagged shader artifact and provenance emitted by a shader frontend.
/// </summary>
public sealed record ShaderCompileResult
{
    private ShaderArtifact _artifact = null!;

    public ShaderCompileResult(
        ShaderArtifact Artifact,
        string EntryPoint,
        string ArtifactIdentity,
        string CompilerIdentity,
        string? ReflectionJson,
        IReadOnlyList<ShaderCompileDependency> Dependencies,
        IReadOnlyList<ShaderCompileDiagnostic> Diagnostics,
        bool LoadedFromCache,
        TimeSpan CompileDuration)
    {
        this.Artifact = Artifact;
        this.EntryPoint = EntryPoint;
        this.ArtifactIdentity = ArtifactIdentity;
        this.CompilerIdentity = CompilerIdentity;
        this.ReflectionJson = ReflectionJson;
        this.Dependencies = Dependencies;
        this.Diagnostics = Diagnostics;
        this.LoadedFromCache = LoadedFromCache;
        this.CompileDuration = CompileDuration;
    }

    /// <summary>Preserves the original Vulkan SPIR-V constructor.</summary>
    public ShaderCompileResult(
        byte[] SpirV,
        string EntryPoint,
        string ArtifactIdentity,
        string CompilerIdentity,
        string? ReflectionJson,
        IReadOnlyList<ShaderCompileDependency> Dependencies,
        IReadOnlyList<ShaderCompileDiagnostic> Diagnostics,
        bool LoadedFromCache,
        TimeSpan CompileDuration)
        : this(ShaderArtifact.FromSpirV(SpirV), EntryPoint, ArtifactIdentity, CompilerIdentity,
            ReflectionJson, Dependencies, Diagnostics, LoadedFromCache, CompileDuration)
    {
    }

    /// <summary>The target-tagged output payload.</summary>
    public ShaderArtifact Artifact
    {
        get => _artifact;
        init => _artifact = value ?? throw new ArgumentNullException(nameof(value));
    }

    public ShaderCompileTarget Target => Artifact.Target;

    /// <summary>Compatibility accessor for Vulkan callers; rejects WGSL artifacts.</summary>
    public byte[] SpirV
    {
        get => Artifact.SpirV;
        init => Artifact = ShaderArtifact.FromSpirV(value);
    }

    public string EntryPoint { get; init; }
    public string ArtifactIdentity { get; init; }
    public string CompilerIdentity { get; init; }
    public string? ReflectionJson { get; init; }
    public IReadOnlyList<ShaderCompileDependency> Dependencies { get; init; }
    public IReadOnlyList<ShaderCompileDiagnostic> Diagnostics { get; init; }
    public bool LoadedFromCache { get; init; }
    public TimeSpan CompileDuration { get; init; }

    public override string ToString() => $"ShaderCompileResult {{ Target = {Target}, EntryPoint = {EntryPoint}, ArtifactIdentity = {ArtifactIdentity} }}";

    /// <summary>Preserves positional deconstruction for existing Vulkan callers.</summary>
    public void Deconstruct(
        out byte[] SpirV,
        out string EntryPoint,
        out string ArtifactIdentity,
        out string CompilerIdentity,
        out string? ReflectionJson,
        out IReadOnlyList<ShaderCompileDependency> Dependencies,
        out IReadOnlyList<ShaderCompileDiagnostic> Diagnostics,
        out bool LoadedFromCache,
        out TimeSpan CompileDuration)
    {
        SpirV = this.SpirV;
        EntryPoint = this.EntryPoint;
        ArtifactIdentity = this.ArtifactIdentity;
        CompilerIdentity = this.CompilerIdentity;
        ReflectionJson = this.ReflectionJson;
        Dependencies = this.Dependencies;
        Diagnostics = this.Diagnostics;
        LoadedFromCache = this.LoadedFromCache;
        CompileDuration = this.CompileDuration;
    }
}
