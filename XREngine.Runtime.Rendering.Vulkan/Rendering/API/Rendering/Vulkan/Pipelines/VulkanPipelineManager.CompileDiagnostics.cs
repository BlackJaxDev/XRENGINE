using System.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanPipelineManager
{
    private const int MaxPendingJobDiagnosticRows = 8;

    /// <summary>
    /// Captures the oldest queued jobs from frozen build requests. The snapshot does not
    /// inspect shader caches or wait for a native pipeline creation call.
    /// </summary>
    private (int GraphicsCount, VulkanGraphicsPipelineJobDiagnostic[] Graphics,
        int ComputeCount, VulkanComputePipelineJobDiagnostic[] Compute) CapturePendingJobDiagnostics()
    {
        VulkanGraphicsPipelineCompileJob?[] graphics = new VulkanGraphicsPipelineCompileJob?[MaxPendingJobDiagnosticRows];
        VulkanComputePipelineCompileJob?[] compute = new VulkanComputePipelineCompileJob?[MaxPendingJobDiagnosticRows];
        int graphicsCount;
        int computeCount;
        lock (_vulkanGraphicsPipelineCompileJobsLock)
        {
            graphicsCount = 0;
            foreach (VulkanGraphicsPipelineCompileJob job in _vulkanGraphicsPipelineCompileJobs.Values)
            {
                InsertOldest(graphics, job);
                graphicsCount++;
            }

            computeCount = 0;
            foreach (VulkanComputePipelineCompileJob job in _vulkanComputePipelineCompileJobs.Values)
            {
                InsertOldest(compute, job);
                computeCount++;
            }
        }

        long now = Stopwatch.GetTimestamp();
        VulkanGraphicsPipelineJobDiagnostic[] graphicsRows = new VulkanGraphicsPipelineJobDiagnostic[Math.Min(graphicsCount, MaxPendingJobDiagnosticRows)];
        for (int i = 0; i < graphicsRows.Length; i++)
        {
            VulkanGraphicsPipelineCompileJob job = graphics[i]!;
            VulkanGraphicsPipelineBuildRequest request = job.Request;
            PipelineShaderStageCreateInfo[] frozenStages = request.GraphicsStages;
            VulkanPipelineShaderStageDiagnostic[] stages = new VulkanPipelineShaderStageDiagnostic[frozenStages.Length];
            for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
            {
                PipelineShaderStageCreateInfo stage = frozenStages[stageIndex];
                stages[stageIndex] = new(stage.Stage.ToString(), stage.Module.Handle);
            }

            graphicsRows[i] = new()
            {
                PipelineName = request.PipelineName,
                ProgramName = request.ProgramName,
                OwnerId = request.OwnerId,
                ProgramPipelineHash = request.Key.ProgramPipelineHash,
                InterfaceGeneration = request.Key.InterfaceGeneration,
                DependencyGeneration = request.DependencyGeneration,
                UsesGraphicsPipelineLibraries = request.UseGraphicsPipelineLibraries,
                IsMeshShaderPipeline = request.IsMeshShaderPipeline,
                QueuedAgeMilliseconds = Stopwatch.GetElapsedTime(job.QueuedTimestamp, now).TotalMilliseconds,
                WorkerPromiseStatus = job.Task.Status.ToString(),
                PublicationPromiseStatus = job.PublicationTask.Status.ToString(),
                Stages = stages,
            };
        }

        VulkanComputePipelineJobDiagnostic[] computeRows = new VulkanComputePipelineJobDiagnostic[Math.Min(computeCount, MaxPendingJobDiagnosticRows)];
        for (int i = 0; i < computeRows.Length; i++)
        {
            VulkanComputePipelineCompileJob job = compute[i]!;
            VulkanComputePipelineBuildRequest request = job.Request;
            computeRows[i] = new()
            {
                ProgramName = request.ProgramName,
                ProgramBindingId = request.Key.ProgramBindingId,
                ProgramFingerprint = request.Key.ProgramFingerprint,
                ProgramLinkGeneration = request.Key.ProgramLinkGeneration,
                DependencyGeneration = request.Key.DependencyGeneration,
                QueuedAgeMilliseconds = Stopwatch.GetElapsedTime(job.QueuedTimestamp, now).TotalMilliseconds,
                WorkerPromiseStatus = job.Task.Status.ToString(),
                Stage = new(request.ComputeStage.Stage.ToString(), request.ComputeStage.Module.Handle),
            };
        }

        return (graphicsCount, graphicsRows, computeCount, computeRows);
    }

    private static void InsertOldest(VulkanGraphicsPipelineCompileJob?[] rows, VulkanGraphicsPipelineCompileJob job)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] is { } existing && existing.QueuedTimestamp <= job.QueuedTimestamp)
                continue;

            for (int shift = rows.Length - 1; shift > i; shift--)
                rows[shift] = rows[shift - 1];
            rows[i] = job;
            break;
        }
    }

    private static void InsertOldest(VulkanComputePipelineCompileJob?[] rows, VulkanComputePipelineCompileJob job)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] is { } existing && existing.QueuedTimestamp <= job.QueuedTimestamp)
                continue;

            for (int shift = rows.Length - 1; shift > i; shift--)
                rows[shift] = rows[shift - 1];
            rows[i] = job;
            break;
        }
    }
}
