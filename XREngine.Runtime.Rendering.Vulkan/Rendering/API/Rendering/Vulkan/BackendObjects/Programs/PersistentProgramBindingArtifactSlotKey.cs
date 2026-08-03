using System.Runtime.CompilerServices;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stable owner slot for one cross-frame program-binding artifact. Content
/// generations are stored separately so mutations replace the slot instead of
/// growing the cache once per revision.
/// </summary>
internal readonly struct PersistentProgramBindingArtifactSlotKey :
    IEquatable<PersistentProgramBindingArtifactSlotKey>
{
    private readonly XRMaterial _material;
    private readonly XRMeshRenderer _meshRenderer;

    internal PersistentProgramBindingArtifactSlotKey(
        XRMaterial material,
        XRMeshRenderer meshRenderer)
    {
        _material = material;
        _meshRenderer = meshRenderer;
    }

    public bool Equals(PersistentProgramBindingArtifactSlotKey other)
        => ReferenceEquals(_material, other._material) &&
            ReferenceEquals(_meshRenderer, other._meshRenderer);

    public override bool Equals(object? obj)
        => obj is PersistentProgramBindingArtifactSlotKey other &&
            Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            RuntimeHelpers.GetHashCode(_material),
            RuntimeHelpers.GetHashCode(_meshRenderer));
}
