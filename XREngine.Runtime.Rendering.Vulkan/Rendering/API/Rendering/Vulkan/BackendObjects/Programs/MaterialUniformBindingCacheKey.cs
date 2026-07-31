using System.Runtime.CompilerServices;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies immutable numeric bindings owned by one material revision.
/// Render-scope values, callbacks, descriptor resources, and render-program
/// identity are intentionally excluded because the captured name/value payload
/// is material-owned and reusable by every compatible program.
/// </summary>
internal readonly struct MaterialUniformBindingCacheKey : IEquatable<MaterialUniformBindingCacheKey>
{
    private readonly XRMaterial _material;
    private readonly ulong _materialLayoutVersion;
    private readonly ulong _materialValueVersion;
    private readonly long _materialShaderRevision;
    private readonly long _materialUberRevision;

    internal MaterialUniformBindingCacheKey(XRMaterial material)
    {
        _material = material;
        _materialLayoutVersion = material.BindingLayoutVersion;
        _materialValueVersion = material.BindingValueVersion;
        _materialShaderRevision = material.ShaderStateRevision;
        _materialUberRevision = material.UberStateRevision;
    }

    public bool Equals(MaterialUniformBindingCacheKey other)
        => ReferenceEquals(_material, other._material) &&
           _materialLayoutVersion == other._materialLayoutVersion &&
           _materialValueVersion == other._materialValueVersion &&
           _materialShaderRevision == other._materialShaderRevision &&
           _materialUberRevision == other._materialUberRevision;

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
        return hash.ToHashCode();
    }
}
