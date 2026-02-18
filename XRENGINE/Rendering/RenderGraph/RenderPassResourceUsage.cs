namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Records how a pass touches a named logical resource (texture, buffer, etc.).
/// </summary>
public sealed class RenderPassResourceUsage(
    string resourceName,
    ERenderPassResourceType resourceType,
    ERenderGraphAccess access,
    ERenderPassLoadOp loadOp = ERenderPassLoadOp.Load,
    ERenderPassStoreOp storeOp = ERenderPassStoreOp.Store)
{
    public string ResourceName { get; } = resourceName;
    public ERenderPassResourceType ResourceType { get; } = resourceType;
    public ERenderGraphAccess Access { get; } = access;
    public ERenderPassLoadOp LoadOp { get; } = loadOp;
    public ERenderPassStoreOp StoreOp { get; } = storeOp;

    public bool IsAttachment => ResourceType 
        is ERenderPassResourceType.ColorAttachment 
        or ERenderPassResourceType.DepthAttachment 
        or ERenderPassResourceType.StencilAttachment 
        or ERenderPassResourceType.ResolveAttachment;
}
