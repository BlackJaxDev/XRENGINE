using System.Collections.Concurrent;

namespace XREngine;

/// <summary>
/// Central process-environment facade used by runtime-toggleable editor settings.
/// Launch values are retained so a temporary editor override can be cleared without
/// losing an explicit value supplied by the parent process.
/// </summary>
public static class XREnvironment
{
    private static readonly ConcurrentDictionary<string, RuntimeEnvironmentVariableState> Variables =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MutationLock = new();

    static XREnvironment()
    {
        foreach (RuntimeEnvironmentVariableDescriptor descriptor in XREngineEnvironmentVariableCatalog.All)
            Variables.TryAdd(descriptor.Name, CreateState(descriptor.Name));
    }

    /// <summary>
    /// Raised after an in-process override changes the effective process value.
    /// Subscribers must keep handlers short and thread-safe.
    /// </summary>
    public static event Action<RuntimeEnvironmentVariableChange>? VariableChanged;

    /// <summary>
    /// Forces launch-value capture for the complete catalog. Call this at process entry
    /// before bootstrap code mutates the process environment.
    /// </summary>
    public static void Initialize()
    {
        // Invoking any member runs the static constructor. This explicit entry point
        // documents the required bootstrap ordering without performing extra work.
    }

    public static RuntimeEnvironmentVariableState GetState(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Variables.GetOrAdd(name, static variableName => CreateState(variableName));
    }

    public static string? GetValue(string name)
        => GetState(name).EffectiveValue;

    public static string? GetLaunchValue(string name)
        => GetState(name).LaunchValue;

    public static bool TryGetRuntimeOverride(string name, out string? value)
    {
        RuntimeEnvironmentVariableState state = GetState(name);
        value = state.RuntimeOverrideValue;
        return state.HasRuntimeOverride;
    }

    /// <summary>
    /// Supplies a persisted editor-setting value beneath explicit launch and temporary
    /// runtime overrides. A null value removes the preference layer.
    /// </summary>
    public static void SetPreferenceValue(string name, string? value)
    {
        RuntimeEnvironmentVariableState state = GetState(name);
        if (string.Equals(state.PreferenceValue, value, StringComparison.Ordinal))
            return;

        string? previous;
        lock (MutationLock)
        {
            previous = state.EffectiveValue;
            state.SetPreferenceValue(value);
            Environment.SetEnvironmentVariable(
                name,
                state.EffectiveValue,
                EnvironmentVariableTarget.Process);
        }

        if (!string.Equals(previous, state.EffectiveValue, StringComparison.Ordinal))
            PublishChange(state, previous);
    }

    /// <summary>
    /// Sets an explicit in-process override. A null value deliberately masks a launch
    /// value; use <see cref="ClearRuntimeOverride"/> to inherit the launch value again.
    /// </summary>
    public static void SetRuntimeOverride(string name, string? value)
    {
        RuntimeEnvironmentVariableState state = GetState(name);
        string? previous;
        lock (MutationLock)
        {
            previous = state.EffectiveValue;
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
            state.SetRuntimeOverride(value);
        }

        PublishChange(state, previous);
    }

    /// <summary>
    /// Removes the in-process override and restores the value captured at process launch.
    /// </summary>
    public static bool ClearRuntimeOverride(string name)
    {
        RuntimeEnvironmentVariableState state = GetState(name);
        if (!state.HasRuntimeOverride)
            return false;

        string? previous;
        lock (MutationLock)
        {
            previous = state.EffectiveValue;
            Environment.SetEnvironmentVariable(name, state.InheritedValue, EnvironmentVariableTarget.Process);
            state.ClearRuntimeOverride();
        }

        PublishChange(state, previous);
        return true;
    }

    /// <summary>
    /// Refreshes values changed by code outside this facade. Launch values remain immutable.
    /// </summary>
    public static void RefreshFromProcess()
    {
        foreach (RuntimeEnvironmentVariableState state in Variables.Values)
        {
            if (state.HasRuntimeOverride)
                continue;

            string? previous = state.EffectiveValue;
            string? current = Environment.GetEnvironmentVariable(state.Name, EnvironmentVariableTarget.Process);
            if (string.Equals(previous, current, StringComparison.Ordinal))
                continue;

            state.RefreshEffectiveValue(current);
            PublishChange(state, previous);
        }
    }

    public static bool IsEnabled(string name, bool defaultValue = false)
        => GetState(name).IsEnabled(defaultValue);

    public static bool? GetBooleanOverride(string name)
        => GetState(name).BooleanOverride;

    private static RuntimeEnvironmentVariableState CreateState(string name)
        => new(name, Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));

    private static void PublishChange(RuntimeEnvironmentVariableState state, string? previous)
    {
        var change = new RuntimeEnvironmentVariableChange(
            state.Name,
            state.LaunchValue,
            previous,
            state.EffectiveValue,
            state.HasRuntimeOverride);

        Delegate[] handlers = VariableChanged?.GetInvocationList() ?? [];
        for (int i = 0; i < handlers.Length; i++)
        {
            try
            {
                ((Action<RuntimeEnvironmentVariableChange>)handlers[i])(change);
            }
            catch
            {
                // A settings observer must not prevent the process environment from changing.
            }
        }
    }
}
