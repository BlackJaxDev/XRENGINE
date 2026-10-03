using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Exact, immutable authored binding-to-program entries from a hash-verified browser package.</summary>
public sealed class WebPipelineArtifactCatalog
{
    public const int MaximumEntries = 256;
    private readonly ImmutableDictionary<string, ShaderProgramArtifact> _artifacts;

    public WebPipelineArtifactCatalog(IEnumerable<KeyValuePair<string, string>> entries, ShaderProgramArtifactCatalog artifacts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(artifacts);
        ImmutableDictionary<string, ShaderProgramArtifact>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, ShaderProgramArtifact>(StringComparer.Ordinal);
        foreach ((string bindingKey, string descriptorIdentity) in entries)
        {
            (string? scope, string pass) = SplitBindingKey(bindingKey);
            string key = GetBindingKey(scope, pass);
            string identity = ShaderProgramArtifactCatalog.ValidateIdentity(descriptorIdentity)
                ?? throw new InvalidDataException($"PipelineArtifact.IdentityMissing: '{key}'.");
            if (!artifacts.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact))
                throw new InvalidDataException($"PipelineArtifact.Missing: '{key}' refers to unverified descriptor '{identity}'.");
            ValidateProgram(key, artifact);
            if (!builder.TryAdd(key, artifact))
                throw new InvalidDataException($"PipelineArtifact.DuplicateBinding: '{key}'.");
            if (builder.Count > MaximumEntries)
                throw new InvalidDataException("PipelineArtifact.BudgetExceeded.");
        }
        _artifacts = builder.ToImmutable();
    }

    public int Count => _artifacts.Count;

    public bool TryResolve(string bindingKey, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
        => _artifacts.TryGetValue(bindingKey, out artifact);

    public bool HasSameIdentities(WebPipelineArtifactCatalog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Count != other.Count)
            return false;
        foreach ((string bindingKey, ShaderProgramArtifact artifact) in _artifacts)
            if (!other.TryResolve(bindingKey, out ShaderProgramArtifact? candidate) || candidate.Identity != artifact.Identity)
                return false;
        return true;
    }

    /// <summary>Accepts a complete authored raster program or one exact compute program, never a material variant.</summary>
    public static void ValidateProgram(string bindingKey, ShaderProgramArtifact artifact)
    {
        string pass = SplitBindingKey(bindingKey).Pass;
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Pass != pass ||
            !(IsCompleteRasterProgram(artifact) ^ IsCompleteComputeProgram(artifact)))
            throw new InvalidDataException($"PipelineArtifact.DescriptorMismatch: '{bindingKey}' refers to an incompatible WebGPU program.");
        using JsonDocument descriptor = JsonDocument.Parse(artifact.DescriptorBytes.ToArray());
        if (descriptor.RootElement.TryGetProperty("materialVariant", out _))
            throw new InvalidDataException($"PipelineArtifact.DescriptorMismatch: '{bindingKey}' declares a material variant.");
        ValidateEngineBindings(artifact);
    }

    /// <summary>Checks the installed shared engine recorder, separately from specialized compute consumers.</summary>
    public static void ValidateEngineBindings(ShaderProgramArtifact artifact)
    {
        int uniforms = 0;
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
        {
            if (resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer && (!resource.DynamicOffset || ++uniforms > 16) ||
                resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer && resource.DynamicOffset)
                throw new InvalidDataException($"PipelineArtifact.BindingUnsupported: '{artifact.Name}' requires dynamic uniforms (at most 16) and non-dynamic storage buffers.");
        }
        foreach (var group in artifact.Resources.Where(resource => resource.Contract.Kind is ShaderAbiResourceKind.SampledImage or ShaderAbiResourceKind.Sampler)
            .GroupBy(resource => resource.Contract.Name, StringComparer.Ordinal))
        {
            ShaderStageResourceLayout[] textures = [.. group.Where(resource => resource.Contract.Kind == ShaderAbiResourceKind.SampledImage)];
            ShaderStageResourceLayout[] samplers = [.. group.Where(resource => resource.Contract.Kind == ShaderAbiResourceKind.Sampler)];
            if (textures.Length != 1 || samplers.Length > 1)
                throw new InvalidDataException($"PipelineArtifact.BindingUnsupported: '{group.Key}' requires one texture and at most one logical sampler.");
            if (samplers.Length == 0) continue;
            ShaderStageResourceLayout texture = textures[0], sampler = samplers[0];
            if (!ShaderTextureBindingType.TryParse(texture.BindingType, out ShaderTextureBindingType shape) ||
                shape.SampleType is "uint" or "sint" || (shape.SampleType == "depth") != (sampler.BindingType == "comparison-sampler") ||
                shape.SampleType == "unfilterable-float" && sampler.BindingType != "non-filtering-sampler" ||
                texture.Contract.Owner != sampler.Contract.Owner || texture.Contract.Frequency != sampler.Contract.Frequency)
                throw new InvalidDataException($"PipelineArtifact.BindingUnsupported: '{group.Key}' texture and sampler contracts are incompatible.");
        }
    }

    /// <summary>Checks the stage shape required by a raster-consuming pipeline pass.</summary>
    public static bool IsCompleteRasterProgram(ShaderProgramArtifact artifact)
        => artifact.VertexEntryPoint is not null && artifact.FragmentEntryPoint is not null &&
            artifact.ComputeEntryPoint is null && artifact.ComputeWorkgroupSize is null;

    /// <summary>Checks the stage shape required by a compute-consuming pipeline pass.</summary>
    public static bool IsCompleteComputeProgram(ShaderProgramArtifact artifact)
        => artifact.ComputeEntryPoint is not null && artifact.VertexEntryPoint is null &&
            artifact.FragmentEntryPoint is null && artifact.ComputeWorkgroupSize is not null &&
            artifact.VertexBuffers.IsEmpty;

    /// <summary>Builds an unambiguous package key while keeping the descriptor's authored pass unchanged.</summary>
    public static string GetBindingKey(string? scope, string pass)
    {
        if (!IsValidComponent(pass) || scope is not null && !IsValidComponent(scope))
            throw new InvalidDataException("PipelineArtifact.BindingInvalid: scope and pass must be bounded lowercase identifiers.");
        return scope is null ? pass : scope + "::" + pass;
    }

    /// <summary>Recovers the optional scope and descriptor pass from a validated package key.</summary>
    public static (string? Scope, string Pass) SplitBindingKey(string bindingKey)
    {
        if (bindingKey is null)
            throw new InvalidDataException("PipelineArtifact.BindingInvalid: binding key is missing.");
        int separator = bindingKey.IndexOf("::", StringComparison.Ordinal);
        string? scope = separator < 0 ? null : bindingKey[..separator];
        string pass = separator < 0 ? bindingKey : bindingKey[(separator + 2)..];
        if (GetBindingKey(scope, pass) != bindingKey)
            throw new InvalidDataException($"PipelineArtifact.BindingInvalid: '{bindingKey}'.");
        return (scope, pass);
    }

    /// <summary>Validates one authored identifier without reserving particular pipeline types or passes.</summary>
    public static bool IsValidComponent(string? component)
    {
        if (component is null || component.Length is < 1 or > 64 || component[0] is not (>= 'a' and <= 'z'))
            return false;
        foreach (char character in component.AsSpan(1))
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.'))
                return false;
        return true;
    }

    /// <summary>Compatibility alias for callers that validate an unscoped pass.</summary>
    public static bool IsSupportedPass(string? pass)
        => IsValidComponent(pass);
}
