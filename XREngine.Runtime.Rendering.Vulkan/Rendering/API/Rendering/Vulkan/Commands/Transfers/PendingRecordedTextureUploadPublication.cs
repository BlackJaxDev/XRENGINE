namespace XREngine.Rendering.Vulkan;

internal readonly record struct PendingRecordedTextureUploadPublication(
    VulkanImportedTexturePendingUpload Upload,
    ulong TimelineValue,
    string UploadSource);
