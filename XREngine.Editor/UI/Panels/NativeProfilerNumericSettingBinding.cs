using System.Globalization;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>Connects one native numeric field to an editor profiler preference.</summary>
internal sealed class NativeProfilerNumericSettingBinding(
    ENativeProfilerSetting setting, NativeProfilerSettingOwner owner) : IProfilerSettingBinding
{
    private Action<string>? _changed;
    private readonly object _overrideSetting = ResolveOverride(setting, owner.OverrideDebug);

    public bool IsCurrent => owner.IsCurrent &&
        ReferenceEquals(_overrideSetting, ResolveOverride(setting, owner.OverrideDebug));

    public string Value
    {
        get
        {
            var debug = owner.EffectiveDebug;
            return setting switch
            {
                ENativeProfilerSetting.SnapshotInterval => debug.CodeProfilerSnapshotIntervalMs.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.HistoryCapacity => debug.CodeProfilerThreadHistoryCapacity.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.DropThreshold => debug.CodeProfilerFpsDropMinDeltaMs.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.DebugOutputMinimum => debug.CodeProfilerDebugOutputMinElapsedMs.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.StatsThreadInterval => debug.CodeProfilerStatsThreadIntervalMs.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.MaxOverflowPerCycle => debug.CodeProfilerMaxOverflowPerCycle.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.MaxOverflowQueueSize => debug.CodeProfilerMaxOverflowQueueSize.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.ProducerBufferCapacity => debug.CodeProfilerProducerBufferCapacity.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.FpsBaselineWindow => debug.CodeProfilerFpsDropBaselineWindowSamples.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.FpsMinimumPrevious => debug.CodeProfilerFpsDropMinPreviousFps.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.RenderStallThreshold => debug.CodeProfilerRenderStallThresholdMs.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.GraphSamples => debug.ProfilerPanelGraphSampleCount.ToString(CultureInfo.CurrentCulture),
                ENativeProfilerSetting.GraphSmoothing => debug.ProfilerPanelSmoothingAlpha.ToString(CultureInfo.CurrentCulture),
                _ => string.Empty,
            };
        }
    }

    public event Action<string>? ValueChanged
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public void SetValue(string value)
    {
        if (!IsCurrent)
            throw new InvalidOperationException("The profiler settings owner changed. Reopen the field.");
        switch (setting)
        {
            case ENativeProfilerSetting.SnapshotInterval:
                NativeProfilerSettingWriter.Set(owner, int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture),
                    static debug => debug.CodeProfilerSnapshotIntervalMs,
                    static overrides => overrides.CodeProfilerSnapshotIntervalMsOverride,
                    static (debug, parsed) => debug.CodeProfilerSnapshotIntervalMs = parsed);
                break;
            case ENativeProfilerSetting.HistoryCapacity:
                NativeProfilerSettingWriter.Set(owner, int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture),
                    static debug => debug.CodeProfilerThreadHistoryCapacity,
                    static overrides => overrides.CodeProfilerThreadHistoryCapacityOverride,
                    static (debug, parsed) => debug.CodeProfilerThreadHistoryCapacity = parsed);
                break;
            case ENativeProfilerSetting.DropThreshold:
                NativeProfilerSettingWriter.Set(owner, ParseFiniteFloat(value),
                    static debug => debug.CodeProfilerFpsDropMinDeltaMs,
                    static overrides => overrides.CodeProfilerFpsDropMinDeltaMsOverride,
                    static (debug, parsed) => debug.CodeProfilerFpsDropMinDeltaMs = parsed);
                break;
            case ENativeProfilerSetting.DebugOutputMinimum:
                NativeProfilerSettingWriter.Set(owner, ParseFiniteFloat(value),
                    static debug => debug.CodeProfilerDebugOutputMinElapsedMs,
                    static overrides => overrides.CodeProfilerDebugOutputMinElapsedMsOverride,
                    static (debug, parsed) => debug.CodeProfilerDebugOutputMinElapsedMs = parsed);
                break;
            case ENativeProfilerSetting.StatsThreadInterval:
                NativeProfilerSettingWriter.Set(owner, int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture),
                    static debug => debug.CodeProfilerStatsThreadIntervalMs,
                    static overrides => overrides.CodeProfilerStatsThreadIntervalMsOverride,
                    static (debug, parsed) => debug.CodeProfilerStatsThreadIntervalMs = parsed);
                break;
            case ENativeProfilerSetting.MaxOverflowPerCycle:
                NativeProfilerSettingWriter.Set(owner, int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture),
                    static debug => debug.CodeProfilerMaxOverflowPerCycle,
                    static overrides => overrides.CodeProfilerMaxOverflowPerCycleOverride,
                    static (debug, parsed) => debug.CodeProfilerMaxOverflowPerCycle = parsed);
                break;
            case ENativeProfilerSetting.MaxOverflowQueueSize:
                NativeProfilerSettingWriter.Set(owner, int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture),
                    static debug => debug.CodeProfilerMaxOverflowQueueSize,
                    static overrides => overrides.CodeProfilerMaxOverflowQueueSizeOverride,
                    static (debug, parsed) => debug.CodeProfilerMaxOverflowQueueSize = parsed);
                break;
            case ENativeProfilerSetting.ProducerBufferCapacity:
                NativeProfilerSettingWriter.Set(owner, int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture),
                    static debug => debug.CodeProfilerProducerBufferCapacity,
                    static overrides => overrides.CodeProfilerProducerBufferCapacityOverride,
                    static (debug, parsed) => debug.CodeProfilerProducerBufferCapacity = parsed);
                break;
            case ENativeProfilerSetting.FpsBaselineWindow:
                NativeProfilerSettingWriter.Set(owner, int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture),
                    static debug => debug.CodeProfilerFpsDropBaselineWindowSamples,
                    static overrides => overrides.CodeProfilerFpsDropBaselineWindowSamplesOverride,
                    static (debug, parsed) => debug.CodeProfilerFpsDropBaselineWindowSamples = parsed);
                break;
            case ENativeProfilerSetting.FpsMinimumPrevious:
                NativeProfilerSettingWriter.Set(owner, ParseFiniteFloat(value),
                    static debug => debug.CodeProfilerFpsDropMinPreviousFps,
                    static overrides => overrides.CodeProfilerFpsDropMinPreviousFpsOverride,
                    static (debug, parsed) => debug.CodeProfilerFpsDropMinPreviousFps = parsed);
                break;
            case ENativeProfilerSetting.RenderStallThreshold:
                NativeProfilerSettingWriter.Set(owner, ParseFiniteFloat(value),
                    static debug => debug.CodeProfilerRenderStallThresholdMs,
                    static overrides => overrides.CodeProfilerRenderStallThresholdMsOverride,
                    static (debug, parsed) => debug.CodeProfilerRenderStallThresholdMs = parsed);
                break;
            case ENativeProfilerSetting.GraphSamples:
                NativeProfilerSettingWriter.Set(owner, int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture),
                    static debug => debug.ProfilerPanelGraphSampleCount,
                    static overrides => overrides.ProfilerPanelGraphSampleCountOverride,
                    static (debug, parsed) => debug.ProfilerPanelGraphSampleCount = parsed);
                break;
            case ENativeProfilerSetting.GraphSmoothing:
                NativeProfilerSettingWriter.Set(owner, ParseFiniteFloat(value),
                    static debug => debug.ProfilerPanelSmoothingAlpha,
                    static overrides => overrides.ProfilerPanelSmoothingAlphaOverride,
                    static (debug, parsed) => debug.ProfilerPanelSmoothingAlpha = parsed);
                break;
            default:
                throw new InvalidOperationException("This profiler setting is not numeric.");
        }
        if (IsCurrent)
            _changed?.Invoke(Value);
    }

    public void Refresh() => _changed?.Invoke(Value);

    private static float ParseFiniteFloat(string value)
    {
        float parsed = float.Parse(value, NumberStyles.Float, CultureInfo.CurrentCulture);
        return float.IsFinite(parsed) ? parsed : throw new FormatException("A profiler setting needs a finite value.");
    }

    private static object ResolveOverride(ENativeProfilerSetting setting, EditorDebugOverrides debug)
        => setting switch
        {
            ENativeProfilerSetting.SnapshotInterval => debug.CodeProfilerSnapshotIntervalMsOverride,
            ENativeProfilerSetting.HistoryCapacity => debug.CodeProfilerThreadHistoryCapacityOverride,
            ENativeProfilerSetting.DropThreshold => debug.CodeProfilerFpsDropMinDeltaMsOverride,
            ENativeProfilerSetting.DebugOutputMinimum => debug.CodeProfilerDebugOutputMinElapsedMsOverride,
            ENativeProfilerSetting.StatsThreadInterval => debug.CodeProfilerStatsThreadIntervalMsOverride,
            ENativeProfilerSetting.MaxOverflowPerCycle => debug.CodeProfilerMaxOverflowPerCycleOverride,
            ENativeProfilerSetting.MaxOverflowQueueSize => debug.CodeProfilerMaxOverflowQueueSizeOverride,
            ENativeProfilerSetting.ProducerBufferCapacity => debug.CodeProfilerProducerBufferCapacityOverride,
            ENativeProfilerSetting.FpsBaselineWindow => debug.CodeProfilerFpsDropBaselineWindowSamplesOverride,
            ENativeProfilerSetting.FpsMinimumPrevious => debug.CodeProfilerFpsDropMinPreviousFpsOverride,
            ENativeProfilerSetting.RenderStallThreshold => debug.CodeProfilerRenderStallThresholdMsOverride,
            ENativeProfilerSetting.GraphSamples => debug.ProfilerPanelGraphSampleCountOverride,
            ENativeProfilerSetting.GraphSmoothing => debug.ProfilerPanelSmoothingAlphaOverride,
            _ => throw new InvalidOperationException("This profiler setting is not persistent numeric input."),
        };
}
