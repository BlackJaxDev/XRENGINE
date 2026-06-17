using System.ComponentModel;
using System.Threading.Tasks;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMcp(Name = "dump_cpu_frame_profile", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Dump the latest CPU profiler frame snapshot to an LLM-readable log file in the current Build/Logs run directory.")]
        public static Task<McpToolResponse> DumpCpuFrameProfileAsync(McpToolContext context)
        {
            ProfilerDiagnosticDumps.DumpResult result = ProfilerDiagnosticDumps.DumpCpuFrameTimingHistory();
            return Task.FromResult(new McpToolResponse(
                result.Message,
                new
                {
                    files = result.FileNames,
                    paths = ProfilerDiagnosticDumps.BuildAbsoluteLogPaths(result.FileNames),
                    log_directory = ProfilerDiagnosticDumps.GetCurrentLogDirectory(),
                    error = result.Error
                },
                isError: !result.Success));
        }

        [XRMcp(Name = "dump_gpu_render_pipeline_profile", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Dump retained GPU timing history for one render pipeline, or all captured pipelines when pipeline_name is omitted.")]
        public static Task<McpToolResponse> DumpGpuRenderPipelineProfileAsync(
            McpToolContext context,
            [McpName("pipeline_name"), Description("Render pipeline root name to dump. Omit to dump all captured render pipelines.")]
            string? pipelineName = null,
            [McpName("all_pipelines"), Description("When true, dump all captured render pipelines regardless of pipeline_name.")]
            bool allPipelines = false)
        {
            ProfilerDiagnosticDumps.DumpResult result = allPipelines
                ? ProfilerDiagnosticDumps.DumpAllGpuRenderPipelineTimingHistories()
                : ProfilerDiagnosticDumps.DumpGpuRenderPipelineTimingHistory(pipelineName);

            string[] availablePipelines = result.Success
                ? []
                : ProfilerDiagnosticDumps.GetAvailableGpuRenderPipelineNames();

            return Task.FromResult(new McpToolResponse(
                result.Message,
                new
                {
                    files = result.FileNames,
                    paths = ProfilerDiagnosticDumps.BuildAbsoluteLogPaths(result.FileNames),
                    log_directory = ProfilerDiagnosticDumps.GetCurrentLogDirectory(),
                    available_pipelines = availablePipelines,
                    error = result.Error
                },
                isError: !result.Success));
        }
    }
}
