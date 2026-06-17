namespace XREngine.Rendering.Resources;

[Flags]
public enum RenderPipelineResourceUsage
{
    None = 0,
    SampledTexture = 1 << 0,
    ColorAttachment = 1 << 1,
    DepthStencilAttachment = 1 << 2,
    StorageImage = 1 << 3,
    TransferSource = 1 << 4,
    TransferDestination = 1 << 5,
    UniformBuffer = 1 << 6,
    StorageBuffer = 1 << 7,
    VertexBuffer = 1 << 8,
    IndexBuffer = 1 << 9,
    IndirectBuffer = 1 << 10,
    PresentSource = 1 << 11,
}
