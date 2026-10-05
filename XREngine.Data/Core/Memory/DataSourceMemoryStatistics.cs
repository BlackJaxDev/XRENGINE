using System;
using System.Threading;

namespace XREngine.Data
{
    /// <summary>
    /// Process-wide accounting of native memory owned by <see cref="DataSource"/>
    /// instances (mesh buffer client copies, texture mip pixels and other unmanaged
    /// payloads). Large owned allocations are also reported to the GC as memory
    /// pressure, so finalizable sources that nothing disposed are collected in
    /// proportion to the native memory they hold rather than their tiny managed size.
    /// </summary>
    public static class DataSourceMemoryStatistics
    {
        /// <summary>
        /// Owned allocations at or above this size add GC memory pressure. Smaller
        /// ones are only counted, which keeps pressure bookkeeping off the many tiny
        /// uniform and parameter payloads.
        /// </summary>
        public const long PressureThresholdBytes = 64L * 1024L;

        private static long s_ownedBytes;
        private static long s_ownedCount;
        private static long s_peakOwnedBytes;
        private static long s_pressureBytes;
        private static long s_finalizedBytes;
        private static long s_finalizedCount;

        /// <summary>Native bytes currently owned by live data sources.</summary>
        public static long OwnedBytes => Interlocked.Read(ref s_ownedBytes);

        /// <summary>Number of live data sources that own their native memory.</summary>
        public static long OwnedCount => Interlocked.Read(ref s_ownedCount);

        /// <summary>Highest <see cref="OwnedBytes"/> observed.</summary>
        public static long PeakOwnedBytes => Interlocked.Read(ref s_peakOwnedBytes);

        /// <summary>Native bytes currently reported to the GC as memory pressure.</summary>
        public static long PressureBytes => Interlocked.Read(ref s_pressureBytes);

        /// <summary>
        /// Bytes freed by finalizers instead of an explicit dispose since startup:
        /// sources that were dropped without being disposed.
        /// </summary>
        public static long FinalizedBytes => Interlocked.Read(ref s_finalizedBytes);

        /// <summary>Count of sources freed by their finalizer since startup.</summary>
        public static long FinalizedCount => Interlocked.Read(ref s_finalizedCount);

        internal static void RecordAllocation(long bytes)
        {
            if (bytes <= 0L)
                return;

            long owned = Interlocked.Add(ref s_ownedBytes, bytes);
            Interlocked.Increment(ref s_ownedCount);
            UpdatePeak(owned);
            if (bytes < PressureThresholdBytes)
                return;

            GC.AddMemoryPressure(bytes);
            Interlocked.Add(ref s_pressureBytes, bytes);
        }

        internal static void RecordRelease(long bytes, bool finalized)
        {
            if (bytes <= 0L)
                return;

            Interlocked.Add(ref s_ownedBytes, -bytes);
            Interlocked.Decrement(ref s_ownedCount);
            if (finalized)
            {
                Interlocked.Add(ref s_finalizedBytes, bytes);
                Interlocked.Increment(ref s_finalizedCount);
            }
            if (bytes < PressureThresholdBytes)
                return;

            GC.RemoveMemoryPressure(bytes);
            Interlocked.Add(ref s_pressureBytes, -bytes);
        }

        /// <summary>One-line summary for diagnostics and MCP inspection.</summary>
        public static string Describe()
            => $"ownedMB={OwnedBytes / (1024.0 * 1024.0):F1} ownedCount={OwnedCount} peakMB={PeakOwnedBytes / (1024.0 * 1024.0):F1} " +
               $"pressureMB={PressureBytes / (1024.0 * 1024.0):F1} finalizedMB={FinalizedBytes / (1024.0 * 1024.0):F1} finalizedCount={FinalizedCount}";

        private static void UpdatePeak(long owned)
        {
            long current = Interlocked.Read(ref s_peakOwnedBytes);
            while (owned > current)
            {
                long observed = Interlocked.CompareExchange(ref s_peakOwnedBytes, owned, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
