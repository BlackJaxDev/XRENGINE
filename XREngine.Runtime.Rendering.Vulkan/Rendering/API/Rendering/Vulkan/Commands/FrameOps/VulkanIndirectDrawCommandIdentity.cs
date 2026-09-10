using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Exact dynamic state and frame-data epoch encoded by one indirect secondary.</summary>
internal struct VulkanIndirectDrawCommandIdentity
{
    private VulkanIndirectViewportScissorIdentityBuffer _views;
    private int _count;
    private IndexType _indexType;
    private ulong _frameDataGeneration;
    private uint _drawCount;
    private uint _stride;
    private bool _useCount;
    private nuint _byteOffset;
    private nuint _countByteOffset;
    internal readonly bool IsComplete => _count > 0;

    internal static VulkanIndirectDrawCommandIdentity Capture(
        in IndirectDrawPayload indirect, IndexType indexType, ulong frameDataGeneration)
    {
        VulkanIndirectDrawCommandIdentity result = default;
        PendingMeshDraw draw = indirect.Draw;
        // Match the encoder's effective single-view fallback exactly.
        bool indexed = draw.ViewportScissorCount > 1 &&
            draw.IndexedViewports is { } viewports && draw.IndexedScissors is { } scissors &&
            viewports.Length >= draw.ViewportScissorCount && scissors.Length >= draw.ViewportScissorCount;
        uint count = indexed ? draw.ViewportScissorCount : 1u;
        if (count > VulkanIndirectViewportScissorIdentityBuffer.Capacity)
            return result;
        result._count = (int)count;
        result._indexType = indexType;
        result._frameDataGeneration = frameDataGeneration;
        result._drawCount = indirect.DrawCount;
        result._stride = indirect.Stride;
        result._useCount = indirect.UseCount;
        result._byteOffset = indirect.ByteOffset;
        result._countByteOffset = indirect.CountByteOffset;
        for (int i = 0; i < result._count; i++)
            result._views[i] = indexed
                ? VulkanIndirectViewportScissorIdentity.Capture(draw.IndexedViewports![i], draw.IndexedScissors![i])
                : VulkanIndirectViewportScissorIdentity.Capture(draw.Viewport, draw.Scissor);
        return result;
    }

    internal readonly bool Matches(in VulkanIndirectDrawCommandIdentity other)
    {
        if (_count != other._count || _indexType != other._indexType ||
            _frameDataGeneration != other._frameDataGeneration ||
            _drawCount != other._drawCount || _stride != other._stride || _useCount != other._useCount ||
            _byteOffset != other._byteOffset || _countByteOffset != other._countByteOffset)
            return false;
        for (int i = 0; i < _count; i++)
            if (_views[i] != other._views[i])
                return false;
        return true;
    }
}
