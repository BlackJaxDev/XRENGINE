using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Exact, immutable pass-to-program bindings from a hash-verified browser package.</summary>
public sealed class WebPipelineArtifactCatalog
{
    private readonly ImmutableDictionary<string, ShaderProgramArtifact> _artifacts;

    public WebPipelineArtifactCatalog(IEnumerable<KeyValuePair<string, string>> entries, ShaderProgramArtifactCatalog artifacts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(artifacts);
        ImmutableDictionary<string, ShaderProgramArtifact>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, ShaderProgramArtifact>(StringComparer.Ordinal);
        foreach ((string pass, string descriptorIdentity) in entries)
        {
            if (!IsSupportedPass(pass))
                throw new InvalidDataException($"PipelineArtifact.PassUnsupported: '{pass}'.");
            string identity = ShaderProgramArtifactCatalog.ValidateIdentity(descriptorIdentity)
                ?? throw new InvalidDataException($"PipelineArtifact.IdentityMissing: '{pass}'.");
            if (!artifacts.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact))
                throw new InvalidDataException($"PipelineArtifact.Missing: '{pass}' refers to unverified descriptor '{identity}'.");
            if (artifact.Pass != pass || artifact.VertexEntryPoint is null || artifact.FragmentEntryPoint is null ||
                artifact.ComputeEntryPoint is not null)
                throw new InvalidDataException($"PipelineArtifact.DescriptorMismatch: '{pass}' refers to an incompatible WebGPU program.");
            using JsonDocument descriptor = JsonDocument.Parse(artifact.DescriptorBytes.ToArray());
            if (descriptor.RootElement.TryGetProperty("materialVariant", out _))
                throw new InvalidDataException($"PipelineArtifact.DescriptorMismatch: '{pass}' declares a material variant.");
            if (!builder.TryAdd(pass, artifact))
                throw new InvalidDataException($"PipelineArtifact.DuplicatePass: '{pass}'.");
        }
        if (builder.Count > 16)
            throw new InvalidDataException("PipelineArtifact.BudgetExceeded.");
        _artifacts = builder.ToImmutable();
    }

    public int Count => _artifacts.Count;

    public bool TryResolve(string pass, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
        => _artifacts.TryGetValue(pass, out artifact);

    public bool HasSameIdentities(WebPipelineArtifactCatalog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Count != other.Count)
            return false;
        foreach ((string pass, ShaderProgramArtifact artifact) in _artifacts)
            if (!other.TryResolve(pass, out ShaderProgramArtifact? candidate) || candidate.Identity != artifact.Identity)
                return false;
        return true;
    }

    /// <summary>Only engine-owned raster passes may be selected by package metadata.</summary>
    public static bool IsSupportedPass(string? pass)
        => pass is "tonemap" or "depth-normal" or "gtao-generate" or "gtao-blur-horizontal" or
            "gtao-blur-vertical" or "bloom-copy" or "bloom-downsample" or "bloom-upsample" or "bloom-combine";
}
