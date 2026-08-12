using System;
using System.Threading;

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable allocation-free descriptor publication telemetry snapshot.</summary>
internal readonly record struct VulkanDescriptorPublicationTelemetrySnapshot(
    ulong Scanned,
    ulong Dirty,
    ulong DirtyRanges,
    ulong InfoElements,
    ulong WriteElements,
    ulong NativeBytes,
    ulong CompatibilityTicks,
    int HighWaterMark);

/// <summary>
/// Keeps the hot publication counters separate from diagnostic naming. Resource fingerprints
/// remain the authority for invalidating recorded artifacts; this only reports the resulting work.
/// </summary>
internal sealed class VulkanDescriptorPublicationTelemetry
{
    private long _scanned;
    private long _dirty;
    private long _ranges;
    private long _infos;
    private long _writes;
    private long _bytes;
    private long _ticks;
    private int _highWater;

    internal void Record(int scanned, int dirty, int ranges, int infoElements, int writeElements, ulong compatibilityTicks)
    {
        if (scanned > 0) Interlocked.Add(ref _scanned, scanned);
        if (dirty > 0) Interlocked.Add(ref _dirty, dirty);
        if (ranges > 0) Interlocked.Add(ref _ranges, ranges);
        if (infoElements > 0) Interlocked.Add(ref _infos, infoElements);
        if (writeElements > 0) Interlocked.Add(ref _writes, writeElements);
        if (compatibilityTicks > 0) Interlocked.Add(ref _ticks, unchecked((long)compatibilityTicks));
        if (dirty > 0)
        {
            Interlocked.Add(ref _bytes, (long)((ulong)infoElements * (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<DescriptorImageInfo>() +
                (ulong)writeElements * (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<WriteDescriptorSet>()));
            int observed;
            while ((observed = Volatile.Read(ref _highWater)) < dirty &&
                Interlocked.CompareExchange(ref _highWater, dirty, observed) != observed) { }
        }
    }

    internal VulkanDescriptorPublicationTelemetrySnapshot Snapshot() => new(
        unchecked((ulong)Interlocked.Read(ref _scanned)), unchecked((ulong)Interlocked.Read(ref _dirty)),
        unchecked((ulong)Interlocked.Read(ref _ranges)), unchecked((ulong)Interlocked.Read(ref _infos)),
        unchecked((ulong)Interlocked.Read(ref _writes)), unchecked((ulong)Interlocked.Read(ref _bytes)),
        unchecked((ulong)Interlocked.Read(ref _ticks)), Volatile.Read(ref _highWater));
}
