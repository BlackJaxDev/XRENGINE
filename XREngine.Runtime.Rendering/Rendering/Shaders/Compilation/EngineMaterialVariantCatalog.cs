using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Content-verified catalog of complete engine material variants, optionally backed by a stable session provider.
/// Missing variants are unsupported rather than replaced by a guessed shader.
/// </summary>
public sealed class EngineMaterialVariantCatalog : IEngineMaterialVariantResolver
{
    private readonly ImmutableDictionary<EngineMaterialVariantKey, ShaderProgramArtifact> _variants;

    public EngineMaterialVariantCatalog(IEnumerable<EngineMaterialVariantEntry> entries, ShaderProgramArtifactCatalog artifacts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(artifacts);
        ImmutableDictionary<EngineMaterialVariantKey, ShaderProgramArtifact>.Builder builder =
            ImmutableDictionary.CreateBuilder<EngineMaterialVariantKey, ShaderProgramArtifact>();
        foreach (EngineMaterialVariantEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            entry.Key.Validate();
            string identity = ShaderProgramArtifactCatalog.ValidateIdentity(entry.DescriptorIdentity)
                ?? throw new ArgumentException("A cooked material variant requires a descriptor identity.", nameof(entries));
            if (!artifacts.TryResolve(identity, entry.Key.Target, out ShaderProgramArtifact? artifact))
                throw new InvalidDataException($"MaterialVariant.ArtifactMissing: '{identity}' is not a validated {entry.Key.Target} artifact.");
            if (!string.Equals(artifact.Pass, entry.Key.Pass, StringComparison.Ordinal))
                throw new InvalidDataException($"MaterialVariant.PassMismatch: '{identity}' declares pass '{artifact.Pass}', not '{entry.Key.Pass}'.");
            using JsonDocument descriptor = JsonDocument.Parse(artifact.DescriptorBytes.ToArray());
            if (!descriptor.RootElement.TryGetProperty("materialVariant", out JsonElement declaration)
                || ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration, artifact.Pass, artifact.Target) != entry.Key)
                throw new InvalidDataException($"MaterialVariant.DescriptorMismatch: '{identity}'.");
            if (!builder.TryAdd(entry.Key, artifact))
                throw new InvalidDataException($"MaterialVariant.DuplicateKey: '{entry.Key}'.");
        }
        _variants = builder.ToImmutable();
    }

    private readonly ShaderArtifactCatalogProvider? _provider;
    internal EngineMaterialVariantCatalog(ShaderArtifactCatalogProvider provider) : this([], new ShaderProgramArtifactCatalog([])) => _provider = provider;
    private EngineMaterialVariantCatalog Current => _provider?.Snapshot.MaterialVariants ?? this;
    public int Count => Current._variants.Count;

    public bool TryResolve(EngineMaterialVariantKey key, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
        => Current._variants.TryGetValue(key, out artifact);
}
