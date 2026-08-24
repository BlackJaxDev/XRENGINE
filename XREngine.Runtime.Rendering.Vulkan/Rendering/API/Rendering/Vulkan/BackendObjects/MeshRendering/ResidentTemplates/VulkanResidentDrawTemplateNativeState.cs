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
    private readonly VkBufferHandle[] _vertexBuffers;
    private readonly uint[] _vertexBindings;

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
        VertexBindingSignature = vertexBindingSignature;
        DrawTemplate = drawTemplate;
    }

    internal PipelineLayout PipelineLayout { get; }
    internal VulkanPreparedMeshPrimitive Primitive0 { get; }
    internal VulkanPreparedMeshPrimitive Primitive1 { get; }
    internal VulkanPreparedMeshPrimitive Primitive2 { get; }
    internal byte PrimitiveCount { get; }
    internal ReadOnlySpan<VkBufferHandle> VertexBuffers => _vertexBuffers;
    internal ReadOnlySpan<uint> VertexBindings => _vertexBindings;
    internal ulong VertexBindingSignature { get; }
    internal PendingMeshDraw DrawTemplate { get; }

    internal bool IsValid
        => PipelineLayout.Handle != 0 &&
           PrimitiveCount is > 0 and <= 3 &&
           _vertexBuffers is not null &&
           _vertexBindings is not null &&
           _vertexBuffers.Length == _vertexBindings.Length;

    internal VulkanPreparedMeshPrimitive GetPrimitive(int index)
        => index switch
        {
            0 when PrimitiveCount > 0 => Primitive0,
            1 when PrimitiveCount > 1 => Primitive1,
            2 when PrimitiveCount > 2 => Primitive2,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
}
