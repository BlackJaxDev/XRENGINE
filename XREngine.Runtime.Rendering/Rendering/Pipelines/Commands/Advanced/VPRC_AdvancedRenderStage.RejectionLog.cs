using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Commands;

public sealed partial class VPRC_AdvancedRenderStage
{
    /// <summary>The minimum time between two stage rejection reports of one stage command.</summary>
    private static readonly long StageRejectionReportRepeatTicks = System.Diagnostics.Stopwatch.Frequency * 5;

    /// <summary>The timestamp of the last stage rejection report, or 0 before the first report.</summary>
    private long _stageRejectionReportTimestamp;

    /// <summary>The number of rejections of this stage command since it was created.</summary>
    private long _stageRejectionCount;

    /// <summary>
    /// Writes a rejection of this native stage to <see cref="RenderReadinessFailureLog"/>
    /// (category <c>AdvancedStageRejected</c>). A rejection of a native scene stage stops the
    /// authoring of the frame: the stage throws, and the frame records only the operations
    /// before it, so the desktop output can stall with no scene draw. Release builds compile
    /// out the rendering warning, so this report is the only Release record of the cause. The
    /// line names the stage, the backend phase, the rejection kind, whether the stage retries
    /// on a later frame, and the rejection count.
    /// </summary>
    /// <remarks>
    /// The method writes at most one line per <see cref="StageRejectionReportRepeatTicks"/> for
    /// each stage command. Strings are formatted only when a line is written.
    /// </remarks>
    private void ReportStageRejectionToReadinessLog(
        string kind,
        EAdvancedVisibilityStageBackendPhase phase,
        string reason,
        bool retryable)
    {
        _stageRejectionCount++;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_stageRejectionReportTimestamp != 0 &&
            now - _stageRejectionReportTimestamp < StageRejectionReportRepeatTicks)
            return;

        _stageRejectionReportTimestamp = now;
        RenderReadinessFailureLog.Report(
            "AdvancedStageRejected",
            ActivePipelineInstance.ProfilerKey,
            $"time={DateTimeOffset.Now:HH:mm:ss.fff} stage={Stage} phase={phase} kind={kind} " +
            $"retryable={retryable} rejections={_stageRejectionCount}",
            string.IsNullOrEmpty(reason) ? "<none>" : reason);
    }
}
