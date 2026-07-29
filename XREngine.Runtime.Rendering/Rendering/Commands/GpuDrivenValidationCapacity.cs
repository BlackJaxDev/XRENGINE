using System;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Applies explicit capacity-only controls used by GPU-driven acceptance
/// validation. Active counts are never changed by this helper.
/// </summary>
internal static class GpuDrivenValidationCapacity
{
    internal static uint Multiplier { get; } = ResolveMultiplier(
        Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.GpuDrivenValidationCapacityMultiplier));

    internal static uint Floor { get; } = ResolveFloor(
        Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.GpuDrivenValidationCapacityFloor));

    internal static uint Apply(uint requiredCapacity)
        => Apply(requiredCapacity, Multiplier, Floor);

    internal static uint Apply(uint requiredCapacity, uint multiplier, uint floor)
        => Math.Max(Scale(requiredCapacity, multiplier), floor);

    internal static uint Scale(uint requiredCapacity)
        => Scale(requiredCapacity, Multiplier);

    internal static uint Scale(uint requiredCapacity, uint multiplier)
    {
        ulong scaled = (ulong)Math.Max(requiredCapacity, 1u) * multiplier;
        return (uint)Math.Min(scaled, int.MaxValue);
    }

    internal static uint ResolveMultiplier(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return 1u;

        if (uint.TryParse(raw, out uint multiplier) &&
            multiplier is 1u or 4u or 16u)
        {
            return multiplier;
        }

        throw new InvalidOperationException(
            $"{XREngineEnvironmentVariables.GpuDrivenValidationCapacityMultiplier} " +
            "must be 1, 4, or 16 when set.");
    }

    internal static uint ResolveFloor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return 0u;

        if (uint.TryParse(raw, out uint floor) && floor <= int.MaxValue)
            return floor;

        throw new InvalidOperationException(
            $"{XREngineEnvironmentVariables.GpuDrivenValidationCapacityFloor} " +
            $"must be between 0 and {int.MaxValue} when set.");
    }
}
