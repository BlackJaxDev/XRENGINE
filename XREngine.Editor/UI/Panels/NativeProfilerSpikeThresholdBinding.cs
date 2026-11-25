using System.Globalization;

namespace XREngine.Editor;

/// <summary>Connects one retained numeric field to native spike filtering.</summary>
internal sealed class NativeProfilerSpikeThresholdBinding(ProfilerPanel owner) : IProfilerSettingBinding
{
    public bool IsCurrent => owner.IsProfilerOwnerCurrent;
    public string Value => owner.MinimumSpikeDropFps.ToString(CultureInfo.CurrentCulture);
    public event Action<string>? ValueChanged;

    public void SetValue(string value)
    {
        if (!IsCurrent)
            throw new InvalidOperationException("The profiler view changed. Reopen the field.");
        float parsed = float.Parse(value, NumberStyles.Float, CultureInfo.CurrentCulture);
        if (!float.IsFinite(parsed) || parsed < 0f)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (!IsCurrent)
            throw new InvalidOperationException("The profiler view changed. Reopen the field.");
        owner.MinimumSpikeDropFps = parsed;
        if (IsCurrent)
            ValueChanged?.Invoke(Value);
    }

    public void Refresh() => ValueChanged?.Invoke(Value);
}
