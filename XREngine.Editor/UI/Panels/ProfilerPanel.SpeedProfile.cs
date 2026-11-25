using System.Globalization;

namespace XREngine.Editor;

public partial class ProfilerPanel
{
    private int _speedProfileSeconds = 15;
    private string _speedProfileStatus = string.Empty;
    private string _speedProfileSummaryPath = string.Empty;

    /// <summary>Gets or sets the duration of the next speed profile capture, in seconds.</summary>
    internal int SpeedProfileSeconds
    {
        get => _speedProfileSeconds;
        set
        {
            if (value < NativeProfilerSpeedProfileSecondsBinding.MinimumSeconds ||
                value > NativeProfilerSpeedProfileSecondsBinding.MaximumSeconds)
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _speedProfileSeconds, value, publishNotifications: false);
        }
    }

    /// <summary>
    /// Adds the speed profile command and duration rows to the Settings view. The command uses
    /// the same engine capture as the ImGui profiler.
    /// </summary>
    private void AddSpeedProfileRows(List<NativeProfilerDisplayRow> rows)
    {
        SyncSpeedProfileSummary();
        bool active = Engine.IsSpeedProfileCaptureActive;
        string value = active
            ? Engine.SpeedProfileCaptureSecondsRemaining.ToString("F1", CultureInfo.InvariantCulture) + " s"
            : "Off";
        string details = active ? "Activate to stop and write the summary." :
            _speedProfileStatus.Length != 0 ? _speedProfileStatus :
            "Activate to capture frame, render, and GPU pipeline statistics.";
        rows.Add(new("Speed profile", value, details, (int)ENativeProfilerSetting.SpeedProfile));
        rows.Add(new("Speed profile seconds", SpeedProfileSeconds.ToString(CultureInfo.InvariantCulture),
            "1 to 600 seconds; edit value", (int)ENativeProfilerSetting.SpeedProfileSeconds));
        if (_speedProfileSummaryPath.Length != 0)
            rows.Add(new("Speed profile summary", string.Empty, _speedProfileSummaryPath));
    }

    /// <summary>Starts a capture, or stops the active capture and records its summary path.</summary>
    private void ToggleSpeedProfile()
    {
        if (!IsProfilerOwnerCurrent)
            return;
        if (Engine.IsSpeedProfileCaptureActive)
        {
            if (Engine.TryStopSpeedProfileCapture(out string summaryPath, out string? error))
            {
                _speedProfileSummaryPath = summaryPath;
                _speedProfileStatus = "Speed profile written.";
            }
            else
                _speedProfileStatus = string.IsNullOrWhiteSpace(error) ? "Speed profile stop failed." : error;
        }
        else
        {
            _speedProfileStatus = Engine.TryStartSpeedProfileCapture(SpeedProfileSeconds, "native-profiler-panel",
                out string? error)
                ? "Speed profile running."
                : string.IsNullOrWhiteSpace(error) ? "Speed profile start failed." : error;
        }
        if (IsProfilerOwnerCurrent)
            RefreshRows();
    }

    /// <summary>Records a summary that a capture wrote when its duration ended.</summary>
    private void SyncSpeedProfileSummary()
    {
        if (Engine.IsSpeedProfileCaptureActive)
            return;
        string lastSummary = Engine.LastSpeedProfileCaptureSummaryPath;
        if (string.IsNullOrWhiteSpace(lastSummary) || StringComparer.Ordinal.Equals(lastSummary, _speedProfileSummaryPath))
            return;
        _speedProfileSummaryPath = lastSummary;
        _speedProfileStatus = "Speed profile written.";
    }
}
