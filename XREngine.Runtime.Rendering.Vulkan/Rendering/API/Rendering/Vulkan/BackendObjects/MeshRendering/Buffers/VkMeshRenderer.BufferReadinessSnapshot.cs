using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    /// <summary>
    /// Immutable buffer membership published after structural mesh-buffer
    /// changes so readiness probes never enumerate a mutable dictionary.
    /// </summary>
    private sealed class BufferReadinessSnapshot
    {
        public static BufferReadinessSnapshot Empty { get; } =
            new([], [], string.Empty, 0u, 0u, 0u, 0u, fallbackIsTriangleClass: false);

        public BufferReadinessSnapshot(
            KeyValuePair<string, VkDataBuffer>[] requiredBuffers,
            KeyValuePair<string, VkDataBuffer>[] shaderGeneratedRequiredBuffers,
            string missingExpectedIndexBufferDetail,
            uint triangleIndexCount,
            uint lineIndexCount,
            uint pointIndexCount,
            uint fallbackVertexCount,
            bool fallbackIsTriangleClass)
        {
            RequiredBuffers = requiredBuffers;
            ShaderGeneratedRequiredBuffers = shaderGeneratedRequiredBuffers;
            MissingExpectedIndexBufferDetail = missingExpectedIndexBufferDetail;
            TriangleIndexCount = triangleIndexCount;
            LineIndexCount = lineIndexCount;
            PointIndexCount = pointIndexCount;
            FallbackVertexCount = fallbackVertexCount;
            FallbackIsTriangleClass = fallbackIsTriangleClass;
        }

        public KeyValuePair<string, VkDataBuffer>[] RequiredBuffers { get; }
        public KeyValuePair<string, VkDataBuffer>[] ShaderGeneratedRequiredBuffers { get; }
        public string MissingExpectedIndexBufferDetail { get; }
        public uint TriangleIndexCount { get; }
        public uint LineIndexCount { get; }
        public uint PointIndexCount { get; }
        public uint FallbackVertexCount { get; }
        public bool FallbackIsTriangleClass { get; }
    }
}
