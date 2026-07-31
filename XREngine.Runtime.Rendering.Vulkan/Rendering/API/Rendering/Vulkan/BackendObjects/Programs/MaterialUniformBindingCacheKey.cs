using System.Runtime.CompilerServices;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies immutable numeric material bindings that can outlive a render
/// frame. Render-scope values, callbacks, and descriptor resources are
/// intentionally excluded: they have different ownership and frequency.
/// </summary>
internal readonly struct MaterialUniformBindingCacheKey : IEquatable<MaterialUniformBindingCacheKey>
{
    private readonly XRMaterial _material;
    private readonly ulong _materialLayoutVersion;
    private readonly ulong _materialValueVersion;
    private readonly long _materialShaderRevision;
    private readonly long _materialUberRevision;
    private readonly ulong _programLinkGeneration;

    internal MaterialUniformBindingCacheKey(XRMaterial material, ulong programLinkGeneration)
    {
        _material = material;
        _materialLayoutVersion = material.BindingLayoutVersion;
        _materialValueVersion = material.BindingValueVersion;
        _materialShaderRevision = material.ShaderStateRevision;
        _materialUberRevision = material.UberStateRevision;
        _programLinkGeneration = programLinkGeneration;
    }

    public bool Equals(MaterialUniformBindingCacheKey other)
        => ReferenceEquals(_material, other._material) &&
           _materialLayoutVersion == other._materialLayoutVersion &&
           _materialValueVersion == other._materialValueVersion &&
           _materialShaderRevision == other._materialShaderRevision &&
           _materialUberRevision == other._materialUberRevision &&
           _programLinkGeneration == other._programLinkGeneration;

    public override bool Equals(object? obj)
        => obj is MaterialUniformBindingCacheKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(RuntimeHelpers.GetHashCode(_material));
        hash.Add(_materialLayoutVersion);
        hash.Add(_materialValueVersion);
        hash.Add(_materialShaderRevision);
        hash.Add(_materialUberRevision);
        hash.Add(_programLinkGeneration);
        return hash.ToHashCode();
    }
}
