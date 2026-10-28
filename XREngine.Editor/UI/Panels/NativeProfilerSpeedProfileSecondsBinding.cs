using System.Globalization;

namespace XREngine.Editor;

/// <summary>Connects one retained integer field to the native speed profile duration.</summary>
internal sealed class NativeProfilerSpeedProfileSecondsBinding(ProfilerPanel owner) : IProfilerSettingBinding
{
    internal const int MinimumSeconds = 1;
    internal const int MaximumSeconds = 600;

    public bool IsCurrent => owner.IsProfilerOwnerCurrent;
    public string Value => owner.SpeedProfileSeconds.ToString(CultureInfo.CurrentCulture);
    public event Action<string>? ValueChanged;

    public void SetValue(string value)
    {
        if (!IsCurrent)
            throw new InvalidOperationException("The profiler view changed. Reopen the field.");
        int parsed = int.Parse(value, NumberStyles.Integer, CultureInfo.CurrentCulture);
        if (parsed < MinimumSeconds || parsed > MaximumSeconds)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (!IsCurrent)
            throw new InvalidOperationException("The profiler view changed. Reopen the field.");
        owner.SpeedProfileSeconds = parsed;
        if (IsCurrent)
            ValueChanged?.Invoke(Value);
    }

    public void Refresh() => ValueChanged?.Invoke(Value);
}
