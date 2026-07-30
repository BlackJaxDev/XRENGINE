namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    internal readonly record struct DescriptorAllocationKey(
        ulong LayoutFingerprint,
        ulong SchemaFingerprint,
        uint ProgramBindingId,
        int DescriptorFrameSlotCount,
        int SetCount,
        int MaterialIdentity,
        ulong MaterialBindingLayoutVersion,
        int ViewFamilyIdentity,
        int DrawUniformSlot,
        ulong BindingIdentityFingerprint,
        ulong ImmutableResourceFingerprint);
}
