namespace XREngine.Execution;

/// <summary>
/// The result of <see cref="ProcessorCacheAffinity.Select"/>: the logical processors that
/// share the largest last-level cache, or the reason that no restriction applies.
/// </summary>
/// <param name="Applies">True when the process should be restricted to <paramref name="Mask"/>.</param>
/// <param name="Group">The processor group of <paramref name="Mask"/>.</param>
/// <param name="Mask">The selected logical processors.</param>
/// <param name="LogicalProcessorCount">The number of bits in <paramref name="Mask"/>.</param>
/// <param name="Reason">A short description of the decision for the startup log.</param>
public readonly record struct ProcessorCacheAffinitySelection(
    bool Applies,
    ushort Group,
    ulong Mask,
    int LogicalProcessorCount,
    string Reason);
