using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Records one already-bound stable-bin lane. CPU direct encoding is supplied
    /// explicitly by the caller because this recorder never reconstructs draw
    /// state. GPU indirect encoding consumes only the frozen set-1 argument and
    /// count offsets. Neither branch observes GPU results or retries another lane.
    /// </summary>
    internal unsafe bool TryRecordStableBinSubmission(
        CommandBuffer commandBuffer,
        in VulkanStableBinSubmission submission,
        VulkanStableBinCpuDirectRecorder? cpuDirectRecorder,
        ReadOnlySpan<VulkanPreparedStableBinRecord> records,
        out VulkanStableBinSubmissionRecordingFailure failure)
    {
        failure = VulkanStableBinSubmissionRecordingFailure.None;
        switch (submission.Plan.ResolvedStrategy)
        {
            case EMeshSubmissionStrategy.CpuDirect:
                if (cpuDirectRecorder is null ||
                    !cpuDirectRecorder(commandBuffer, records))
                {
                    failure = VulkanStableBinSubmissionRecordingFailure.CpuDirectEncoderRejected;
                    return false;
                }
                return true;

            case EMeshSubmissionStrategy.GpuIndirectZeroReadback:
            case EMeshSubmissionStrategy.GpuIndirectInstrumented:
            case EMeshSubmissionStrategy.GpuMeshletZeroReadback:
            case EMeshSubmissionStrategy.GpuMeshletInstrumented:
                break;

            default:
                failure = VulkanStableBinSubmissionRecordingFailure.UnsupportedStrategy;
                return false;
        }

        VulkanAdvancedVisibilityResourceState state = submission.VisibilityState;
        bool meshlet = submission.Plan.ResolvedStrategy is
            EMeshSubmissionStrategy.GpuMeshletZeroReadback or
            EMeshSubmissionStrategy.GpuMeshletInstrumented;
        if (!state.IsValid || state.RangeCounts.Buffer.Handle == 0 ||
            (!meshlet && state.IndirectArguments.Buffer.Handle == 0) ||
            (meshlet && state.MeshArguments.Buffer.Handle == 0) ||
            (!meshlet && !_deviceContext.Capabilities.Supports(
                EVulkanDeviceCapability.DrawIndirectCount)) ||
            (meshlet && (!DeviceContext.SupportsMeshTaskIndirectCount ||
                DeviceContext.ExtensionFunctions.ExtMeshShader is null)))
        {
            failure = VulkanStableBinSubmissionRecordingFailure.GpuLaneUnavailable;
            return false;
        }

        // The family recorder publishes one batched compute-to-draw barrier
        // before entering the visibility raster pass. Per-bin barriers would
        // illegally split that sealed synchronization boundary.
        TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.Buffer,
            meshlet
                ? state.MeshArguments.Buffer.Handle
                : state.IndirectArguments.Buffer.Handle,
            meshlet
                ? "StableBin.MeshArguments"
                : "StableBin.IndirectArguments");
        TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.Buffer,
            state.RangeCounts.Buffer.Handle,
            "StableBin.RangeCount");

        uint maxDrawCount = submission.MaximumDrawCount;
        if (maxDrawCount == 0u)
            return true;
        if (meshlet)
        {
            DeviceContext.ExtensionFunctions.ExtMeshShader!.CmdDrawMeshTasksIndirectCount(
                commandBuffer,
                state.MeshArguments.Buffer,
                submission.MeshArgumentOffset,
                state.RangeCounts.Buffer,
                submission.CountOffset,
                maxDrawCount,
                12u);
            RecordGpuMeshletDispatchEmission();
        }
        else if (_deviceContext.MutableCapabilities._usesCoreDrawIndirectCountCommands)
        {
            Api!.CmdDrawIndexedIndirectCount(
                commandBuffer,
                state.IndirectArguments.Buffer,
                submission.IndexedArgumentOffset,
                state.RangeCounts.Buffer,
                submission.CountOffset,
                maxDrawCount,
                20u);
        }
        else if (DeviceContext.ExtensionFunctions.KhrDrawIndirectCount is { } extension)
        {
            extension.CmdDrawIndexedIndirectCount(
                commandBuffer,
                state.IndirectArguments.Buffer,
                submission.IndexedArgumentOffset,
                state.RangeCounts.Buffer,
                submission.CountOffset,
                maxDrawCount,
                20u);
        }
        else
        {
            failure = VulkanStableBinSubmissionRecordingFailure.GpuLaneUnavailable;
            return false;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(
            usedCountPath: true,
            usedLoopFallback: false,
            apiCalls: 1,
            submittedDraws: maxDrawCount);
        return true;
    }
}

/// <summary>Caller-supplied exact CPU direct encoder for an already bound lane.</summary>
internal delegate bool VulkanStableBinCpuDirectRecorder(
    CommandBuffer commandBuffer,
    ReadOnlySpan<VulkanPreparedStableBinRecord> records);

internal enum VulkanStableBinSubmissionRecordingFailure : byte
{
    None = 0,
    CpuDirectEncoderRejected = 1,
    GpuLaneUnavailable = 2,
    UnsupportedStrategy = 3,
}
