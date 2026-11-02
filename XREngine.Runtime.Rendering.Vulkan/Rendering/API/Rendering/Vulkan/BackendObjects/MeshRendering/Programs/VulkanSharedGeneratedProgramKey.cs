using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies a shareable generated program by its backend context and the exact
/// content of its stages. Derived pass materials are separate objects with the same
/// shader text, so a key by material object would not let them share a program.
/// </summary>
internal readonly struct VulkanSharedGeneratedProgramKey(
    VulkanBackendObjectContext context,
    string contentIdentity) : IEquatable<VulkanSharedGeneratedProgramKey>
{
    public VulkanBackendObjectContext Context { get; } = context;
    public string ContentIdentity { get; } = contentIdentity;

    public bool Equals(VulkanSharedGeneratedProgramKey other)
        => ReferenceEquals(Context, other.Context) &&
           string.Equals(ContentIdentity, other.ContentIdentity, StringComparison.Ordinal);

    public override bool Equals(object? obj)
        => obj is VulkanSharedGeneratedProgramKey other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            RuntimeHelpers.GetHashCode(Context),
            StringComparer.Ordinal.GetHashCode(ContentIdentity));
}
