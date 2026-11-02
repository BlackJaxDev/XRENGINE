using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    /// <summary>
    /// Vertex input built for one linked program over the current buffer collection.
    /// Attribute locations depend only on the program's reflected inputs, so a renderer
    /// that alternates between pass variants can restore this state without rebuilding it.
    /// </summary>
    private sealed class CachedVertexInputState(
        ulong linkGeneration,
        VertexInputBindingDescription[] bindings,
        VertexInputAttributeDescription[] attributes,
        MeshGeometryLayoutSignature geometryLayoutSignature,
        KeyValuePair<uint, VkDataBuffer>[] buffersByBinding)
    {
        public ulong LinkGeneration { get; } = linkGeneration;
        public VertexInputBindingDescription[] Bindings { get; } = bindings;
        public VertexInputAttributeDescription[] Attributes { get; } = attributes;
        public MeshGeometryLayoutSignature GeometryLayoutSignature { get; } = geometryLayoutSignature;
        public KeyValuePair<uint, VkDataBuffer>[] BuffersByBinding { get; } = buffersByBinding;
    }

    private readonly Dictionary<VkRenderProgram, CachedVertexInputState> _vertexInputStatesByProgram = [];

    /// <summary>
    /// Marks the vertex input state stale after the buffer collection changed. A program
    /// switch sets the dirty flag directly, because the cached states stay valid for it.
    /// Requires <see cref="_bufferStateSync"/>.
    /// </summary>
    private void InvalidateVertexInputStructureNoLock()
    {
        _vertexInputStateDirty = true;
        _vertexInputStatesByProgram.Clear();
    }

    /// <summary>Requires <see cref="_bufferStateSync"/>.</summary>
    private bool TryRestoreVertexInputStateNoLock()
    {
        if (_program is not { } program ||
            !_vertexInputStatesByProgram.TryGetValue(program, out CachedVertexInputState? cached) ||
            cached.LinkGeneration != program.LinkGeneration)
        {
            return false;
        }

        _vertexBindings = cached.Bindings;
        _vertexAttributes = cached.Attributes;
        _geometryLayoutSignature = cached.GeometryLayoutSignature;
        _vertexBuffersByBinding.Clear();
        foreach (KeyValuePair<uint, VkDataBuffer> pair in cached.BuffersByBinding)
            _vertexBuffersByBinding[pair.Key] = pair.Value;
        return true;
    }

    /// <summary>Requires <see cref="_bufferStateSync"/>.</summary>
    private void CacheVertexInputStateNoLock()
    {
        if (_program is { } program)
        {
            _vertexInputStatesByProgram[program] = new CachedVertexInputState(
                program.LinkGeneration,
                _vertexBindings,
                _vertexAttributes,
                _geometryLayoutSignature,
                [.. _vertexBuffersByBinding]);
        }
    }
}
