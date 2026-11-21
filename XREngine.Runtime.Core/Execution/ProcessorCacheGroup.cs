namespace XREngine.Execution;

/// <summary>
/// One last-level cache and the logical processors that share it, in one
/// processor group.
/// </summary>
/// <param name="Group">The processor group of <paramref name="Mask"/>.</param>
/// <param name="Mask">The logical processors in <paramref name="Group"/> that share the cache.</param>
/// <param name="SizeBytes">The cache size in bytes.</param>
public readonly record struct ProcessorCacheGroup(ushort Group, ulong Mask, uint SizeBytes);
