using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    /// <summary>
    /// Returns the number of exact vertex and index identities a recorded draw
    /// contributes after render preparation has built the binding state.
    /// </summary>
    /// <remarks>
    /// Packet-capacity planning only needs counts. It must not repeat native
    /// handle/generation capture for every candidate prefix because the complete
    /// identities are captured once when the final packet key is constructed.
    /// </remarks>
    internal void GetRecordedBufferBindingCounts(
        out int vertexCount,
        out int indexCount)
    {
        lock (_bufferStateSync)
        {
            vertexCount = _vertexBindings.Length;
            indexCount = 0;
            if (HasIndexData(_triangleIndexBuffer))
                indexCount++;
            if (HasIndexData(_lineIndexBuffer))
                indexCount++;
            if (HasIndexData(_pointIndexBuffer))
                indexCount++;
        }
    }

    /// <summary>
    /// Captures only the vertex and index bindings emitted by the draw recorder.
    /// Mesh dictionaries deliberately are not exposed: they also contain storage
    /// and deformation sources which are descriptor dependencies, not vertex binds.
    /// </summary>
    internal void CaptureRecordedBufferBindings(
        Span<VulkanRecordedBufferIdentity> vertexBindings,
        out int vertexCount,
        out bool vertexComplete,
        Span<VulkanRecordedBufferIdentity> indexBindings,
        out int indexCount,
        out bool indexComplete)
    {
        lock (_bufferStateSync)
        {
            EnsureBuffers();
            BuildVertexInputState();

            vertexCount = 0;
            vertexComplete = true;
            for (int i = 0; i < _vertexBindings.Length; i++)
            {
                uint binding = _vertexBindings[i].Binding;
                if (!_vertexBuffersByBinding.TryGetValue(binding, out VkDataBuffer? source) ||
                    source.BufferHandle is not { Handle: not 0 } handle)
                {
                    vertexComplete = false;
                    continue;
                }

                if (vertexCount >= vertexBindings.Length)
                {
                    vertexComplete = false;
                    continue;
                }

                ulong generation = GetResourceGeneration(
                    ObjectType.Buffer,
                    handle.Handle);
                vertexBindings[vertexCount++] = new VulkanRecordedBufferIdentity(
                    EVulkanRecordedBufferBindingKind.Vertex,
                    binding,
                    handle.Handle,
                    generation,
                    Offset: 0UL,
                    Range: source.AllocatedByteSize);
                vertexComplete &= generation != 0UL && source.AllocatedByteSize != 0UL;
            }

            int capturedIndexCount = 0;
            bool capturedIndicesComplete = true;
            AppendRecordedIndexBinding(
                _triangleIndexBuffer,
                0u,
                indexBindings,
                ref capturedIndexCount,
                ref capturedIndicesComplete);
            AppendRecordedIndexBinding(
                _lineIndexBuffer,
                1u,
                indexBindings,
                ref capturedIndexCount,
                ref capturedIndicesComplete);
            AppendRecordedIndexBinding(
                _pointIndexBuffer,
                2u,
                indexBindings,
                ref capturedIndexCount,
                ref capturedIndicesComplete);
            indexCount = capturedIndexCount;
            indexComplete = capturedIndicesComplete;
        }
    }

    /// <summary>
    /// Describes why the current vertex bindings cannot form an exact recorded
    /// identity. This intentionally performs detailed inspection only after a
    /// diagnostic capture has already failed.
    /// </summary>
    internal string DescribeRecordedVertexBindingCapture()
    {
        lock (_bufferStateSync)
        {
            EnsureBuffers();
            BuildVertexInputState();

            if (_vertexBindings.Length > VulkanRecordedBufferIdentityBuffer.Capacity)
            {
                return $"per-draw binding count {_vertexBindings.Length} exceeds identity capacity " +
                    VulkanRecordedBufferIdentityBuffer.Capacity;
            }

            for (int i = 0; i < _vertexBindings.Length; i++)
            {
                uint binding = _vertexBindings[i].Binding;
                if (!_vertexBuffersByBinding.TryGetValue(binding, out VkDataBuffer? source))
                    return $"binding {binding} has no backing buffer";
                if (source.BufferHandle is not { Handle: not 0 } handle)
                    return $"binding {binding} has no native buffer handle";

                ulong generation = GetResourceGeneration(ObjectType.Buffer, handle.Handle);
                if (generation == 0UL)
                    return $"binding {binding} buffer 0x{handle.Handle:X} has no lifetime generation";
                if (source.AllocatedByteSize == 0UL)
                    return $"binding {binding} buffer 0x{handle.Handle:X} has an empty allocation range";
            }

            return $"all {_vertexBindings.Length} per-draw bindings are complete; the packet aggregate exceeded capacity";
        }
    }

    private void AppendRecordedIndexBinding(
        VkDataBuffer? source,
        uint binding,
        Span<VulkanRecordedBufferIdentity> indexBindings,
        ref int indexCount,
        ref bool indexComplete)
    {
        if (!HasIndexData(source))
            return;

        if (source?.BufferHandle is not { Handle: not 0 } handle ||
            indexCount >= indexBindings.Length)
        {
            indexComplete = false;
            return;
        }

        ulong generation = GetResourceGeneration(
            ObjectType.Buffer,
            handle.Handle);
        indexBindings[indexCount++] = new VulkanRecordedBufferIdentity(
            EVulkanRecordedBufferBindingKind.Index,
            binding,
            handle.Handle,
            generation,
            Offset: 0UL,
            Range: source.AllocatedByteSize);
        indexComplete &= generation != 0UL && source.AllocatedByteSize != 0UL;
    }
}
