namespace XREngine.RenderBench;

/// <summary>One load/unload cycle. LOH sizes describe GC snapshots, not allocation counts.</summary>
public sealed record RuntimeAssetMeasurement(
    string Identity, string ContentSha256, string LoadPath, int Repeat, int Cycle,
    long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections,
    double LoadMilliseconds, double UnloadMilliseconds, long ArchiveOpens,
    long LohBytesBefore, long LohBytesAfter, long HeapBytesAfter,
    long PoolRetainedBytes, long PoolActiveNativeBytes);
