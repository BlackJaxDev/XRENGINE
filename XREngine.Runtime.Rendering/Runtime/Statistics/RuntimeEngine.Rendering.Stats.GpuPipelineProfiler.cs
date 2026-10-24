using XREngine.Rendering;

namespace XREngine
{
    public static partial class RuntimeEngine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class GpuPipelineProfiler
                {
                    public static bool GpuRenderPipelineProfilingEnabled
                        => RenderPipelineGpuProfiler.Instance.LatestSnapshot.Enabled;

                    public static bool GpuRenderPipelineProfilingSupported
                        => RenderPipelineGpuProfiler.Instance.LatestSnapshot.Supported;

                    public static bool GpuRenderPipelineTimingsReady
                        => RenderPipelineGpuProfiler.Instance.LatestSnapshot.Ready;

                    public static string GpuRenderPipelineBackend
                        => RenderPipelineGpuProfiler.Instance.LatestSnapshot.BackendName;

                    public static string GpuRenderPipelineStatusMessage
                        => RenderPipelineGpuProfiler.Instance.LatestSnapshot.StatusMessage;

                    public static double GpuRenderPipelineFrameMs
                        => RenderPipelineGpuProfiler.Instance.LatestSnapshot.FrameMilliseconds;

                    public static Data.Profiling.GpuPipelineTimingNodeData[] GetGpuRenderPipelineTimingRoots()
                        => RenderPipelineGpuProfiler.Instance.LatestSnapshot.Roots;

                    /// <summary>Copies the newest recorded frame times and returns the total sample count.</summary>
                    public static int CopyGpuRenderPipelineTimingHistory(
                        string pipelineName, Span<double> destination, out ulong newestFrameId)
                        => RenderPipelineGpuProfiler.Instance.CopyTimingHistory(pipelineName, destination, out newestFrameId);

                    /// <summary>Copies frame times with their exact history generation.</summary>
                    public static int CopyGpuRenderPipelineTimingHistory(
                        string pipelineName, Span<double> destination,
                        out ulong newestFrameId, out long historyGeneration)
                        => RenderPipelineGpuProfiler.Instance.CopyTimingHistory(
                            pipelineName, destination, out newestFrameId, out historyGeneration);

                    /// <summary>Copies aggregate and retained GPU frame values.</summary>
                    public static bool TryCopyGpuRenderPipelineHistoryDetails(
                        string pipelineName,
                        Span<GpuPipelineHistoryFrame> recentFrames,
                        Span<GpuPipelineHistoryFrame> worstFrames,
                        out GpuPipelineHistorySummary summary)
                        => RenderPipelineGpuProfiler.Instance.TryCopyHistoryDetails(
                            pipelineName, recentFrames, worstFrames, out summary);

                    /// <summary>Copies a bounded tree from one exact worst GPU frame.</summary>
                    public static bool TryCopyGpuWorstFrameTree(string pipelineName,
                        long historyGeneration, ulong frameId,
                        Span<GpuPipelineTimingTreeRow> destination,
                        out int copied, out bool truncated)
                        => RenderPipelineGpuProfiler.Instance.TryCopyWorstFrameTree(
                            pipelineName, historyGeneration, frameId, destination,
                            out copied, out truncated);

                    /// <summary>Copies one page of ranked nodes from an exact history revision.</summary>
                    public static bool TryCopyGpuNodeRankPage(string pipelineName,
                        long historyGeneration, long expectedRevision,
                        EGpuPipelineNodeRankMetric metric, int offset,
                        Span<GpuPipelineNodeRankRow> destination,
                        out long actualRevision, out int totalNodes,
                        out int copied, out bool truncated)
                        => RenderPipelineGpuProfiler.Instance.TryCopyRankPage(
                            pipelineName, historyGeneration, expectedRevision,
                            metric, offset, destination, out actualRevision,
                            out totalNodes, out copied, out truncated);

                    public static bool TryDumpGpuRenderPipelineTimingHistory(
                        string pipelineName,
                        out string fileName,
                        out string? error)
                        => RenderPipelineGpuProfiler.Instance.TryDumpTimingHistory(pipelineName, out fileName, out error);

                    public static bool TryDumpAllGpuRenderPipelineTimingHistories(
                        out string[] fileNames,
                        out string? error)
                        => RenderPipelineGpuProfiler.Instance.TryDumpAllTimingHistories(out fileNames, out error);
                }
            }
        }
    }
}
