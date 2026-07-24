using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class VulkanDescriptorSetLifetimeRecord
    {
        public readonly Dictionary<(uint Binding, uint Element), VulkanDescriptorReferencePair> References = new();
        public readonly Dictionary<(uint Binding, uint Element), VulkanDescriptorImageReference> ImageReferences = new();
        public readonly HashSet<uint> ReflectedImageBindings = [];
        public readonly HashSet<VulkanResourceLifetimeKey> IndexedReferences = [];
        public DescriptorPool Pool;
        public bool UsesUpdateAfterBind;
        public bool HasReflection;
        public ulong Generation;
    }
}
