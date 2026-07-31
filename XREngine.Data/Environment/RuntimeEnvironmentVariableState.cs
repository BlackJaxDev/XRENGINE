using System.Threading;

namespace XREngine;

/// <summary>
/// Thread-safe state for one launch value and its optional in-process override.
/// </summary>
public sealed class RuntimeEnvironmentVariableState
{
    private string? _effectiveValue;
    private string? _runtimeOverrideValue;
    private string? _preferenceValue;
    private int _booleanValue;
    private int _hasRuntimeOverride;

    internal RuntimeEnvironmentVariableState(string name, string? launchValue)
    {
        Name = name;
        LaunchValue = launchValue;
        _effectiveValue = launchValue;
        _booleanValue = ParseBoolean(launchValue);
    }

    public string Name { get; }
    public string? LaunchValue { get; }
    public string? EffectiveValue => Volatile.Read(ref _effectiveValue);
    public bool HasRuntimeOverride => Volatile.Read(ref _hasRuntimeOverride) != 0;
    public string? RuntimeOverrideValue => Volatile.Read(ref _runtimeOverrideValue);
    public string? PreferenceValue => Volatile.Read(ref _preferenceValue);
    public string? InheritedValue => LaunchValue ?? PreferenceValue;

    /// <summary>
    /// Reads the effective value as a boolean without reparsing the environment string.
    /// Invalid or unset values use <paramref name="defaultValue"/>.
    /// </summary>
    public bool IsEnabled(bool defaultValue = false)
        => Volatile.Read(ref _booleanValue) switch
        {
            1 => true,
            0 => false,
            _ => defaultValue,
        };

    /// <summary>
    /// Gets the parsed effective boolean, or <see langword="null"/> when the value is
    /// unset or is not a recognized boolean token.
    /// </summary>
    public bool? BooleanOverride
        => Volatile.Read(ref _booleanValue) switch
        {
            1 => true,
            0 => false,
            _ => null,
        };

    internal void SetRuntimeOverride(string? value)
    {
        Volatile.Write(ref _runtimeOverrideValue, value);
        Volatile.Write(ref _hasRuntimeOverride, 1);
        SetEffectiveValue(value);
    }

    internal void ClearRuntimeOverride()
    {
        Volatile.Write(ref _runtimeOverrideValue, null);
        Volatile.Write(ref _hasRuntimeOverride, 0);
        SetEffectiveValue(InheritedValue);
    }

    internal void SetPreferenceValue(string? value)
    {
        Volatile.Write(ref _preferenceValue, value);
        if (!HasRuntimeOverride)
            SetEffectiveValue(LaunchValue ?? value);
    }

    internal void RefreshEffectiveValue(string? value)
        => SetEffectiveValue(value);

    private void SetEffectiveValue(string? value)
    {
        Volatile.Write(ref _booleanValue, ParseBoolean(value));
        Volatile.Write(ref _effectiveValue, value);
    }

    private static int ParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return -1;

        string normalized = value.Trim();
        if (normalized is "1" ||
            normalized.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (normalized is "0" ||
            normalized.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return -1;
    }
}
