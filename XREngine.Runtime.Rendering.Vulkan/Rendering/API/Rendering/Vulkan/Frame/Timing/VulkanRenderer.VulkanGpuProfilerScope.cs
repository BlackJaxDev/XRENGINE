using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    internal readonly struct VulkanGpuProfilerScope : IDisposable
    {
        private readonly VulkanRenderer? _renderer;
        private readonly CommandBuffer _commandBuffer;
        private readonly QueryPool _queryPool;
        private readonly int _frameSlot;
        private readonly uint _endQuery;
        private readonly string[]? _path;

        public VulkanGpuProfilerScope(
            VulkanRenderer renderer,
            CommandBuffer commandBuffer,
            QueryPool queryPool,
            int frameSlot,
            uint endQuery,
            string[] path)
        {
            _renderer = renderer;
            _commandBuffer = commandBuffer;
            _queryPool = queryPool;
            _frameSlot = frameSlot;
            _endQuery = endQuery;
            _path = path;
        }

        public void Dispose()
            => _renderer?.EndVulkanGpuProfilerScope(_commandBuffer, _queryPool, _frameSlot, _endQuery, _path);
    }
}
