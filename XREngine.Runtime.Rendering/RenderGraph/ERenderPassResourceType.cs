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
    TransferDestination,

    /// <summary>
    /// A transfer destination that addresses the depth aspect of an external output framebuffer.
    /// </summary>
    DepthTransferDestination,
}
