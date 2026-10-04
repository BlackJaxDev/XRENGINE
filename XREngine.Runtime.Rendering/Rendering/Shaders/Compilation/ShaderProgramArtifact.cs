using System.Collections.Immutable;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// A whole cooked shader module with its explicit engine ABI. Its identity is the
/// descriptor hash, including source, compiler, dependencies, and physical layout.
/// </summary>
public sealed record ShaderProgramArtifact(
    string Identity,
    string Name,
    string Pass,
    string SourcePath,
    ShaderArtifact Artifact,
    string SemanticSchemaIdentity,
    string Coordinates,
    string? VertexEntryPoint,
    string? FragmentEntryPoint,
    string? ComputeEntryPoint,
    ImmutableArray<ShaderVertexBufferLayout> VertexBuffers,
    ImmutableArray<ShaderStageResourceLayout> Resources,
    ImmutableDictionary<string, int> RequiredLimits)
{
    /// <summary>Exact validated descriptor bytes retained for content-addressed repackaging; absent for layout-only recipe objects.</summary>
    public ImmutableArray<byte> DescriptorBytes { get; internal init; } = [];

    /// <summary>Declared local invocation dimensions; absent for raster programs.</summary>
    public ShaderComputeWorkgroupSize? ComputeWorkgroupSize { get; internal init; }

    /// <summary>The declared authored frontend used by the target cook.</summary>
    public string SourceLanguage { get; internal init; } = string.Empty;

    /// <summary>Optional exact authored local vertex function shared by the generated raster and native compute programs.</summary>
    public ShaderNativeVertexCompanion? NativeVertexCompanion { get; internal init; }

    /// <summary>The format of the compiled module; authored shader language remains unchanged.</summary>
    public ShaderCompileTarget Target => Artifact.Target;
}
