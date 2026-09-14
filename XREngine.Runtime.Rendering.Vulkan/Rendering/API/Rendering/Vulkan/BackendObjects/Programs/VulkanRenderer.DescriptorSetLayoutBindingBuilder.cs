using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class DescriptorSetLayoutBindingBuilder(DescriptorBindingInfo info)
{
    public uint Set { get; } = info.Set;
    public uint Binding { get; } = info.Binding;
    public DescriptorType DescriptorType { get; } = info.DescriptorType;
    public uint Count { get; } = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(info);
    public string Name { get; private set; } = string.IsNullOrWhiteSpace(info.Name) ? string.Empty : info.Name;
    public ShaderStageFlags StageFlags { get; private set; } = info.StageFlags;
    public ImageViewType? ExpectedImageViewType { get; private set; } = info.ExpectedImageViewType;
    public EVulkanDescriptorBindingRequirement Requirement { get; private set; } = info.Requirement;
    public EVulkanDescriptorOwner? DeclaredOwner { get; private set; } = info.DeclaredOwner;
    public EVulkanBindingFrequency? DeclaredFrequency { get; private set; } = info.DeclaredFrequency;
    public string? NativeAbiIdentity { get; } = info.NativeAbiIdentity;

    public void Merge(DescriptorBindingInfo info)
    {
        // Vulkan links descriptor topology, not host member layouts or semantic providers.
        // An uncontracted stage cannot prove it shares a native resource's ABI either.
        if (!string.Equals(NativeAbiIdentity, info.NativeAbiIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException($"Conflicting or uncontracted native resource ABI at {Set}:{Binding}.");
        if ((DeclaredOwner.HasValue && info.DeclaredOwner.HasValue && DeclaredOwner != info.DeclaredOwner) ||
            (DeclaredFrequency.HasValue && info.DeclaredFrequency.HasValue && DeclaredFrequency != info.DeclaredFrequency))
            throw new InvalidOperationException($"Conflicting explicit descriptor ownership at {Set}:{Binding}.");
        uint incomingCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(info);
        if (info.DescriptorType != DescriptorType || incomingCount != Count)
        {
            if (DeclaredOwner.HasValue || info.DeclaredOwner.HasValue)
                throw new InvalidOperationException($"Conflicting native descriptor ABI at {Set}:{Binding}.");
            Debug.VulkanWarning($"Ignoring conflicting descriptor definition for set {Set}, binding {Binding}. Existing: {DescriptorType} x{Count}, incoming: {info.DescriptorType} x{incomingCount}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(info.Name))
            Name = info.Name;

        ExpectedImageViewType ??= info.ExpectedImageViewType;
        DeclaredOwner ??= info.DeclaredOwner;
        DeclaredFrequency ??= info.DeclaredFrequency;
        StageFlags |= info.StageFlags;
        if (info.Requirement == EVulkanDescriptorBindingRequirement.Required)
            Requirement = EVulkanDescriptorBindingRequirement.Required;
    }

    public DescriptorSetLayoutBinding ToBinding()
        => new()
        {
            Binding = Binding,
            DescriptorType = DescriptorType,
            DescriptorCount = Count,
            StageFlags = StageFlags,
        };

    public DescriptorBindingInfo ToDescriptorBindingInfo()
        => new(Set, Binding, DescriptorType, StageFlags, Count, Name, ExpectedImageViewType, Requirement, DeclaredOwner, DeclaredFrequency, NativeAbiIdentity);
}
