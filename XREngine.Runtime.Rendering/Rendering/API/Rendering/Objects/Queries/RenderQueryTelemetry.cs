namespace XREngine.Rendering;

/// <summary>
/// Low-overhead process counters for query allocation, recording, polling, and transfer.
/// </summary>
public static class RenderQueryTelemetry
{
    private static readonly long[] s_recordingsByKind = new long[Enum.GetValues<ERenderQueryKind>().Length];
    private static readonly long[] s_readsByStatus = new long[Enum.GetValues<ERenderQueryReadStatus>().Length];
    private static long s_allocations;
    private static long s_releases;
    private static long s_waits;
    private static long s_hostReadBytes;
    private static long s_copiedBytes;
    private static long s_unsupported;

    public static long Allocations => Volatile.Read(ref s_allocations);
    public static long Releases => Volatile.Read(ref s_releases);
    public static long Waits => Volatile.Read(ref s_waits);
    public static long HostReadBytes => Volatile.Read(ref s_hostReadBytes);
    public static long CopiedBytes => Volatile.Read(ref s_copiedBytes);
    public static long Unsupported => Volatile.Read(ref s_unsupported);

    public static long GetRecordingCount(ERenderQueryKind kind)
        => Volatile.Read(ref s_recordingsByKind[(int)kind]);

    public static long GetReadCount(ERenderQueryReadStatus status)
        => Volatile.Read(ref s_readsByStatus[(int)status]);

    public static void RecordAllocation() => Interlocked.Increment(ref s_allocations);
    public static void RecordRelease() => Interlocked.Increment(ref s_releases);
    public static void RecordWait() => Interlocked.Increment(ref s_waits);
    public static void RecordRecording(ERenderQueryKind kind) => Interlocked.Increment(ref s_recordingsByKind[(int)kind]);
    public static void RecordRead(ERenderQueryReadStatus status) => Interlocked.Increment(ref s_readsByStatus[(int)status]);
    public static void RecordHostReadBytes(long bytes) => Interlocked.Add(ref s_hostReadBytes, Math.Max(bytes, 0));
    public static void RecordCopiedBytes(long bytes) => Interlocked.Add(ref s_copiedBytes, Math.Max(bytes, 0));
    public static void RecordUnsupported() => Interlocked.Increment(ref s_unsupported);
}
