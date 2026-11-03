using System.Runtime.ExceptionServices;
using XREngine.Execution;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Records one frozen desktop primary on a reserved background render lane. The
/// executor installs the prepared planner generation and the lane's command arena,
/// then calls the same primary recorder that the render thread uses. Its input and
/// result storage are reused; one batch at a time owns them.
/// </summary>
internal sealed class VulkanDesktopRecordWorkExecutor : IRenderWorkExecutor
{
    internal const int DesktopPrimaryOperationKind = 3;

    private VulkanCommandRuntime? _runtime;
    private ResourcePlannerRuntimeGeneration? _plannerGeneration;
    private VulkanPreparedPrimaryCommandInput _input;
    private int _laneId = -1;
    private VulkanPrimaryCommandRecordingResult _result;
    private ExceptionDispatchInfo? _failure;
    private long _executeStartTicks;
    private long _executeEndTicks;

    /// <summary>Start and end timestamps of the last executed recording.</summary>
    internal (long Start, long End) LastExecuteTicks => (_executeStartTicks, _executeEndTicks);

    internal void Prepare(
        VulkanCommandRuntime runtime,
        ResourcePlannerRuntimeGeneration plannerGeneration,
        in VulkanPreparedPrimaryCommandInput input,
        int laneId)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _plannerGeneration = plannerGeneration ?? throw new ArgumentNullException(nameof(plannerGeneration));
        _input = input;
        _laneId = laneId;
        _result = default;
        _failure = null;
    }

    /// <summary>Returns the recording result, or rethrows the worker's exception.</summary>
    internal VulkanPrimaryCommandRecordingResult TakeResult()
    {
        ExceptionDispatchInfo? failure = _failure;
        VulkanPrimaryCommandRecordingResult result = _result;
        Clear();
        failure?.Throw();
        return result;
    }

    internal void Clear()
    {
        _runtime = null;
        _plannerGeneration = null;
        _input = default;
        _laneId = -1;
        _result = default;
        _failure = null;
    }

    public void Execute(in RenderWorkItem item, ref RenderWorkerContext context)
    {
        if (item.OperationKind != DesktopPrimaryOperationKind || context.LaneId != _laneId)
        {
            throw new InvalidOperationException(
                $"Desktop recording expected lane {_laneId}, not {context.LaneId}, for operation {item.OperationKind}.");
        }
        if (!context.TryGetBackendAttachment(out VulkanRenderLaneFrameAttachment? attachment) ||
            attachment is null ||
            attachment.LaneId != context.LaneId ||
            attachment.FrameSlot != context.FrameSlot)
        {
            throw new InvalidOperationException(
                $"Desktop recording lane {context.LaneId} has no matching Vulkan render-lane attachment.");
        }

        VulkanCommandRuntime runtime = _runtime ??
            throw new InvalidOperationException("Desktop recording executor is not prepared.");
        _executeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            using VulkanRenderLaneExecutionScope laneScope = new(attachment);
            using VulkanLaneCommandFamilyArena.RecordingLease arenaLease =
                VulkanLaneCommandFamilyArena.EnterRecording(attachment.Graphics);
            using VulkanPreparedResourcePlannerThreadScope plannerScope = new(
                runtime.ThreadWorkspace.Current,
                runtime,
                _plannerGeneration!);
            _result = runtime.RecordPrimary(in _input);
        }
        catch (Exception ex)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            _executeEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    public void QuarantineFaultedBatch(in RenderWorkBatchFaultContext context)
    {
        // Execute stores recording failures instead of faulting the batch, so a
        // scheduler fault here means the item never ran; no partial output exists.
    }
}
