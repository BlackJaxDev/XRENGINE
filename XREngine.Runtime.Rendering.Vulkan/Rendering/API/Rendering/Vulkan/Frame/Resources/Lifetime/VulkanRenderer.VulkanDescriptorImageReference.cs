using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly struct VulkanDescriptorImageReference(
    ImageView view,
    ImageLayout layout,
    DescriptorType type) : IEquatable<VulkanDescriptorImageReference>
{
    public ImageView View { get; } = view;
    public ImageLayout Layout { get; } = layout;
    public DescriptorType Type { get; } = type;

    public bool Equals(VulkanDescriptorImageReference other)
        => View.Handle == other.View.Handle &&
           Layout == other.Layout &&
           Type == other.Type;

    public override bool Equals(object? obj)
        => obj is VulkanDescriptorImageReference other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(View.Handle, Layout, Type);

    public static bool operator ==(
        VulkanDescriptorImageReference left,
        VulkanDescriptorImageReference right)
        => left.Equals(right);

    public static bool operator !=(
        VulkanDescriptorImageReference left,
        VulkanDescriptorImageReference right)
        => !left.Equals(right);
}
