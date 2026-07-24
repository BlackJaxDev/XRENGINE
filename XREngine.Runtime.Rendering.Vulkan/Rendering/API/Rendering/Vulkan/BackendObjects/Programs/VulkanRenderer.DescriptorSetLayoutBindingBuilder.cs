using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class DescriptorSetLayoutBindingBuilder(DescriptorBindingInfo info)
    {
        public uint Set { get; } = info.Set;
        public uint Binding { get; } = info.Binding;
        public DescriptorType DescriptorType { get; } = info.DescriptorType;
        public uint Count { get; } = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(info);
        public string Name { get; private set; } = string.IsNullOrWhiteSpace(info.Name) ? string.Empty : info.Name;
        public ShaderStageFlags StageFlags { get; private set; } = info.StageFlags;
        public ImageViewType? ExpectedImageViewType { get; private set; } = info.ExpectedImageViewType;

        public void Merge(DescriptorBindingInfo info)
        {
            uint incomingCount = VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(info);
            if (info.DescriptorType != DescriptorType || incomingCount != Count)
            {
                Debug.VulkanWarning($"Ignoring conflicting descriptor definition for set {Set}, binding {Binding}. Existing: {DescriptorType} x{Count}, incoming: {info.DescriptorType} x{incomingCount}.");
                return;
            }

            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(info.Name))
                Name = info.Name;

            ExpectedImageViewType ??= info.ExpectedImageViewType;
            StageFlags |= info.StageFlags;
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
            => new(Set, Binding, DescriptorType, StageFlags, Count, Name, ExpectedImageViewType);
    }

}
