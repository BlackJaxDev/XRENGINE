namespace XREngine;

[Flags]
public enum ERvcFrameGraphUsage
{
    None = 0,
    DepthAttachment = 1 << 0,
    ColorAttachment = 1 << 1,
    StorageImage = 1 << 2,
    SampledTexture = 1 << 3,
    StorageBuffer = 1 << 4,
    IndirectBuffer = 1 << 5,
    TransferSource = 1 << 6,
    TransferDestination = 1 << 7,
    DelayedReadback = 1 << 8,
}
