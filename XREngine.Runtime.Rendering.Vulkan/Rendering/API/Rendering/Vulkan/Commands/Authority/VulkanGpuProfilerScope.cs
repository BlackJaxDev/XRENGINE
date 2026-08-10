using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Completes one command-owned Vulkan timestamp scope without retaining the
/// renderer facade.
/// </summary>
internal readonly struct VulkanGpuProfilerScope : IDisposable
{
    private readonly Vk? _api;
    private readonly VulkanFrameTelemetry? _telemetry;
    private readonly CommandBuffer _commandBuffer;
    private readonly QueryPool _queryPool;
    private readonly int _frameSlot;
    private readonly uint _endQuery;
    private readonly string[]? _path;

    internal VulkanGpuProfilerScope(
        Vk api,
        VulkanFrameTelemetry telemetry,
        CommandBuffer commandBuffer,
        QueryPool queryPool,
        int frameSlot,
        uint endQuery,
        string[] path)
    {
        _api = api;
        _telemetry = telemetry;
        _commandBuffer = commandBuffer;
        _queryPool = queryPool;
        _frameSlot = frameSlot;
        _endQuery = endQuery;
        _path = path;
    }

    public void Dispose()
    {
        if (_api is null ||
            _telemetry is null ||
            _path is null ||
            !_telemetry._vulkanGpuProfilerRecordingActive ||
            _frameSlot < 0 ||
            _telemetry._vulkanGpuProfilerPendingScopes is null ||
            _telemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            _frameSlot >= _telemetry._vulkanGpuProfilerPendingScopes.Length ||
            _commandBuffer.Handle == 0 ||
            _queryPool.Handle == 0)
        {
            return;
        }

        _api.CmdWriteTimestamp(
            _commandBuffer,
            PipelineStageFlags.BottomOfPipeBit,
            _queryPool,
            _endQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(
            ERendererProfilerCounter.TimestampQueryCount);
        _telemetry._vulkanGpuProfilerPendingScopes[_frameSlot].Add(
            new VulkanGpuProfilerPendingScope(_path, _endQuery - 1, _endQuery));
        _telemetry._vulkanGpuProfilerPendingQueryCounts[_frameSlot] = Math.Max(
            _telemetry._vulkanGpuProfilerPendingQueryCounts[_frameSlot],
            (int)_endQuery + 1);
    }
}
