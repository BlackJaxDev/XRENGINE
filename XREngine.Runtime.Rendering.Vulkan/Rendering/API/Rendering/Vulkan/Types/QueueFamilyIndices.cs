namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable-after-selection identity of the queue families used by one Vulkan
/// device lifetime. The device context publishes this value with the selected
/// physical device rather than caching it on the renderer facade.
/// </summary>
public struct QueueFamilyIndices
{
    public uint? GraphicsFamilyIndex { get; set; }
    public uint? PresentFamilyIndex { get; set; }
    public uint? ComputeFamilyIndex { get; set; }
    public uint? TransferFamilyIndex { get; set; }

    /// <summary>
    /// Whether the selected graphics family can execute compute commands recorded
    /// into the primary graphics command stream.
    /// </summary>
    public bool GraphicsFamilySupportsCompute { get; set; }

    /// <summary>
    /// Whether the selected graphics family can execute transfer commands
    /// recorded into the primary graphics command stream.
    /// </summary>
    public bool GraphicsFamilySupportsTransfer { get; set; }

    public readonly bool IsComplete(bool requirePresentQueue = true)
        => GraphicsFamilyIndex.HasValue &&
            (!requirePresentQueue || PresentFamilyIndex.HasValue);
}
