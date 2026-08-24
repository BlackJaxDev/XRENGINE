namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable identity for one coherent canonical-scene publication.
/// Generations identify the independently uploadable images referenced by the
/// publication; <see cref="Sequence"/> orders all consumers of this database.
/// </summary>
public readonly record struct AdvancedGpuScenePublication(
    ulong DatabaseEpoch,
    ulong Sequence,
    ulong FrameGeneration,
    ulong TopologyGeneration,
    ulong ContentGeneration,
    ulong LookupGeneration)
{
    public bool IsValid => DatabaseEpoch != 0u && Sequence != 0u;
}
