namespace XREngine;

/// <summary>
/// Describes an effective process-environment change made through <see cref="XREnvironment"/>.
/// </summary>
public sealed record RuntimeEnvironmentVariableChange(
    string Name,
    string? LaunchValue,
    string? PreviousValue,
    string? EffectiveValue,
    bool HasRuntimeOverride);
