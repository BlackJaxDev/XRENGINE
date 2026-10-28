using System.Globalization;
using VrStats = XREngine.RuntimeEngine.Rendering.Stats.Vr;
using VulkanStats = XREngine.RuntimeEngine.Rendering.Stats.Vulkan;

namespace XREngine.Editor;

/// <summary>
/// Per-frame histories for the Vulkan churn, Vulkan frame timing, and OpenXR timing values. The panel
/// samples once for each new render frame while it is active and not paused. The render-stat view shows
/// each series as a row whose plot cell draws the history.
/// </summary>
public partial class ProfilerPanel
{
    private readonly NativeProfilerStatHistory _statHistory = new();

    private void SampleStatHistory()
    {
        bool vulkan = RuntimeEngine.Rendering.State.IsVulkan;
        bool vr = VrStats.LastFrameHadVrWork;
        if (!vulkan && !vr)
            return;
        if (vulkan)
        {
            int pipelineBinds = VulkanStats.VulkanPipelineBinds;
            int pipelineSkips = VulkanStats.VulkanPipelineBindSkips;
            int descriptorBinds = VulkanStats.VulkanDescriptorBinds;
            int descriptorSkips = VulkanStats.VulkanDescriptorBindSkips;
            _statHistory.Set(ENativeProfilerStatSeries.VulkanPipelineBinds, pipelineBinds);
            _statHistory.Set(ENativeProfilerStatSeries.VulkanDescriptorBinds, descriptorBinds);
            _statHistory.Set(ENativeProfilerStatSeries.VulkanPipelineSkipPercent, Percent(pipelineSkips, pipelineBinds + pipelineSkips));
            _statHistory.Set(ENativeProfilerStatSeries.VulkanDescriptorSkipPercent, Percent(descriptorSkips, descriptorBinds + descriptorSkips));
            _statHistory.Set(ENativeProfilerStatSeries.VulkanPipelineCacheHitPercent, VulkanStats.VulkanPipelineCacheLookupHitRate * 100.0);
            _statHistory.Set(ENativeProfilerStatSeries.VulkanFrameTotalMs, VulkanStats.VulkanFrameTotalMs);
            _statHistory.Set(ENativeProfilerStatSeries.VulkanWaitFenceMs, VulkanStats.VulkanFrameWaitFenceMs);
            _statHistory.Set(ENativeProfilerStatSeries.VulkanRecordCommandBufferMs, VulkanStats.VulkanFrameRecordCommandBufferMs);
            _statHistory.Set(ENativeProfilerStatSeries.VulkanGpuCommandBufferMs, VulkanStats.VulkanFrameGpuCommandBufferMs);
        }
        if (vr)
        {
            _statHistory.Set(ENativeProfilerStatSeries.XrWaitFrameBlockMs, VrStats.VrXrWaitFrameBlockTimeMs);
            _statHistory.Set(ENativeProfilerStatSeries.XrEndFrameSubmitMs, VrStats.VrXrEndFrameSubmitTimeMs);
            _statHistory.Set(ENativeProfilerStatSeries.XrPredictedToLatePoseMm, VrStats.VrXrPredictedToLatePoseDeltaMillimeters);
            _statHistory.Set(ENativeProfilerStatSeries.XrPredictedDisplayLeadMs, VrStats.VrXrPredictedDisplayLeadTimeMs);
        }
        _statHistory.TryCommit(RuntimeEngine.Rendering.State.RenderFrameId);
    }

    private static double Percent(int part, int total)
        => total <= 0 ? 0.0 : part * 100.0 / total;

    private void CaptureVulkanHistoryRows(List<NativeProfilerDisplayRow> rows)
    {
        AddStatHistoryRow(rows, "Vulkan pipeline binds history", ENativeProfilerStatSeries.VulkanPipelineBinds, "F0", "binds/frame");
        AddStatHistoryRow(rows, "Vulkan descriptor binds history", ENativeProfilerStatSeries.VulkanDescriptorBinds, "F0", "binds/frame");
        AddStatHistoryRow(rows, "Vulkan pipeline skip history", ENativeProfilerStatSeries.VulkanPipelineSkipPercent, "F1", "%");
        AddStatHistoryRow(rows, "Vulkan descriptor skip history", ENativeProfilerStatSeries.VulkanDescriptorSkipPercent, "F1", "%");
        AddStatHistoryRow(rows, "Vulkan pipeline cache hit history", ENativeProfilerStatSeries.VulkanPipelineCacheHitPercent, "F1", "%");
        AddStatHistoryRow(rows, "Vulkan CPU frame total history", ENativeProfilerStatSeries.VulkanFrameTotalMs, "F3", "ms");
        AddStatHistoryRow(rows, "Vulkan CPU wait fence history", ENativeProfilerStatSeries.VulkanWaitFenceMs, "F3", "ms");
        AddStatHistoryRow(rows, "Vulkan CPU record history", ENativeProfilerStatSeries.VulkanRecordCommandBufferMs, "F3", "ms");
        AddStatHistoryRow(rows, "Vulkan GPU command buffer history", ENativeProfilerStatSeries.VulkanGpuCommandBufferMs, "F3", "ms");
    }

    private void CaptureOpenXrHistoryRows(List<NativeProfilerDisplayRow> rows)
    {
        AddStatHistoryRow(rows, "xrWaitFrame block history", ENativeProfilerStatSeries.XrWaitFrameBlockMs, "F3", "ms");
        AddStatHistoryRow(rows, "xrEndFrame submit history", ENativeProfilerStatSeries.XrEndFrameSubmitMs, "F3", "ms");
        AddStatHistoryRow(rows, "Predicted to late pose history", ENativeProfilerStatSeries.XrPredictedToLatePoseMm, "F2", "mm");
        AddStatHistoryRow(rows, "Predicted display lead history", ENativeProfilerStatSeries.XrPredictedDisplayLeadMs, "F3", "ms");
    }

    private void AddStatHistoryRow(List<NativeProfilerDisplayRow> rows, string name,
        ENativeProfilerStatSeries series, string format, string units)
    {
        int requested = Engine.EditorPreferences.Debug.ProfilerPanelGraphSampleCount;
        if (!_statHistory.TryGetSummary(series, requested, out float latest, out float average, out float maximum))
            return;
        int frames = Math.Min(_statHistory.Count, Math.Clamp(requested, 1, NativeProfilerStatHistory.Capacity));
        rows.Add(new(name, latest.ToString(format, CultureInfo.InvariantCulture),
            $"{units} latest | average {average.ToString(format, CultureInfo.InvariantCulture)} | max {maximum.ToString(format, CultureInfo.InvariantCulture)} over {frames} frames",
            StatPlotSeries: series));
    }
}
