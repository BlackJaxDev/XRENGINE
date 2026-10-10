using System.Collections.Immutable;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Owns bounded, closed-world WebGPU shader residency for one session. Catalog views stay
/// stable while complete validated batches become visible in a single publication.
/// </summary>
public sealed class ShaderArtifactCatalogProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly ImmutableHashSet<string> _declaredIdentities;
    private readonly ImmutableArray<EngineMaterialVariantEntry> _variants;
    private readonly ImmutableDictionary<string, string> _pipelines;
    private readonly ImmutableDictionary<string, string> _computes;
    private ShaderArtifactCatalogSnapshot _snapshot;
    private bool _retired;

    public ShaderArtifactCatalogProvider(IEnumerable<string> identities,
        IEnumerable<EngineMaterialVariantEntry> variants,
        IEnumerable<KeyValuePair<string, string>> pipelines,
        IEnumerable<KeyValuePair<string, string>> computes)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(computes);
        var declared = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (string identity in identities)
            if (ShaderProgramArtifactCatalog.ValidateIdentity(identity) is null || !declared.Add(identity) || declared.Count > 256)
                throw new InvalidDataException("ShaderArtifact.DeclarationInvalid: expected at most 256 unique descriptor identities.");
        _declaredIdentities = declared.ToImmutable();
        _variants = [.. variants];
        _pipelines = pipelines.ToImmutableDictionary(StringComparer.Ordinal);
        _computes = computes.ToImmutableDictionary(StringComparer.Ordinal);
        if (_variants.Length > 256 || _pipelines.Count > WebPipelineArtifactCatalog.MaximumEntries || _computes.Count > 4)
            throw new InvalidDataException("ShaderArtifact.BindingBudgetExceeded.");
        HashSet<EngineMaterialVariantKey> keys = [];
        foreach (EngineMaterialVariantEntry variant in _variants)
        {
            variant.Key.Validate();
            if (variant.Key.Target != ShaderCompileTarget.WebGPUWgsl)
                throw new InvalidDataException("ShaderArtifact.TargetUnsupported: session delivery requires WebGPU modules.");
            RequireDeclared(variant.DescriptorIdentity);
            if (!keys.Add(variant.Key))
                throw new InvalidDataException($"MaterialVariant.DuplicateKey: '{variant.Key}'.");
        }
        foreach ((string binding, string identity) in _pipelines)
        {
            _ = WebPipelineArtifactCatalog.SplitBindingKey(binding);
            RequireDeclared(identity);
        }
        foreach ((string kernel, string identity) in _computes)
        {
            if (!WebComputeArtifactCatalog.IsSupportedKernel(kernel))
                throw new InvalidDataException($"ComputeArtifact.KernelUnsupported: '{kernel}'.");
            RequireDeclared(identity);
        }
        _snapshot = CreateSnapshot([]);
        Artifacts = new(this);
        MaterialVariants = new(this);
        PipelineArtifacts = new(this);
        ComputeArtifacts = new(this);
    }

    public ShaderProgramArtifactCatalog Artifacts { get; }
    public EngineMaterialVariantCatalog MaterialVariants { get; }
    public WebPipelineArtifactCatalog PipelineArtifacts { get; }
    public WebComputeArtifactCatalog ComputeArtifacts { get; }
    internal ShaderArtifactCatalogSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Validates every module and declared binding before publishing any member of a batch.</summary>
    public void Publish(IEnumerable<ShaderProgramArtifact> artifacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, ShaderProgramArtifact> combined = _snapshot.Artifacts.Artifacts
                .ToDictionary(static artifact => artifact.Identity, StringComparer.Ordinal);
            HashSet<string> batchIdentities = new(StringComparer.Ordinal);
            foreach (ShaderProgramArtifact artifact in artifacts)
            {
                ArgumentNullException.ThrowIfNull(artifact);
                RequireDeclared(artifact.Identity);
                if (!batchIdentities.Add(artifact.Identity))
                    throw new InvalidDataException($"ShaderArtifact.DuplicateIdentity: '{artifact.Identity}'.");
                // Previously published objects remain authoritative. Validate duplicate
                // input too, so a shared identity cannot conceal an invalid source.
                ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
                if (verified.Identity != artifact.Identity || verified.Target != ShaderCompileTarget.WebGPUWgsl)
                    throw new InvalidDataException($"ShaderArtifact.IdentityMismatch: '{artifact.Identity}'.");
                combined.TryAdd(artifact.Identity, verified);
            }
            ShaderArtifactCatalogSnapshot candidate = CreateSnapshot(combined.Values);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _snapshot, candidate);
        }
    }

    private ShaderArtifactCatalogSnapshot CreateSnapshot(IEnumerable<ShaderProgramArtifact> artifacts)
    {
        ShaderProgramArtifactCatalog shaders = new(artifacts, artifactsAreVerified: true);
        bool Resident(string identity) => shaders.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out _);
        return new(shaders,
            new EngineMaterialVariantCatalog(_variants.Where(entry => Resident(entry.DescriptorIdentity)), shaders),
            new WebPipelineArtifactCatalog(_pipelines.Where(entry => Resident(entry.Value)), shaders),
            new WebComputeArtifactCatalog(_computes.Where(entry => Resident(entry.Value)), shaders));
    }

    private void RequireDeclared(string identity)
    {
        if (!_declaredIdentities.Contains(identity))
            throw new InvalidDataException($"ShaderArtifact.UndeclaredIdentity: '{identity}'.");
    }

    /// <summary>Prevents further admission and releases provider ownership; existing program references remain valid.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_retired) return;
            _retired = true;
            Volatile.Write(ref _snapshot, CreateSnapshot([]));
        }
    }
}
