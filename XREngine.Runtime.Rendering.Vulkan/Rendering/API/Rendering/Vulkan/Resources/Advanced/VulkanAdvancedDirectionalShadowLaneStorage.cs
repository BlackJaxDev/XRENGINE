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
    private readonly int[] _cascadeIndices = new int[MaxCascadeCount];
    private readonly DirectionalCascadeStaticTile?[] _staticTiles = new DirectionalCascadeStaticTile?[MaxCascadeCount];
    private readonly ulong[] _staticCasterEpochs = new ulong[MaxCascadeCount];
    private readonly EDirectionalShadowStaticCacheCause?[] _staticCacheCauses = new EDirectionalShadowStaticCacheCause?[MaxCascadeCount];
    private readonly bool[] _staticTilePrepared = new bool[MaxCascadeCount];
    private readonly ulong[] _staticRecordSetHashes = new ulong[MaxCascadeCount];
    private readonly int[] _staticRecordCounts = new int[MaxCascadeCount];
    private byte[] _recordMasks = [];
    private int _recordMaskCount;

    internal int CascadeCount { get; private set; }
    internal float DepthClearValue { get; private set; }
    internal bool ReversedDepth { get; private set; }
    internal uint CullingLayerMask { get; private set; }
    internal ulong RenderFrameId { get; private set; }
    internal XRFrameBuffer? Target { get; private set; }

    /// <summary>Static caster tile of each entry, or null (see AdvancedDirectionalShadowLaneRequest.StaticTiles).</summary>
    internal ReadOnlySpan<DirectionalCascadeStaticTile?> StaticTiles => _staticTiles.AsSpan(0, CascadeCount);
    /// <summary>Static caster epoch of each entry's cascade in this frame.</summary>
    internal ReadOnlySpan<ulong> StaticCasterEpochs => _staticCasterEpochs.AsSpan(0, CascadeCount);
    /// <summary>
    /// Reason why the static cache does not run for each entry, or null when it runs.
    /// Preparation can set it after the capture.
    /// </summary>
    internal ReadOnlySpan<EDirectionalShadowStaticCacheCause?> StaticCacheCauses => _staticCacheCauses.AsSpan(0, CascadeCount);
    /// <summary>64-bit order-independent hash of the static caster records of each entry's cascade.</summary>
    internal ReadOnlySpan<ulong> StaticRecordSetHashes => _staticRecordSetHashes.AsSpan(0, CascadeCount);
    /// <summary>Number of static caster records of each entry's cascade.</summary>
    internal ReadOnlySpan<int> StaticRecordCounts => _staticRecordCounts.AsSpan(0, CascadeCount);

    /// <summary>True when the static cache runs for entry <paramref name="entry"/> in this frame.</summary>
    internal bool UsesStaticCache(int entry)
        => (uint)entry < (uint)CascadeCount && _staticTiles[entry] is not null && _staticTilePrepared[entry];

    /// <summary>Inner tile rectangles (bottom-left origin, page pixels), one per cascade.</summary>
    internal ReadOnlySpan<BoundingRectangle> TileRects => _tileRects.AsSpan(0, CascadeCount);
    /// <summary>Cascade index (0 is the nearest cascade) of each entry.</summary>
    internal ReadOnlySpan<int> CascadeIndices => _cascadeIndices.AsSpan(0, CascadeCount);

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
            _cascadeIndices[index] = source.CascadeIndices[index];
        }

        CascadeCount = source.CascadeCount;
        DepthClearValue = source.DepthClearValue;
        ReversedDepth = source.ReversedDepth;
        CullingLayerMask = source.CullingLayerMask;
        RenderFrameId = renderFrameId;
        Target = source.PageFrameBuffer;
        source.StaticTiles.CopyTo(_staticTiles);
        source.StaticCasterEpochs.CopyTo(_staticCasterEpochs);
        source.StaticCacheCauses.CopyTo(_staticCacheCauses);
        failureReason = "Ready";
        return true;
    }

    /// <summary>
    /// Records that preparation found the static tile target of entry <paramref name="entry"/>
    /// ready, with the static caster record set of its cascade for this frame.
    /// </summary>
    internal void SetStaticTilePrepared(int entry, ulong recordSetHash, int recordCount)
    {
        _staticTilePrepared[entry] = true;
        _staticRecordSetHashes[entry] = recordSetHash;
        _staticRecordCounts[entry] = recordCount;
        _staticCacheCauses[entry] = null;
    }

    /// <summary>Records why the static cache cannot run for entry <paramref name="entry"/> in this frame.</summary>
    internal void BlockStaticCache(int entry, EDirectionalShadowStaticCacheCause cause)
    {
        _staticTilePrepared[entry] = false;
        _staticCacheCauses[entry] = cause;
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
        source.CascadeIndices.CopyTo(_cascadeIndices);
        source._staticTiles.AsSpan().CopyTo(_staticTiles);
        source._staticCasterEpochs.AsSpan().CopyTo(_staticCasterEpochs);
        source._staticCacheCauses.AsSpan().CopyTo(_staticCacheCauses);
        source._staticTilePrepared.AsSpan().CopyTo(_staticTilePrepared);
        source._staticRecordSetHashes.AsSpan().CopyTo(_staticRecordSetHashes);
        source._staticRecordCounts.AsSpan().CopyTo(_staticRecordCounts);
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
        Array.Clear(_staticTiles);
        Array.Clear(_staticCasterEpochs);
        Array.Clear(_staticCacheCauses);
        Array.Clear(_staticTilePrepared);
        Array.Clear(_staticRecordSetHashes);
        Array.Clear(_staticRecordCounts);
    }
}
