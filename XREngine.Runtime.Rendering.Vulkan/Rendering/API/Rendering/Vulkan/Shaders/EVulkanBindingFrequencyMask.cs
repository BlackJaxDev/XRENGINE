namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compact set of binding-frequency owners referenced by a published payload.
/// Bit positions deliberately match the non-zero
/// <see cref="EVulkanBindingFrequency"/> values.
/// </summary>
[Flags]
internal enum EVulkanBindingFrequencyMask : byte
{
    None = 0,
    Frame = 1 << 0,
    View = 1 << 1,
    Pass = 1 << 2,
    Material = 1 << 3,
    Object = 1 << 4,
    Instance = 1 << 5,
    RuntimeCallback = 1 << 6,
    All = Frame |
        View |
        Pass |
        Material |
        Object |
        Instance |
        RuntimeCallback,
}
