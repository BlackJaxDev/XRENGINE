namespace XREngine.Editor;

/// <summary>Captures the exact effective, override, and global preference owners for one edit.</summary>
internal sealed class NativeProfilerSettingOwner
{
    private readonly EditorPreferences _effective = Engine.EditorPreferences;
    private readonly EditorPreferencesOverrides _overrides = Engine.EditorPreferencesOverrides;
    private readonly EditorPreferences _global = Engine.GlobalEditorPreferences;
    private readonly EditorDebugOptions _effectiveDebug;
    private readonly EditorDebugOverrides _overrideDebug;
    private readonly EditorDebugOptions _globalDebug;
    private readonly object[] _overrideLeaves;

    public NativeProfilerSettingOwner()
    {
        _effectiveDebug = _effective.Debug;
        _overrideDebug = _overrides.Debug;
        _globalDebug = _global.Debug;
        _overrideLeaves =
        [
            _overrideDebug.EnableProfilerComponentTimingOverride,
            _overrideDebug.EnableProfilerFrameLoggingOverride,
            _overrideDebug.EnableRenderStatisticsTrackingOverride,
            _overrideDebug.EnableThreadAllocationTrackingOverride,
            _overrideDebug.EnableMainThreadInvokeDiagnosticsOverride,
            _overrideDebug.EnableGpuRenderPipelineProfilingOverride,
            _overrideDebug.EnableProfilerUdpSendingOverride,
            _overrideDebug.ProfilerPanelUpdateIntervalSecondsOverride,
            _overrideDebug.CodeProfilerSnapshotIntervalMsOverride,
            _overrideDebug.CodeProfilerThreadHistoryCapacityOverride,
            _overrideDebug.CodeProfilerFpsDropMinDeltaMsOverride,
            _overrideDebug.CodeProfilerDebugOutputMinElapsedMsOverride,
            _overrideDebug.CodeProfilerStatsThreadIntervalMsOverride,
            _overrideDebug.CodeProfilerMaxOverflowPerCycleOverride,
            _overrideDebug.CodeProfilerMaxOverflowQueueSizeOverride,
            _overrideDebug.CodeProfilerProducerBufferCapacityOverride,
            _overrideDebug.CodeProfilerFpsDropBaselineWindowSamplesOverride,
            _overrideDebug.CodeProfilerFpsDropMinPreviousFpsOverride,
            _overrideDebug.CodeProfilerRenderStallThresholdMsOverride,
            _overrideDebug.ProfilerPanelGraphSampleCountOverride,
            _overrideDebug.ProfilerPanelSmoothingAlphaOverride,
            _overrideDebug.ProfilerPanelShowCpuTimingRawMsLineOverride,
            _overrideDebug.ProfilerPanelShowCpuTimingSmoothedMsLineOverride,
            _overrideDebug.ProfilerPanelShowGpuTimingRawMsLineOverride,
            _overrideDebug.ProfilerPanelShowGpuTimingSmoothedMsLineOverride,
            _overrideDebug.ProfilerPanelInterpolateCpuTimingGraphsOverride,
            _overrideDebug.ProfilerPanelInterpolateGpuTimingGraphsOverride,
            _overrideDebug.ProfilerPanelCpuTimingDisplayModeOverride,
            _overrideDebug.ProfilerPanelGpuTimingDisplayModeOverride,
        ];
    }

    public bool IsCurrent =>
        ReferenceEquals(Engine.EditorPreferences, _effective) &&
        ReferenceEquals(Engine.EditorPreferencesOverrides, _overrides) &&
        ReferenceEquals(Engine.GlobalEditorPreferences, _global) &&
        ReferenceEquals(_effective.Debug, _effectiveDebug) &&
        ReferenceEquals(_overrides.Debug, _overrideDebug) &&
        ReferenceEquals(_global.Debug, _globalDebug) &&
        ReferenceEquals(_overrideLeaves[0], _overrideDebug.EnableProfilerComponentTimingOverride) &&
        ReferenceEquals(_overrideLeaves[1], _overrideDebug.EnableProfilerFrameLoggingOverride) &&
        ReferenceEquals(_overrideLeaves[2], _overrideDebug.EnableRenderStatisticsTrackingOverride) &&
        ReferenceEquals(_overrideLeaves[3], _overrideDebug.EnableThreadAllocationTrackingOverride) &&
        ReferenceEquals(_overrideLeaves[4], _overrideDebug.EnableMainThreadInvokeDiagnosticsOverride) &&
        ReferenceEquals(_overrideLeaves[5], _overrideDebug.EnableGpuRenderPipelineProfilingOverride) &&
        ReferenceEquals(_overrideLeaves[6], _overrideDebug.EnableProfilerUdpSendingOverride) &&
        ReferenceEquals(_overrideLeaves[7], _overrideDebug.ProfilerPanelUpdateIntervalSecondsOverride) &&
        ReferenceEquals(_overrideLeaves[8], _overrideDebug.CodeProfilerSnapshotIntervalMsOverride) &&
        ReferenceEquals(_overrideLeaves[9], _overrideDebug.CodeProfilerThreadHistoryCapacityOverride) &&
        ReferenceEquals(_overrideLeaves[10], _overrideDebug.CodeProfilerFpsDropMinDeltaMsOverride) &&
        ReferenceEquals(_overrideLeaves[11], _overrideDebug.CodeProfilerDebugOutputMinElapsedMsOverride) &&
        ReferenceEquals(_overrideLeaves[12], _overrideDebug.CodeProfilerStatsThreadIntervalMsOverride) &&
        ReferenceEquals(_overrideLeaves[13], _overrideDebug.CodeProfilerMaxOverflowPerCycleOverride) &&
        ReferenceEquals(_overrideLeaves[14], _overrideDebug.CodeProfilerMaxOverflowQueueSizeOverride) &&
        ReferenceEquals(_overrideLeaves[15], _overrideDebug.CodeProfilerProducerBufferCapacityOverride) &&
        ReferenceEquals(_overrideLeaves[16], _overrideDebug.CodeProfilerFpsDropBaselineWindowSamplesOverride) &&
        ReferenceEquals(_overrideLeaves[17], _overrideDebug.CodeProfilerFpsDropMinPreviousFpsOverride) &&
        ReferenceEquals(_overrideLeaves[18], _overrideDebug.CodeProfilerRenderStallThresholdMsOverride) &&
        ReferenceEquals(_overrideLeaves[19], _overrideDebug.ProfilerPanelGraphSampleCountOverride) &&
        ReferenceEquals(_overrideLeaves[20], _overrideDebug.ProfilerPanelSmoothingAlphaOverride) &&
        ReferenceEquals(_overrideLeaves[21], _overrideDebug.ProfilerPanelShowCpuTimingRawMsLineOverride) &&
        ReferenceEquals(_overrideLeaves[22], _overrideDebug.ProfilerPanelShowCpuTimingSmoothedMsLineOverride) &&
        ReferenceEquals(_overrideLeaves[23], _overrideDebug.ProfilerPanelShowGpuTimingRawMsLineOverride) &&
        ReferenceEquals(_overrideLeaves[24], _overrideDebug.ProfilerPanelShowGpuTimingSmoothedMsLineOverride) &&
        ReferenceEquals(_overrideLeaves[25], _overrideDebug.ProfilerPanelInterpolateCpuTimingGraphsOverride) &&
        ReferenceEquals(_overrideLeaves[26], _overrideDebug.ProfilerPanelInterpolateGpuTimingGraphsOverride) &&
        ReferenceEquals(_overrideLeaves[27], _overrideDebug.ProfilerPanelCpuTimingDisplayModeOverride) &&
        ReferenceEquals(_overrideLeaves[28], _overrideDebug.ProfilerPanelGpuTimingDisplayModeOverride);

    public EditorDebugOptions EffectiveDebug => _effectiveDebug;
    public EditorDebugOverrides OverrideDebug => _overrideDebug;
    public EditorDebugOptions GlobalDebug => _globalDebug;
}
