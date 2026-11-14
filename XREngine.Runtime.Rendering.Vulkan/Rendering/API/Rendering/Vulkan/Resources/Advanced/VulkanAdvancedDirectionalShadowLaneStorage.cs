using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data.Geometry;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Backend-owned copy of one directional cascade group accepted for the
/// Advanced directional shadow lane: per-cascade native viewport, scissor and
/// row-vector world-to-clip matrix, plus optional CPU-direct record masks.
/// The physical operation copies the queue ring data before recording.
/// </summary>
internal sealed class VulkanAdvancedDirectionalShadowLaneStorage
{
    internal const int MaxCascadeCount = AdvancedDirectionalShadowLaneRequest.MaxCascadeCount;

    private readonly Matrix4x4[] _viewProjections = new Matrix4x4[MaxCascadeCount];
    private readonly Viewport[] _viewports = new Viewport[MaxCascadeCount];
    private readonly Rect2D[] _scissors = new Rect2D[MaxCascadeCount];
    private readonly BoundingRectangle[] _tileRects = new BoundingRectangle[MaxCascadeCount];
    private byte[] _recordMasks = [];
    private int _recordMaskCount;

    internal int CascadeCount { get; private set; }
    internal float DepthClearValue { get; private set; }
    internal bool ReversedDepth { get; private set; }
    internal uint CullingLayerMask { get; private set; }
    internal ulong RenderFrameId { get; private set; }
    internal XRFrameBuffer? Target { get; private set; }

    /// <summary>Static caster tile of cascade 0, or null (see AdvancedDirectionalShadowLaneRequest.StaticTile).</summary>
    internal DirectionalCascadeStaticTile? StaticTile { get; private set; }
    /// <summary>Entry index of cascade 0 when <see cref="StaticTile"/> is set; otherwise -1.</summary>
    internal int StaticCacheEntry { get; private set; } = -1;
    /// <summary>Static caster epoch of cascade 0 in this frame.</summary>
    internal ulong StaticCasterEpoch { get; private set; }
    /// <summary>
    /// Reason why the static cache does not run for this group, or null when it runs (or the
    /// group does not draw cascade 0). Preparation can set it after the capture.
    /// </summary>
    internal EDirectionalShadowStaticCacheCause? StaticCacheCause { get; private set; }
    /// <summary>True after preparation found the static tile target ready for this frame.</summary>
    internal bool StaticTilePrepared { get; private set; }
    /// <summary>64-bit order-independent hash of the static caster records of cascade 0.</summary>
    internal ulong StaticRecordSetHash { get; private set; }
    /// <summary>Number of static caster records of cascade 0.</summary>
    internal int StaticRecordCount { get; private set; }
    /// <summary>True when the static cache runs for this group in this frame.</summary>
    internal bool UsesStaticCache => StaticTile is not null && StaticTilePrepared && StaticCacheEntry >= 0;
    /// <summary>Inner tile rectangles (bottom-left origin, page pixels), one per cascade.</summary>
    internal ReadOnlySpan<BoundingRectangle> TileRects => _tileRects.AsSpan(0, CascadeCount);

    internal ReadOnlySpan<Matrix4x4> ViewProjections => _viewProjections.AsSpan(0, CascadeCount);
    internal ReadOnlySpan<Viewport> Viewports => _viewports.AsSpan(0, CascadeCount);
    internal ReadOnlySpan<Rect2D> Scissors => _scissors.AsSpan(0, CascadeCount);
    internal ReadOnlySpan<byte> RecordMasks => _recordMasks.AsSpan(0, _recordMaskCount);
    internal int RecordMaskCount => _recordMaskCount;
    internal bool HasRecordMasks => _recordMaskCount > 0;

    /// <summary>
    /// Copies the atlas manager's request into native terms. Tile rectangles
    /// use the same bottom-left-origin conversion as the generic layered pass
    /// so both paths rasterize into identical page pixels.
    /// </summary>
    internal bool TryCapture(
        AdvancedDirectionalShadowLaneRequest source,
        ulong renderFrameId,
        Extent2D pageExtent,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(source);
        Clear();
        if (source.PageFrameBuffer is null || source.CascadeCount <= 0 ||
            pageExtent.Width == 0u || pageExtent.Height == 0u)
        {
            failureReason = "The directional cascade group has no atlas page or cascades.";
            return false;
        }

        ReadOnlySpan<BoundingRectangle> rects = source.TileRects;
        ReadOnlySpan<Matrix4x4> matrices = source.ViewProjections;
        for (int index = 0; index < source.CascadeCount; index++)
        {
            Rect2D scissor = VulkanStateTracker.CreateVulkanScissor(rects[index], pageExtent);
            if (scissor.Extent.Width == 0u || scissor.Extent.Height == 0u)
            {
                Clear();
                failureReason = "A cascade tile rectangle lies outside its atlas page.";
                return false;
            }

            _scissors[index] = scissor;
            _viewports[index] = VulkanCommandRuntime.CreateVulkanViewport(rects[index], pageExtent);
            _viewProjections[index] = matrices[index];
            _tileRects[index] = rects[index];
        }

        CascadeCount = source.CascadeCount;
        DepthClearValue = source.DepthClearValue;
        ReversedDepth = source.ReversedDepth;
        CullingLayerMask = source.CullingLayerMask;
        RenderFrameId = renderFrameId;
        Target = source.PageFrameBuffer;
        StaticTile = source.StaticTile;
        StaticCacheEntry = source.StaticTile is not null ? source.StaticCacheEntry : -1;
        StaticCasterEpoch = source.StaticCasterEpoch;
        StaticCacheCause = source.StaticCacheCause;
        failureReason = "Ready";
        return true;
    }

    /// <summary>
    /// Records that preparation found the static tile target ready, with the static caster
    /// record set of cascade 0 for this frame.
    /// </summary>
    internal void SetStaticTilePrepared(ulong recordSetHash, int recordCount)
    {
        StaticTilePrepared = true;
        StaticRecordSetHash = recordSetHash;
        StaticRecordCount = recordCount;
        StaticCacheCause = null;
    }

    /// <summary>Records why the static cache cannot run for this group in this frame.</summary>
    internal void BlockStaticCache(EDirectionalShadowStaticCacheCause cause)
    {
        StaticTilePrepared = false;
        StaticCacheCause = cause;
    }

    /// <summary>Copies one sealed lane without retaining the mutable queue ring slot.</summary>
    internal bool TryCopyFrom(VulkanAdvancedDirectionalShadowLaneStorage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Clear();
        if (source.CascadeCount <= 0 || source.CascadeCount > MaxCascadeCount ||
            source.Target is null || source.RenderFrameId == 0UL)
            return false;
        source.ViewProjections.CopyTo(_viewProjections);
        source.Viewports.CopyTo(_viewports);
        source.Scissors.CopyTo(_scissors);
        source.TileRects.CopyTo(_tileRects);
        StaticTile = source.StaticTile;
        StaticCacheEntry = source.StaticCacheEntry;
        StaticCasterEpoch = source.StaticCasterEpoch;
        StaticCacheCause = source.StaticCacheCause;
        StaticTilePrepared = source.StaticTilePrepared;
        StaticRecordSetHash = source.StaticRecordSetHash;
        StaticRecordCount = source.StaticRecordCount;
        CascadeCount = source.CascadeCount;
        DepthClearValue = source.DepthClearValue;
        ReversedDepth = source.ReversedDepth;
        CullingLayerMask = source.CullingLayerMask;
        RenderFrameId = source.RenderFrameId;
        Target = source.Target;
        return true;
    }

    /// <summary>
    /// Returns writable mask storage for one record per sealed bin record. The
    /// array grows only when the family's record capacity grows.
    /// </summary>
    internal Span<byte> AcquireRecordMasks(int recordCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        if (_recordMasks.Length < recordCount)
            _recordMasks = new byte[Math.Max(recordCount, _recordMasks.Length * 2)];
        _recordMaskCount = recordCount;
        return _recordMasks.AsSpan(0, recordCount);
    }

    internal void Clear()
    {
        CascadeCount = 0;
        DepthClearValue = 1.0f;
        ReversedDepth = false;
        CullingLayerMask = 0u;
        RenderFrameId = 0UL;
        Target = null;
        _recordMaskCount = 0;
        StaticTile = null;
        StaticCacheEntry = -1;
        StaticCasterEpoch = 0UL;
        StaticCacheCause = null;
        StaticTilePrepared = false;
        StaticRecordSetHash = 0UL;
        StaticRecordCount = 0;
    }
}
