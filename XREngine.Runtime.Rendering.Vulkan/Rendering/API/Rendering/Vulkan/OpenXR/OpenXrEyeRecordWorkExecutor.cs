using System.Diagnostics;
using System.Runtime.ExceptionServices;
using XREngine.Execution;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Executes the two frozen OpenXR eye-primary inputs on their exact logical
/// render lanes. Results remain indexed by canonical eye order, independent of
/// lane completion order.
/// </summary>
internal sealed class OpenXrEyeRecordWorkExecutor : IRenderWorkExecutor
{
    internal const int EyePrimaryOperationKind = 2;

    private readonly OpenXrPreparedEyeRecordWorkerInput[] _inputs = new OpenXrPreparedEyeRecordWorkerInput[2];
    private readonly OpenXrEyeRecordWorkerResult[] _results = new OpenXrEyeRecordWorkerResult[2];
    private VulkanCommandRuntime? _runtime;
    private VulkanOpenXrCommandRecordingService? _recordingService;

    internal void Prepare(
        VulkanCommandRuntime runtime,
        VulkanOpenXrCommandRecordingService recordingService,
        in OpenXrPreparedEyeRecordWorkerInput firstEye,
        in OpenXrPreparedEyeRecordWorkerInput secondEye)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(recordingService);
        if (firstEye.RenderFrameSlot != secondEye.RenderFrameSlot)
        {
            throw new InvalidOperationException(
                "A paired OpenXR render-domain batch requires one shared in-flight frame slot.");
        }

        _runtime = runtime;
        _recordingService = recordingService;
        _inputs[0] = firstEye;
        _inputs[1] = secondEye;
        _results[0] = default;
        _results[1] = default;
    }

    internal ref readonly OpenXrEyeRecordWorkerResult GetResult(int eyeOrdinal)
    {
        if ((uint)eyeOrdinal >= (uint)_results.Length)
            throw new ArgumentOutOfRangeException(nameof(eyeOrdinal));
        return ref _results[eyeOrdinal];
    }

    internal void Clear()
    {
        _runtime = null;
        _recordingService = null;
        Array.Clear(_inputs);
        Array.Clear(_results);
    }

    public void Execute(in RenderWorkItem item, ref RenderWorkerContext context)
    {
        if (item.OperationKind != EyePrimaryOperationKind ||
            item.SourceCount != 1 ||
            (uint)item.SourceStart >= (uint)_inputs.Length)
        {
            throw new InvalidOperationException(
                "OpenXR eye recording received an invalid frozen work range.");
        }

        int eyeOrdinal = item.SourceStart;
        OpenXrPreparedEyeRecordWorkerInput prepared = _inputs[eyeOrdinal];
        if (context.LaneId != prepared.RenderLaneId ||
            context.FrameSlot != prepared.RenderFrameSlot)
        {
            throw new InvalidOperationException(
                $"OpenXR eye {eyeOrdinal} expected render lane {prepared.RenderLaneId}:" +
                $"{prepared.RenderFrameSlot}, not {context.LaneId}:{context.FrameSlot}.");
        }
        if (!context.TryGetBackendAttachment(
                out VulkanRenderLaneFrameAttachment? attachment) ||
            attachment is null ||
            attachment.LaneId != context.LaneId ||
            attachment.FrameSlot != context.FrameSlot)
        {
            throw new InvalidOperationException(
                $"OpenXR eye {eyeOrdinal} has no matching Vulkan render-lane attachment.");
        }

        VulkanOpenXrCommandRecordingService recordingService =
            _recordingService ??
            throw new InvalidOperationException(
                "OpenXR eye recording executor is not configured.");
        long start = Stopwatch.GetTimestamp();
        try
        {
            using VulkanRenderLaneExecutionScope laneScope = new(attachment);
            using VulkanLaneCommandFamilyArena.RecordingLease arenaLease =
                VulkanLaneCommandFamilyArena.EnterRecording(
                    attachment.Graphics);
            bool success = recordingService.TryRecordPreparedEye(
                context.LaneId,
                in prepared,
                out OpenXrRecordedEyeCommandBuffer recorded,
                out VulkanImportedTexturePendingUpload[] recordedUploads);
            long end = Stopwatch.GetTimestamp();
            _results[eyeOrdinal] = new OpenXrEyeRecordWorkerResult(
                success,
                recorded,
                context.ManagedThreadId,
                Stopwatch.GetElapsedTime(start, end),
                null,
                start,
                end,
                recordedUploads);
        }
        catch (Exception ex)
        {
            long end = Stopwatch.GetTimestamp();
            _results[eyeOrdinal] = new OpenXrEyeRecordWorkerResult(
                false,
                default,
                context.ManagedThreadId,
                Stopwatch.GetElapsedTime(start, end),
                ex.Message,
                start,
                end,
                Failure: ExceptionDispatchInfo.Capture(ex));
        }
    }

    public void QuarantineFaultedBatch(in RenderWorkBatchFaultContext context)
    {
        VulkanCommandRuntime? runtime = _runtime;
        if (runtime is null)
            return;

        for (int eyeOrdinal = 0; eyeOrdinal < _results.Length; eyeOrdinal++)
        {
            OpenXrRecordedEyeCommandBuffer recorded =
                _results[eyeOrdinal].Recorded;
            if (recorded.CommandBuffer.Handle == 0)
                continue;

            try
            {
                runtime.MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(
                    in recorded,
                    "OpenXR render-domain batch faulted before canonical submission");
            }
            catch
            {
                // Quarantine callbacks are required to remain nonthrowing.
            }
        }
    }
}
