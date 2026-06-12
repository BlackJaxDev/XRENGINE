using System.Text;
using System.Threading;

namespace XREngine.Rendering;

public readonly record struct XRBufferWriteTelemetrySnapshot(
    long FrameId,
    long DeviceLocalUploadBytes,
    long StagingUploadBytes,
    long HostVisibleUploadBytes,
    long PersistentRingUploadBytes,
    long ReadbackBytes,
    long DiagnosticSharedBytes,
    long CompatibilityPushBytes,
    int StagingAllocations,
    int StagingReuses,
    int PersistentRingAllocations,
    int PersistentRingExhaustions,
    int PersistentRingFenceWaits,
    int HostVisibleWrites,
    int HostCachedReadbacks,
    int DeviceAddressConsumers,
    int DescriptorFallbacks,
    int ZeroReadbackViolations);

/// <summary>
/// Lightweight per-frame counters for XRDataBuffer write/readback routing.
/// </summary>
public static class XRBufferWriteTelemetry
{
    private static long s_frameId = long.MinValue;
    private static long s_deviceLocalUploadBytes;
    private static long s_stagingUploadBytes;
    private static long s_hostVisibleUploadBytes;
    private static long s_persistentRingUploadBytes;
    private static long s_readbackBytes;
    private static long s_diagnosticSharedBytes;
    private static long s_compatibilityPushBytes;
    private static int s_stagingAllocations;
    private static int s_stagingReuses;
    private static int s_persistentRingAllocations;
    private static int s_persistentRingExhaustions;
    private static int s_persistentRingFenceWaits;
    private static int s_hostVisibleWrites;
    private static int s_hostCachedReadbacks;
    private static int s_deviceAddressConsumers;
    private static int s_descriptorFallbacks;
    private static int s_zeroReadbackViolations;

    public static void RecordUpload(XRBufferResolvedRoute route, long bytes)
    {
        if (bytes <= 0)
            return;

        EnsureFrame();
        switch (route)
        {
            case XRBufferResolvedRoute.DeviceLocal:
                Interlocked.Add(ref s_deviceLocalUploadBytes, bytes);
                break;
            case XRBufferResolvedRoute.StagingUpload:
                Interlocked.Add(ref s_stagingUploadBytes, bytes);
                break;
            case XRBufferResolvedRoute.HostVisible:
                Interlocked.Add(ref s_hostVisibleUploadBytes, bytes);
                Interlocked.Increment(ref s_hostVisibleWrites);
                break;
            case XRBufferResolvedRoute.PersistentMappedRing:
                Interlocked.Add(ref s_persistentRingUploadBytes, bytes);
                break;
            case XRBufferResolvedRoute.Readback:
                Interlocked.Add(ref s_readbackBytes, bytes);
                break;
            case XRBufferResolvedRoute.DiagnosticShared:
                Interlocked.Add(ref s_diagnosticSharedBytes, bytes);
                break;
            case XRBufferResolvedRoute.CompatibilityPush:
                Interlocked.Add(ref s_compatibilityPushBytes, bytes);
                break;
        }
    }

    public static void RecordStagingAllocation(bool reused)
    {
        EnsureFrame();
        if (reused)
            Interlocked.Increment(ref s_stagingReuses);
        else
            Interlocked.Increment(ref s_stagingAllocations);
    }

    public static void RecordPersistentRingAllocation(long bytes)
    {
        if (bytes <= 0)
            return;

        EnsureFrame();
        Interlocked.Increment(ref s_persistentRingAllocations);
        Interlocked.Add(ref s_persistentRingUploadBytes, bytes);
    }

    public static void RecordPersistentRingExhaustion()
    {
        EnsureFrame();
        Interlocked.Increment(ref s_persistentRingExhaustions);
    }

    public static void RecordPersistentRingFenceWait()
    {
        EnsureFrame();
        Interlocked.Increment(ref s_persistentRingFenceWaits);
    }

    public static void RecordHostCachedReadback(long bytes)
    {
        if (bytes <= 0)
            return;

        EnsureFrame();
        Interlocked.Increment(ref s_hostCachedReadbacks);
        Interlocked.Add(ref s_readbackBytes, bytes);
    }

    public static void RecordDeviceAddressConsumer(bool consumed)
    {
        EnsureFrame();
        if (consumed)
            Interlocked.Increment(ref s_deviceAddressConsumers);
        else
            Interlocked.Increment(ref s_descriptorFallbacks);
    }

    public static void RecordZeroReadbackViolation()
    {
        EnsureFrame();
        Interlocked.Increment(ref s_zeroReadbackViolations);
    }

    public static XRBufferWriteTelemetrySnapshot GetSnapshot()
    {
        EnsureFrame();
        return new XRBufferWriteTelemetrySnapshot(
            Volatile.Read(ref s_frameId),
            Volatile.Read(ref s_deviceLocalUploadBytes),
            Volatile.Read(ref s_stagingUploadBytes),
            Volatile.Read(ref s_hostVisibleUploadBytes),
            Volatile.Read(ref s_persistentRingUploadBytes),
            Volatile.Read(ref s_readbackBytes),
            Volatile.Read(ref s_diagnosticSharedBytes),
            Volatile.Read(ref s_compatibilityPushBytes),
            Volatile.Read(ref s_stagingAllocations),
            Volatile.Read(ref s_stagingReuses),
            Volatile.Read(ref s_persistentRingAllocations),
            Volatile.Read(ref s_persistentRingExhaustions),
            Volatile.Read(ref s_persistentRingFenceWaits),
            Volatile.Read(ref s_hostVisibleWrites),
            Volatile.Read(ref s_hostCachedReadbacks),
            Volatile.Read(ref s_deviceAddressConsumers),
            Volatile.Read(ref s_descriptorFallbacks),
            Volatile.Read(ref s_zeroReadbackViolations));
    }

    public static void AppendProfilerSummary(StringBuilder builder)
    {
        XRBufferWriteTelemetrySnapshot snapshot = GetSnapshot();
        builder.Append("XRBufferWriteFrame: ").Append(snapshot.FrameId).AppendLine();
        builder.Append("XRBufferUploadDeviceLocalBytes: ").Append(snapshot.DeviceLocalUploadBytes).AppendLine();
        builder.Append("XRBufferUploadStagingBytes: ").Append(snapshot.StagingUploadBytes).AppendLine();
        builder.Append("XRBufferUploadHostVisibleBytes: ").Append(snapshot.HostVisibleUploadBytes).AppendLine();
        builder.Append("XRBufferUploadPersistentRingBytes: ").Append(snapshot.PersistentRingUploadBytes).AppendLine();
        builder.Append("XRBufferUploadCompatibilityPushBytes: ").Append(snapshot.CompatibilityPushBytes).AppendLine();
        builder.Append("XRBufferReadbackBytes: ").Append(snapshot.ReadbackBytes).AppendLine();
        builder.Append("XRBufferDiagnosticSharedBytes: ").Append(snapshot.DiagnosticSharedBytes).AppendLine();
        builder.Append("XRBufferStagingAllocations: ").Append(snapshot.StagingAllocations).AppendLine();
        builder.Append("XRBufferStagingReuses: ").Append(snapshot.StagingReuses).AppendLine();
        builder.Append("XRBufferPersistentRingAllocations: ").Append(snapshot.PersistentRingAllocations).AppendLine();
        builder.Append("XRBufferPersistentRingExhaustions: ").Append(snapshot.PersistentRingExhaustions).AppendLine();
        builder.Append("XRBufferPersistentRingFenceWaits: ").Append(snapshot.PersistentRingFenceWaits).AppendLine();
        builder.Append("XRBufferHostVisibleWrites: ").Append(snapshot.HostVisibleWrites).AppendLine();
        builder.Append("XRBufferHostCachedReadbacks: ").Append(snapshot.HostCachedReadbacks).AppendLine();
        builder.Append("XRBufferDeviceAddressConsumers: ").Append(snapshot.DeviceAddressConsumers).AppendLine();
        builder.Append("XRBufferDescriptorFallbacks: ").Append(snapshot.DescriptorFallbacks).AppendLine();
        builder.Append("XRBufferZeroReadbackViolations: ").Append(snapshot.ZeroReadbackViolations).AppendLine();
    }

    private static void EnsureFrame()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Volatile.Read(ref s_frameId);
        if (previousFrame == currentFrame)
            return;

        if (Interlocked.CompareExchange(ref s_frameId, currentFrame, previousFrame) != previousFrame)
            return;

        Interlocked.Exchange(ref s_deviceLocalUploadBytes, 0L);
        Interlocked.Exchange(ref s_stagingUploadBytes, 0L);
        Interlocked.Exchange(ref s_hostVisibleUploadBytes, 0L);
        Interlocked.Exchange(ref s_persistentRingUploadBytes, 0L);
        Interlocked.Exchange(ref s_readbackBytes, 0L);
        Interlocked.Exchange(ref s_diagnosticSharedBytes, 0L);
        Interlocked.Exchange(ref s_compatibilityPushBytes, 0L);
        Interlocked.Exchange(ref s_stagingAllocations, 0);
        Interlocked.Exchange(ref s_stagingReuses, 0);
        Interlocked.Exchange(ref s_persistentRingAllocations, 0);
        Interlocked.Exchange(ref s_persistentRingExhaustions, 0);
        Interlocked.Exchange(ref s_persistentRingFenceWaits, 0);
        Interlocked.Exchange(ref s_hostVisibleWrites, 0);
        Interlocked.Exchange(ref s_hostCachedReadbacks, 0);
        Interlocked.Exchange(ref s_deviceAddressConsumers, 0);
        Interlocked.Exchange(ref s_descriptorFallbacks, 0);
        Interlocked.Exchange(ref s_zeroReadbackViolations, 0);
    }
}
