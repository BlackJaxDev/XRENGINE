namespace XREngine.Rendering.Vulkan;

internal readonly record struct CommandChainKey(
    int FrameSlot,
    RenderViewKey ViewKey,
    int PassIndex,
    int TargetIdentity,
    ulong DescriptorBindingVariant,
    bool DynamicOverlay,
    int ChainOrdinal);
