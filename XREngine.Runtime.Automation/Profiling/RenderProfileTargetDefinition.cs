using XREngine.Rendering;

namespace XREngine.Runtime.Automation.Profiling;

/// <summary>A profile target and the runtime requirements needed to prepare it.</summary>
public sealed record RenderProfileTargetDefinition(
    string Name,
    string Component,
    string Fixture,
    RenderExecutionMode ExecutionMode,
    bool Supported,
    string? UnsupportedReason = null,
    IReadOnlyList<string>? Inclusions = null,
    IReadOnlyList<string>? Exclusions = null,
    bool SupportsOutputHash = false);
