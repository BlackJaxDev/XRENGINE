namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        internal readonly record struct DescriptorAllocationKey(
            ulong LayoutFingerprint,
            ulong SchemaFingerprint,
            int DescriptorFrameSlotCount,
            int SetCount,
            int MaterialIdentity,
            ulong MaterialBindingLayoutVersion,
            int ViewFamilyIdentity,
            int DrawUniformSlot,
            ulong BindingIdentityFingerprint,
            ulong ImmutableResourceFingerprint);
    }
}
