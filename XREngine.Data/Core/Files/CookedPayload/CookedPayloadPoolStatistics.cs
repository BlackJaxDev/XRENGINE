namespace XREngine.Core.Files;

/// <summary>Counters for the cooked payload buffer pool, in the same shape as the other hot-path pool statistics.</summary>
public readonly record struct CookedPayloadPoolStatistics(
    long RentCount,
    long RentMissCount,
    long NativeRentCount,
    long ReturnCount,
    long ReleaseOverflowCount,
    long RetainedBytes,
    long RetainedBytesHighWater,
    long ActiveNativeBytes,
    long ActiveNativeBytesHighWater);
