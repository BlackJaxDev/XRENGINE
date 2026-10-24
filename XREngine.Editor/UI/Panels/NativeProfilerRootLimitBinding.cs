using System.Globalization;

namespace XREngine.Editor;

/// <summary>Connects one retained field to an active CPU root time limit.</summary>
internal sealed class NativeProfilerRootLimitBinding(ProfilerPanel owner, bool minimum) : IProfilerSettingBinding
{
    private readonly long _viewRevision = owner.RootViewRevision;
    public bool IsCurrent => owner.IsRootViewCurrent && owner.RootViewRevision == _viewRevision;
    public string Value => (minimum ? owner.RootMinimumMilliseconds : owner.RootMaximumMilliseconds)
        .ToString(CultureInfo.CurrentCulture);
    public event Action<string>? ValueChanged;

    public void SetValue(string value)
    {
        if (!IsCurrent)
            throw new InvalidOperationException("The profiler view changed. Reopen the field.");
        float parsed = float.Parse(value, NumberStyles.Float, CultureInfo.CurrentCulture);
        if (!float.IsFinite(parsed) || parsed < 0f || parsed > 1000f)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (!IsCurrent)
            throw new InvalidOperationException("The profiler view changed. Reopen the field.");
        owner.SetRootLimit(minimum, parsed);
        if (IsCurrent)
            ValueChanged?.Invoke(Value);
    }

    public void Refresh() => ValueChanged?.Invoke(Value);
}
