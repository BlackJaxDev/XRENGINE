namespace XREngine.Rendering.Commands;

/// <summary>
/// Identifies one reserved, not-yet-visible canonical publication ring entry.
/// Only the owning shared database can prepare, commit, or fault this token.
/// </summary>
internal readonly record struct AdvancedGpuScenePublicationTransaction(
    ulong DatabaseEpoch,
    ulong Sequence,
    int RingIndex)
{
    public bool IsValid
        => DatabaseEpoch != 0u && Sequence != 0u && RingIndex >= 0;
}
