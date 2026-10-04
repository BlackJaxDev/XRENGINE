using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// A map of hash-verified cooked modules. Ordinary catalogs are immutable;
/// session provider views retain their identity while complete immutable snapshots are admitted.
/// </summary>
public sealed class ShaderProgramArtifactCatalog : IShaderProgramArtifactResolver
{
    private readonly ImmutableDictionary<string, ShaderProgramArtifact> _artifacts;
    private readonly ImmutableHashSet<(ShaderCompileTarget Target, string Language, string Schema, string Pass)> _profiles;

    public ShaderProgramArtifactCatalog(IEnumerable<ShaderProgramArtifact> artifacts) : this(artifacts, false) { }

    internal ShaderProgramArtifactCatalog(IEnumerable<ShaderProgramArtifact> artifacts, bool artifactsAreVerified)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ImmutableDictionary<string, ShaderProgramArtifact>.Builder builder = ImmutableDictionary.CreateBuilder<string, ShaderProgramArtifact>(StringComparer.Ordinal);
        var profiles = ImmutableHashSet.CreateBuilder<(ShaderCompileTarget, string, string, string)>();
        foreach (ShaderProgramArtifact artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ValidateIdentity(artifact.Identity);
            if (artifact.DescriptorBytes.IsDefaultOrEmpty)
                throw new InvalidDataException($"ShaderArtifact.DescriptorMissing: '{artifact.Name}' has no verified descriptor bytes.");
            ShaderProgramArtifact verified = artifactsAreVerified ? artifact
                : ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
            if (verified.Identity != artifact.Identity)
                throw new InvalidDataException($"ShaderArtifact.IdentityMismatch: '{artifact.Name}' differs from its descriptor bytes.");
            if (!builder.TryAdd(verified.Identity, verified))
                throw new InvalidDataException($"ShaderArtifact.DuplicateIdentity: '{artifact.Identity}'.");
            profiles.Add((verified.Target, verified.SourceLanguage, verified.SemanticSchemaIdentity, verified.Pass));
        }
        _artifacts = builder.ToImmutable();
        _profiles = profiles.ToImmutable();
    }

    private readonly ShaderArtifactCatalogProvider? _provider;
    internal ShaderProgramArtifactCatalog(ShaderArtifactCatalogProvider provider) : this([]) => _provider = provider;
    private ShaderProgramArtifactCatalog Current => _provider?.Snapshot.Artifacts ?? this;
    internal IEnumerable<ShaderProgramArtifact> Artifacts => Current._artifacts.Values;
    public int Count => Current._artifacts.Count;

    /// <summary>Checks a verified program profile without enumerating or allocating during frame admission.</summary>
    public bool ContainsProgram(ShaderCompileTarget target, string language, string schema, string pass)
        => Current._profiles.Contains((target, language, schema, pass));

    public bool TryResolve(string identity, ShaderCompileTarget target, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        if (Current._artifacts.TryGetValue(identity, out artifact) && artifact.Target == target)
            return true;
        artifact = null;
        return false;
    }

    /// <summary>Validates the serialized content-addressed reference without requiring its payload to be loaded yet.</summary>
    public static string? ValidateIdentity(string? identity)
    {
        if (identity is null)
            return null;
        if (identity.Length != 64)
            throw new ArgumentException("ShaderArtifact.IdentityInvalid: an identity must be a lowercase SHA-256 descriptor hash.", nameof(identity));
        foreach (char character in identity)
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new ArgumentException("ShaderArtifact.IdentityInvalid: an identity must be a lowercase SHA-256 descriptor hash.", nameof(identity));
        return identity;
    }
}
