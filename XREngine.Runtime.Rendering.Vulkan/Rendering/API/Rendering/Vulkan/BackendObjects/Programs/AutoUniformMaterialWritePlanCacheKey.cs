using XREngine.Rendering;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one compiled material write-plan variant. A material can appear
/// in multiple binding scopes whose runtime-owned uniform layouts differ, so
/// those variants must coexist instead of evicting one another every frame.
/// </summary>
internal readonly struct AutoUniformMaterialWritePlanCacheKey :
    IEquatable<AutoUniformMaterialWritePlanCacheKey>
{
    private readonly ulong _publicationLayoutSignature;
    private readonly int _materialIdentity;
    private readonly ulong _materialLayoutVersion;
    private readonly ulong _materialValueVersion;
    private readonly ulong _runtimeUniformNameSignature;
    private readonly ulong _runtimeUniformPublicationLayoutSignature;

    internal AutoUniformMaterialWritePlanCacheKey(
        ulong publicationLayoutSignature,
        XRMaterial material,
        ulong runtimeUniformNameSignature,
        ulong runtimeUniformPublicationLayoutSignature)
    {
        _publicationLayoutSignature = publicationLayoutSignature;
        _materialIdentity = RuntimeHelpers.GetHashCode(material);
        _materialLayoutVersion = material.BindingLayoutVersion;
        _materialValueVersion = material.BindingValueVersion;
        _runtimeUniformNameSignature = runtimeUniformNameSignature;
        _runtimeUniformPublicationLayoutSignature =
            runtimeUniformPublicationLayoutSignature;
    }

    internal ulong MaterialLayoutVersion => _materialLayoutVersion;
    internal ulong MaterialValueVersion => _materialValueVersion;

    public bool Equals(AutoUniformMaterialWritePlanCacheKey other)
        => _publicationLayoutSignature ==
               other._publicationLayoutSignature &&
           _materialIdentity == other._materialIdentity &&
           _materialLayoutVersion == other._materialLayoutVersion &&
           _materialValueVersion == other._materialValueVersion &&
           _runtimeUniformNameSignature == other._runtimeUniformNameSignature &&
           _runtimeUniformPublicationLayoutSignature ==
               other._runtimeUniformPublicationLayoutSignature;

    public override bool Equals(object? obj)
        => obj is AutoUniformMaterialWritePlanCacheKey other &&
           Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(_publicationLayoutSignature);
        hash.Add(_materialIdentity);
        hash.Add(_materialLayoutVersion);
        hash.Add(_materialValueVersion);
        hash.Add(_runtimeUniformNameSignature);
        hash.Add(_runtimeUniformPublicationLayoutSignature);
        return hash.ToHashCode();
    }
}
