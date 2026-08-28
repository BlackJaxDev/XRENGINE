using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable cross-frame native state retained by one resident draw template.
/// Frame-local prepared-stream ranges are deliberately absent; each hit copies
/// these stable bindings into the current frame slot before sealing.
/// </summary>
internal readonly struct VulkanResidentDrawTemplateNativeState
{
    private readonly VkBufferHandle[]? _vertexBuffers;
    private readonly uint[]? _vertexBindings;
    private readonly VkBufferHandle _inlineVertexBuffer0;
    private readonly VkBufferHandle _inlineVertexBuffer1;
    private readonly uint _inlineVertexBinding0;
    private readonly uint _inlineVertexBinding1;
    private readonly byte _inlineVertexBufferCount;

    internal VulkanResidentDrawTemplateNativeState(
        PipelineLayout pipelineLayout,
        in VulkanPreparedMeshPrimitive primitive0,
        in VulkanPreparedMeshPrimitive primitive1,
        in VulkanPreparedMeshPrimitive primitive2,
        byte primitiveCount,
        ReadOnlySpan<VkBufferHandle> vertexBuffers,
        ReadOnlySpan<uint> vertexBindings,
        ulong vertexBindingSignature,
        in PendingMeshDraw drawTemplate)
    {
        if (vertexBuffers.Length != vertexBindings.Length)
            throw new ArgumentException(
                "Resident vertex buffer and binding snapshots must have the same length.");

        PipelineLayout = pipelineLayout;
        Primitive0 = primitive0;
        Primitive1 = primitive1;
        Primitive2 = primitive2;
        PrimitiveCount = primitiveCount;
        _vertexBuffers = vertexBuffers.ToArray();
        _vertexBindings = vertexBindings.ToArray();
        _inlineVertexBuffer0 = default;
        _inlineVertexBuffer1 = default;
        _inlineVertexBinding0 = 0u;
        _inlineVertexBinding1 = 0u;
        _inlineVertexBufferCount = 0;
        VertexBindingSignature = vertexBindingSignature;
        DrawTemplate = drawTemplate;
    }

    /// <summary>
    /// Allocation-free fixed one-binding closure used by canonical packed
    /// visibility geometry.
    /// </summary>
    internal VulkanResidentDrawTemplateNativeState(
        PipelineLayout pipelineLayout,
        in VulkanPreparedMeshPrimitive primitive0,
        VkBufferHandle vertexBuffer0,
        uint vertexBinding0,
        ulong vertexBindingSignature,
        in PendingMeshDraw drawTemplate)
    {
        PipelineLayout = pipelineLayout;
        Primitive0 = primitive0;
        Primitive1 = default;
        Primitive2 = default;
        PrimitiveCount = 1;
        _vertexBuffers = null;
        _vertexBindings = null;
        _inlineVertexBuffer0 = vertexBuffer0;
        _inlineVertexBuffer1 = default;
        _inlineVertexBinding0 = vertexBinding0;
        _inlineVertexBinding1 = 0u;
        _inlineVertexBufferCount = 1;
        VertexBindingSignature = vertexBindingSignature;
        DrawTemplate = drawTemplate;
    }

    /// <summary>Allocation-free fixed two-binding native closure.</summary>
    internal VulkanResidentDrawTemplateNativeState(
        PipelineLayout pipelineLayout,
        in VulkanPreparedMeshPrimitive primitive0,
        VkBufferHandle vertexBuffer0,
        uint vertexBinding0,
        VkBufferHandle vertexBuffer1,
        uint vertexBinding1,
        ulong vertexBindingSignature,
        in PendingMeshDraw drawTemplate)
    {
        PipelineLayout = pipelineLayout;
        Primitive0 = primitive0;
        Primitive1 = default;
        Primitive2 = default;
        PrimitiveCount = 1;
        _vertexBuffers = null;
        _vertexBindings = null;
        _inlineVertexBuffer0 = vertexBuffer0;
        _inlineVertexBuffer1 = vertexBuffer1;
        _inlineVertexBinding0 = vertexBinding0;
        _inlineVertexBinding1 = vertexBinding1;
        _inlineVertexBufferCount = 2;
        VertexBindingSignature = vertexBindingSignature;
        DrawTemplate = drawTemplate;
    }

    internal PipelineLayout PipelineLayout { get; }
    internal VulkanPreparedMeshPrimitive Primitive0 { get; }
    internal VulkanPreparedMeshPrimitive Primitive1 { get; }
    internal VulkanPreparedMeshPrimitive Primitive2 { get; }
    internal byte PrimitiveCount { get; }
    internal ReadOnlySpan<VkBufferHandle> VertexBuffers
        => _vertexBuffers ?? ReadOnlySpan<VkBufferHandle>.Empty;
    internal ReadOnlySpan<uint> VertexBindings
        => _vertexBindings ?? ReadOnlySpan<uint>.Empty;
    internal int VertexBufferCount
        => _inlineVertexBufferCount != 0
            ? _inlineVertexBufferCount
            : _vertexBuffers?.Length ?? 0;
    internal ulong VertexBindingSignature { get; }
    internal PendingMeshDraw DrawTemplate { get; }

    internal bool IsValid
        => PipelineLayout.Handle != 0 &&
           PrimitiveCount is > 0 and <= 3 &&
           VertexBufferCount > 0 &&
           (_inlineVertexBufferCount != 0 ||
            _vertexBuffers!.Length == _vertexBindings!.Length);

    internal VkBufferHandle GetVertexBuffer(int index)
        => _inlineVertexBufferCount != 0
            ? index switch
            {
                0 => _inlineVertexBuffer0,
                1 => _inlineVertexBuffer1,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            }
            : _vertexBuffers![index];

    internal uint GetVertexBinding(int index)
        => _inlineVertexBufferCount != 0
            ? index switch
            {
                0 => _inlineVertexBinding0,
                1 => _inlineVertexBinding1,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            }
            : _vertexBindings![index];

    internal VulkanPreparedMeshPrimitive GetPrimitive(int index)
        => index switch
        {
            0 when PrimitiveCount > 0 => Primitive0,
            1 when PrimitiveCount > 1 => Primitive1,
            2 when PrimitiveCount > 2 => Primitive2,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
}
