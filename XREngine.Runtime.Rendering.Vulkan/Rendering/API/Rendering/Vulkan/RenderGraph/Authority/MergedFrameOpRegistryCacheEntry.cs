using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>Reusable merged registry and its compatibility inputs for one planner-state key.</summary>
internal sealed class MergedFrameOpRegistryCacheEntry(
    VulkanFrameOpPlannerStateKey ownerKey,
    RenderResourceRegistry? primaryRegistry,
    FrameOpRegistryCacheSource[] sources,
    int frameBufferDescriptorSignature,
    ulong frameOpsSignature,
    RenderResourceRegistry mergedRegistry,
    ulong lastUsedFrameId)
{
    public VulkanFrameOpPlannerStateKey OwnerKey { get; } = ownerKey;
    public RenderResourceRegistry? PrimaryRegistry { get; } = primaryRegistry;
    public int PrimaryDescriptorSignature { get; set; } = primaryRegistry?.DescriptorSignature ?? 0;
    public FrameOpRegistryCacheSource[] Sources { get; set; } = sources;
    public int FrameBufferDescriptorSignature { get; set; } = frameBufferDescriptorSignature;
    public ulong FrameOpsSignature { get; set; } = frameOpsSignature;
    public RenderResourceRegistry MergedRegistry { get; set; } = mergedRegistry;
    public ulong LastUsedFrameId { get; set; } = lastUsedFrameId;
}
