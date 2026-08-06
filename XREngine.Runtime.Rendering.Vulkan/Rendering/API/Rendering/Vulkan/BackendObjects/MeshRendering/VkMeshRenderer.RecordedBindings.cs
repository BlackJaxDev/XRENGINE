using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
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

                ulong generation = Renderer.GetCurrentVulkanResourceGeneration(
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

        ulong generation = Renderer.GetCurrentVulkanResourceGeneration(
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
