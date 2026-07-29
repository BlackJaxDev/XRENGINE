namespace XREngine.Rendering.Vulkan;

internal readonly record struct CommandChainKey(
    int FrameSlot,
    RenderViewKey ViewKey,
    int PassIndex,
    int TargetIdentity,
    bool DynamicOverlay,
    int ChainOrdinal);
