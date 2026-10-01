using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// An immutable, session-owned map of hash-verified cooked modules. Load sidecar
/// bytes through the runtime asset source before publishing the catalog to a renderer.
/// </summary>
public sealed class ShaderProgramArtifactCatalog : IShaderProgramArtifactResolver
{
    private readonly ImmutableDictionary<string, ShaderProgramArtifact> _artifacts;

    public ShaderProgramArtifactCatalog(IEnumerable<ShaderProgramArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ImmutableDictionary<string, ShaderProgramArtifact>.Builder builder = ImmutableDictionary.CreateBuilder<string, ShaderProgramArtifact>(StringComparer.Ordinal);
        foreach (ShaderProgramArtifact artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ValidateIdentity(artifact.Identity);
            if (artifact.DescriptorBytes.IsDefaultOrEmpty)
                throw new InvalidDataException($"ShaderArtifact.DescriptorMissing: '{artifact.Name}' has no verified descriptor bytes.");
            ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
            if (verified.Identity != artifact.Identity)
                throw new InvalidDataException($"ShaderArtifact.IdentityMismatch: '{artifact.Name}' differs from its descriptor bytes.");
            if (!builder.TryAdd(verified.Identity, verified))
                throw new InvalidDataException($"ShaderArtifact.DuplicateIdentity: '{artifact.Identity}'.");
        }
        _artifacts = builder.ToImmutable();
    }

    public int Count => _artifacts.Count;

    public bool TryResolve(string identity, ShaderCompileTarget target, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        if (_artifacts.TryGetValue(identity, out artifact) && artifact.Target == target)
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
