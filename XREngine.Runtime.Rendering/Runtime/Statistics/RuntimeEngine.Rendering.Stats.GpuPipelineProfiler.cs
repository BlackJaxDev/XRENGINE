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

                    /// <summary>
                    /// Returns the timing statistics of every profiled pipeline, keyed by profiler key and profiling path.
                    /// The result is cached until the profiler records a new frame.
                    /// </summary>
                    public static RenderPipelineTimingSnapshot GetTimingSnapshot()
                        => RenderPipelineGpuProfiler.Instance.GetTimingSnapshot();

                    public static Data.Profiling.GpuPipelineTimingNodeData[] GetGpuRenderPipelineTimingRoots()
                        => RenderPipelineGpuProfiler.Instance.LatestSnapshot.Roots;

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
