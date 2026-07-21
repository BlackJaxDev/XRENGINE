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

    public bool IsValid
        => IsCountValid(BaseMipLevel, MipLevelCount) &&
           IsCountValid(BaseArrayLayer, ArrayLayerCount);

    public bool Overlaps(in RenderGraphSubresourceRange other)
        => IntervalsOverlap(BaseMipLevel, MipLevelCount, other.BaseMipLevel, other.MipLevelCount) &&
           IntervalsOverlap(BaseArrayLayer, ArrayLayerCount, other.BaseArrayLayer, other.ArrayLayerCount);

    private static bool IsCountValid(uint start, uint count)
        => count != 0u &&
           (count == Remaining || start <= uint.MaxValue - count);

    private static bool IntervalsOverlap(uint firstStart, uint firstCount, uint secondStart, uint secondCount)
    {
        ulong firstEnd = firstCount == Remaining ? ulong.MaxValue : (ulong)firstStart + firstCount;
        ulong secondEnd = secondCount == Remaining ? ulong.MaxValue : (ulong)secondStart + secondCount;
        return firstStart < secondEnd && secondStart < firstEnd;
    }
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
        RenderGraphSubresourceRange? subresourceRange = null,
        uint? resolveSourceColorIndex = null,
        int logicalVersion = -1,
        bool imported = false,
        RenderGraphSyncState? importedInitialState = null)
    {
        ResourceName = resourceName;
        ResourceType = resourceType;
        Access = access;
        LoadOp = loadOp;
        StoreOp = storeOp;
        SubresourceRange = Normalize(subresourceRange ?? RenderGraphSubresourceRange.Full);
        ResolveSourceColorIndex = resolveSourceColorIndex;
        LogicalVersion = logicalVersion;
        IsImported = imported;
        ImportedInitialState = importedInitialState;
    }

    public string ResourceName { get; }
    public ERenderPassResourceType ResourceType { get; }
    public ERenderGraphAccess Access { get; }
    public ERenderPassLoadOp LoadOp { get; }
    public ERenderPassStoreOp StoreOp { get; }
    public RenderGraphSubresourceRange SubresourceRange { get; }
    public uint? ResolveSourceColorIndex { get; }
    /// <summary>
    /// Gets the explicit logical resource version consumed or produced by this use.
    /// A negative value opts into legacy declaration-ordered hazard tracking.
    /// </summary>
    public int LogicalVersion { get; }
    /// <summary>Gets whether this use imports an externally initialized version.</summary>
    public bool IsImported { get; }
    /// <summary>Gets the synchronization state supplied by the external owner.</summary>
    public RenderGraphSyncState? ImportedInitialState { get; }

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
