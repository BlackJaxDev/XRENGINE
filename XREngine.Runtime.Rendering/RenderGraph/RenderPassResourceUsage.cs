namespace XREngine.Rendering.RenderGraph;

public readonly record struct RenderGraphSubresourceRange(
    uint BaseMipLevel,
    uint MipLevelCount,
    uint BaseArrayLayer,
    uint ArrayLayerCount)
{
    public const uint Remaining = uint.MaxValue;

    public static RenderGraphSubresourceRange Full { get; } = new(0u, Remaining, 0u, Remaining);

    public bool IsWholeResource
        => BaseMipLevel == 0u &&
           MipLevelCount == Remaining &&
           BaseArrayLayer == 0u &&
           ArrayLayerCount == Remaining;
}

/// <summary>
/// Records how a pass touches a named logical resource (texture, buffer, etc.).
/// </summary>
public sealed class RenderPassResourceUsage
{
    public RenderPassResourceUsage(
        string resourceName,
        ERenderPassResourceType resourceType,
        ERenderGraphAccess access,
        ERenderPassLoadOp loadOp = ERenderPassLoadOp.Load,
        ERenderPassStoreOp storeOp = ERenderPassStoreOp.Store,
        RenderGraphSubresourceRange? subresourceRange = null)
    {
        ResourceName = resourceName;
        ResourceType = resourceType;
        Access = access;
        LoadOp = loadOp;
        StoreOp = storeOp;
        SubresourceRange = Normalize(subresourceRange ?? RenderGraphSubresourceRange.Full);
    }

    public string ResourceName { get; }
    public ERenderPassResourceType ResourceType { get; }
    public ERenderGraphAccess Access { get; }
    public ERenderPassLoadOp LoadOp { get; }
    public ERenderPassStoreOp StoreOp { get; }
    public RenderGraphSubresourceRange SubresourceRange { get; }

    public bool IsAttachment => ResourceType
        is ERenderPassResourceType.ColorAttachment
        or ERenderPassResourceType.DepthAttachment
        or ERenderPassResourceType.StencilAttachment
        or ERenderPassResourceType.ResolveAttachment;

    private static RenderGraphSubresourceRange Normalize(RenderGraphSubresourceRange range)
        => range with
        {
            MipLevelCount = range.MipLevelCount == 0u ? 1u : range.MipLevelCount,
            ArrayLayerCount = range.ArrayLayerCount == 0u ? 1u : range.ArrayLayerCount
        };
}
