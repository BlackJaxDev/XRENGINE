namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Identifies native image contents without a dependency on a graphics API.</summary>
internal readonly record struct TemporalHistoryResourceIdentity(
    object? BackendOwner,
    ulong ImageHandle,
    ulong ImageGeneration,
    uint AspectMask,
    uint BaseMipLevel,
    uint LevelCount,
    uint BaseArrayLayer,
    uint LayerCount)
{
    public bool IsValid => BackendOwner is not null && ImageHandle != 0 &&
        ImageGeneration != 0 && AspectMask != 0 && LevelCount != 0 && LayerCount != 0;

    /// <summary>Checks whether accepted writes contain the exact sampled image range.</summary>
    public bool Contains(in TemporalHistoryResourceIdentity read)
        => IsValid && read.IsValid && ReferenceEquals(BackendOwner, read.BackendOwner) &&
           ImageHandle == read.ImageHandle && ImageGeneration == read.ImageGeneration &&
           (AspectMask & read.AspectMask) == read.AspectMask &&
           BaseMipLevel <= read.BaseMipLevel &&
           (ulong)BaseMipLevel + LevelCount >= (ulong)read.BaseMipLevel + read.LevelCount &&
           BaseArrayLayer <= read.BaseArrayLayer &&
           (ulong)BaseArrayLayer + LayerCount >= (ulong)read.BaseArrayLayer + read.LayerCount;

    public bool Matches(in TemporalHistoryResourceIdentity other)
        => ReferenceEquals(BackendOwner, other.BackendOwner) && ImageHandle == other.ImageHandle &&
           ImageGeneration == other.ImageGeneration && AspectMask == other.AspectMask &&
           BaseMipLevel == other.BaseMipLevel && LevelCount == other.LevelCount &&
           BaseArrayLayer == other.BaseArrayLayer && LayerCount == other.LayerCount;
}
