namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Describes the role a logical resource plays inside a render pass.
/// </summary>
public enum ERenderPassResourceType
{
    ColorAttachment,
    DepthAttachment,
    StencilAttachment,
    ResolveAttachment,
    SampledTexture,
    StorageTexture,
    UniformBuffer,
    StorageBuffer,
    VertexBuffer,
    IndexBuffer,
    IndirectBuffer,
    TransferSource,
    TransferDestination
}
