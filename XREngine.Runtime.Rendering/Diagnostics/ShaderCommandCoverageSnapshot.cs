using System.Collections.Generic;

namespace XREngine.Rendering;

/// <summary>Serialized coverage data for MCP and diagnostics consumers.</summary>
public sealed record ShaderCommandCoverageSnapshot(
    bool Enabled,
    int Capacity,
    long DroppedRegistrations,
    IReadOnlyList<ShaderCommandCoverageEntrySnapshot> Entries)
{
    /// <summary>Documents work intentionally absent from this bounded collector.</summary>
    public const string Exclusions = "Raw native advanced commands without XRShader identity, raw OpenGL multi-draw submission outside the scoped mesh path, Vulkan mesh-task native recording, and cached-command replay are excluded. AuthoredSourceSha256 hashes only authored source text; it is not a preprocessed or compiler-artifact identity. Vulkan entries are Recorded commands, not GPU-completed work. Snapshot counters are individually atomic but may change while a snapshot is assembled.";
}
