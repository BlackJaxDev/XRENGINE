namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One live-resource ownership group from the lifetime tracker: the native object
/// type, the registering owner label, how many tracked handles are not destroyed,
/// and how many of those are queued for retirement. Diagnostic only.
/// </summary>
public sealed record VulkanLiveResourceOwnerCount(
    string Type,
    string Owner,
    int Live,
    int PendingRetirement);
