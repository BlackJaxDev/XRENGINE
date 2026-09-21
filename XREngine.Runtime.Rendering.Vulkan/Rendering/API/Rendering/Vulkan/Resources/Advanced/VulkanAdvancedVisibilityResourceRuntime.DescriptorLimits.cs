namespace XREngine.Rendering.Vulkan;

/// <summary>Validates the fixed native set-1 storage-image descriptor budget before allocation or admission.</summary>
internal sealed partial class VulkanAdvancedVisibilityResourceRuntime
{
    internal const uint RequiredSet1StorageImages = 10u;

    internal static bool TryValidateSet1StorageImageLimits(VulkanDeviceContext device, out string reason)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.PhysicalDeviceCapabilities is not { } capabilities)
        {
            reason = "Advanced set-1 requires 10 storage images (depth pyramid=1, prior native outputs=5, DDGI exports=4), but Vulkan physical-device limits are unavailable.";
            return false;
        }

        uint perStage = capabilities.Properties.Limits.MaxPerStageDescriptorStorageImages;
        uint perSet = capabilities.Properties.Limits.MaxDescriptorSetStorageImages;
        if (perStage >= RequiredSet1StorageImages && perSet >= RequiredSet1StorageImages)
        {
            reason = "Ready";
            return true;
        }

        reason = $"Advanced set-1 requires {RequiredSet1StorageImages} storage images (depth pyramid=1, prior native outputs=5, DDGI exports=4); device reports MaxPerStageDescriptorStorageImages={perStage} and MaxDescriptorSetStorageImages={perSet}.";
        return false;
    }
}
