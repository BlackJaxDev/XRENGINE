using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data.Geometry;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Backend-owned copy of one directional cascade group accepted for the
/// Advanced directional shadow lane: per-cascade native viewport, scissor and
/// row-vector world-to-clip matrix, plus the per-record cascade masks that
/// family preparation derives from the sealed bins. Instances live in a ring
/// on the frame loop and are stamped with the render frame that captured them,
/// so a stale slot can never be recorded for a later frame.
/// </summary>
internal sealed class VulkanAdvancedDirectionalShadowLaneStorage
{
    internal const int MaxCascadeCount = AdvancedDirectionalShadowLaneRequest.MaxCascadeCount;

    private readonly Matrix4x4[] _viewProjections = new Matrix4x4[MaxCascadeCount];
    private readonly Viewport[] _viewports = new Viewport[MaxCascadeCount];
    private readonly Rect2D[] _scissors = new Rect2D[MaxCascadeCount];
    private byte[] _recordMasks = [];
    private int _recordMaskCount;

    internal int CascadeCount { get; private set; }
    internal float DepthClearValue { get; private set; }
    internal bool ReversedDepth { get; private set; }
    internal ulong RenderFrameId { get; private set; }
    internal XRFrameBuffer? Target { get; private set; }

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
        }

        CascadeCount = source.CascadeCount;
        DepthClearValue = source.DepthClearValue;
        ReversedDepth = source.ReversedDepth;
        RenderFrameId = renderFrameId;
        Target = source.PageFrameBuffer;
        failureReason = "Ready";
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
        RenderFrameId = 0UL;
        Target = null;
        _recordMaskCount = 0;
    }
}
