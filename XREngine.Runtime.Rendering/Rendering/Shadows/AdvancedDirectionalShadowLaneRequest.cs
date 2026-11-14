using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// One directional cascade group handed from the shadow atlas manager to the
/// Advanced directional shadow raster stage. The atlas manager owns and pools
/// the instances; a backend copies the content when it enqueues the stage and
/// never retains the instance across frames.
/// </summary>
public sealed class AdvancedDirectionalShadowLaneRequest
{
    /// <summary>Upper bound on cascades per group, matching the directional cascade limit.</summary>
    public const int MaxCascadeCount = 8;

    private readonly Matrix4x4[] _viewProjections = new Matrix4x4[MaxCascadeCount];
    private readonly BoundingRectangle[] _tileRects = new BoundingRectangle[MaxCascadeCount];
    private readonly int[] _cascadeIndices = new int[MaxCascadeCount];

    /// <summary>Depth-only atlas page the cascades render into.</summary>
    public XRFrameBuffer? PageFrameBuffer { get; private set; }

    /// <summary>Number of valid cascade entries.</summary>
    public int CascadeCount { get; private set; }

    /// <summary>Depth convention of the cascade projections (shadow cameras use normal depth).</summary>
    public bool ReversedDepth { get; private set; }

    /// <summary>Depth value each tile is cleared to before its casters are drawn.</summary>
    public float DepthClearValue { get; private set; }

    /// <summary>Effective layer mask of the grouped cascade collection camera.</summary>
    public uint CullingLayerMask { get; private set; }

    /// <summary>Render frame that scheduled the group.</summary>
    public ulong RenderFrameId { get; private set; }

    /// <summary>Owning light, for diagnostics.</summary>
    public Guid LightId { get; private set; }

    /// <summary>World-to-clip matrices in row-vector convention, one per cascade.</summary>
    public ReadOnlySpan<Matrix4x4> ViewProjections => _viewProjections.AsSpan(0, CascadeCount);

    /// <summary>Inner atlas tile rectangles (bottom-left origin, page pixels), one per cascade.</summary>
    public ReadOnlySpan<BoundingRectangle> TileRects => _tileRects.AsSpan(0, CascadeCount);

    /// <summary>Cascade index (0 is the nearest cascade) of each entry.</summary>
    public ReadOnlySpan<int> CascadeIndices => _cascadeIndices.AsSpan(0, CascadeCount);

    /// <summary>
    /// Static caster tile of cascade 0, or null when the static cache does not apply to this
    /// group. See <see cref="StaticCacheEntry"/> and <see cref="StaticCacheCause"/>.
    /// </summary>
    public DirectionalCascadeStaticTile? StaticTile { get; private set; }

    /// <summary>Entry index of cascade 0 when <see cref="StaticTile"/> is set; otherwise -1.</summary>
    public int StaticCacheEntry { get; private set; } = -1;

    /// <summary>Static caster epoch of cascade 0 in this frame (see DirectionalLightComponent.MarkDirectionalCascadeCasterChanges).</summary>
    public ulong StaticCasterEpoch { get; private set; }

    /// <summary>
    /// Why the static cache does not apply to this group, when the group draws cascade 0 and
    /// <see cref="StaticTile"/> is null. The lane counts it.
    /// </summary>
    public EDirectionalShadowStaticCacheCause? StaticCacheCause { get; private set; }

    /// <summary>Atlas-manager bookkeeping slot; not meaningful to backends.</summary>
    internal int PendingSlot { get; set; } = -1;
    internal bool RequiresStrictGpu { get; set; }
    internal AdvancedDirectionalShadowConsumerAuthority ConsumerAuthority { get; set; }

    internal void Reset(
        XRFrameBuffer pageFrameBuffer,
        Guid lightId,
        ulong renderFrameId,
        bool reversedDepth,
        float depthClearValue,
        uint cullingLayerMask)
    {
        PageFrameBuffer = pageFrameBuffer;
        LightId = lightId;
        RenderFrameId = renderFrameId;
        ReversedDepth = reversedDepth;
        DepthClearValue = depthClearValue;
        CullingLayerMask = cullingLayerMask;
        CascadeCount = 0;
        ClearStaticCache();
    }

    /// <summary>Attaches the static caster tile of cascade 0 (entry <paramref name="entry"/>) to the group.</summary>
    internal void SetStaticCache(DirectionalCascadeStaticTile tile, int entry, ulong staticCasterEpoch)
    {
        StaticTile = tile;
        StaticCacheEntry = entry;
        StaticCasterEpoch = staticCasterEpoch;
        StaticCacheCause = null;
    }

    /// <summary>Records why the static cache does not apply to a group that draws cascade 0.</summary>
    internal void SetStaticCacheCause(EDirectionalShadowStaticCacheCause cause)
    {
        ClearStaticCache();
        StaticCacheCause = cause;
    }

    private void ClearStaticCache()
    {
        StaticTile = null;
        StaticCacheEntry = -1;
        StaticCasterEpoch = 0UL;
        StaticCacheCause = null;
    }

    internal void Clear()
    {
        PageFrameBuffer = null;
        CascadeCount = 0;
        CullingLayerMask = 0u;
        PendingSlot = -1;
        RequiresStrictGpu = false;
        ConsumerAuthority = default;
        ClearStaticCache();
    }

    internal bool TryAddCascade(in BoundingRectangle tileRect, in Matrix4x4 viewProjection, int cascadeIndex)
    {
        if (CascadeCount >= MaxCascadeCount || tileRect.Width <= 0 || tileRect.Height <= 0)
            return false;

        _tileRects[CascadeCount] = tileRect;
        _viewProjections[CascadeCount] = viewProjection;
        _cascadeIndices[CascadeCount] = cascadeIndex;
        CascadeCount++;
        return true;
    }
}
