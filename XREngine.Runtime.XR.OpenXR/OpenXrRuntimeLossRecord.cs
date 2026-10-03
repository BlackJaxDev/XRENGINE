using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Immutable first-observer evidence for one OpenXR runtime-loss incident.
/// </summary>
internal readonly record struct OpenXrRuntimeLossRecord(
    OpenXRAPI.OpenXrRuntimeLossReason Reason,
    string Operation,
    Result? Result,
    DateTimeOffset ObservedAtUtc);
