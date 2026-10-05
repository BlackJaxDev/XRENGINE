using System.Collections.Concurrent;
using System.Diagnostics;
using XREngine.Data;

namespace XREngine.Rendering
{
    /// <summary>
    /// Moves the client bytes of uploaded <see cref="EXRBufferClientCopyPolicy.ReleaseAfterUpload"/>
    /// buffers out of private memory into copy-on-write mappings of delete-on-close
    /// session spill files. A background timer drains the queue, so no render or
    /// upload thread writes files. The private copy a spill replaces is not freed
    /// here: buffer clones may share it and worker threads may still read through a
    /// raw pointer, so it is only dropped after a grace period and left to its
    /// finalizer (which <see cref="DataSourceMemoryStatistics"/> pressure makes prompt).
    /// </summary>
    internal static class XRBufferClientSpill
    {
        /// <summary>Buffers below this size stay private: a file and a view each cost more than they save.</summary>
        internal const uint MinimumSpillBytes = 256u * 1024u;

        private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ReleaseGracePeriod = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DrainInterval = TimeSpan.FromSeconds(2);

        private static readonly ConcurrentQueue<(WeakReference<XRDataBuffer> Buffer, long QueuedTimestamp)> s_pending = new();
        private static readonly Queue<(DataSource Replaced, long SpilledTimestamp)> s_retired = new();
        private static readonly object s_drainSync = new();
        private static Timer? s_timer;
        private static string? s_directory;
        private static long s_spilledBytes;
        private static long s_spilledBuffers;
        private static long s_failedSpills;
        private static string? s_lastFailure;

        /// <summary>Client bytes currently held in spill mappings instead of private memory.</summary>
        public static long SpilledBytes => Interlocked.Read(ref s_spilledBytes);

        /// <summary>One-line summary for diagnostics and MCP inspection.</summary>
        public static string Describe()
            => $"spilledMB={SpilledBytes / 1048576.0:F1} spilledBuffers={Interlocked.Read(ref s_spilledBuffers)} " +
               $"pending={s_pending.Count} failed={Interlocked.Read(ref s_failedSpills)} lastFailure='{s_lastFailure}'";

        /// <summary>Queues <paramref name="buffer"/> for a spill once its upload has settled.</summary>
        internal static void Enqueue(XRDataBuffer buffer)
        {
            s_pending.Enqueue((new WeakReference<XRDataBuffer>(buffer), Stopwatch.GetTimestamp()));
            if (Volatile.Read(ref s_timer) is null)
            {
                Timer timer = new(static _ => Drain(), null, DrainInterval, DrainInterval);
                if (Interlocked.CompareExchange(ref s_timer, timer, null) is not null)
                    timer.Dispose();
            }
        }

        internal static void RecordReleased(long bytes)
            => Interlocked.Add(ref s_spilledBytes, -bytes);

        private static void Drain()
        {
            if (!Monitor.TryEnter(s_drainSync))
                return;
            try
            {
                DrainCore();
            }
            catch (Exception ex)
            {
                // A timer callback must never throw: an escaping exception ends the process.
                RecordFailure(ex);
            }
            finally
            {
                Monitor.Exit(s_drainSync);
            }
        }

        private static void DrainCore()
        {
            long now = Stopwatch.GetTimestamp();
            DropRetiredCopies(now);
            int count = s_pending.Count;
            for (int i = 0; i < count && s_pending.TryPeek(out var head); i++)
            {
                if (Stopwatch.GetElapsedTime(head.QueuedTimestamp, now) < SettleDelay)
                    break;
                if (!s_pending.TryDequeue(out var entry))
                    break;
                if (entry.Buffer.TryGetTarget(out XRDataBuffer? buffer))
                    TrySpill(buffer, now);
            }
        }

        private static void DropRetiredCopies(long now)
        {
            while (s_retired.TryPeek(out var retired) &&
                   Stopwatch.GetElapsedTime(retired.SpilledTimestamp, now) >= ReleaseGracePeriod)
            {
                s_retired.Dequeue();
            }
        }

        private static void TrySpill(XRDataBuffer buffer, long now)
        {
            if (!buffer.TryBeginClientSpill(out DataSource? source, out ulong revision, out ulong writeActivity))
                return;

            // The private copy is leased from here to TryCompleteClientSpill: if the
            // buffer releases or replaces it meanwhile, its disposal is handed back here.
            XRBufferSpilledDataSource? spilled = null;
            bool swapped = false;
            try
            {
                spilled = WriteSpillFile(source);
            }
            catch (Exception ex)
            {
                // A full or read-only cache volume, or any other failure, leaves the copy
                // private; the failure is counted and named rather than retried every frame.
                RecordFailure(ex);
            }
            finally
            {
                swapped = buffer.TryCompleteClientSpill(source, spilled, revision, writeActivity, out bool disposeSource);
                if (!swapped)
                    spilled?.Dispose();
                if (disposeSource)
                    source.Dispose();
            }

            if (!swapped)
                return;

            s_retired.Enqueue((source, now));
            Interlocked.Add(ref s_spilledBytes, source.Length);
            Interlocked.Increment(ref s_spilledBuffers);
        }

        private static unsafe XRBufferSpilledDataSource WriteSpillFile(DataSource source)
        {
            uint length = source.Length;
            // Another session may have removed this folder while it was empty.
            string directory = Directory.CreateDirectory(ResolveDirectory()).FullName;
            FileStream? file = new(
                Path.Combine(directory, $"{Guid.NewGuid():N}.bin"),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            try
            {
                file.Write(new ReadOnlySpan<byte>(source.Address.Pointer, checked((int)length)));
                file.Flush();
                XRBufferSpilledDataSource spilled = XRBufferSpilledDataSource.Map(file, length);
                file = null;
                return spilled;
            }
            finally
            {
                file?.Dispose();
            }
        }

        private static void RecordFailure(Exception ex)
        {
            Interlocked.Increment(ref s_failedSpills);
            s_lastFailure = $"{ex.GetType().Name}: {ex.Message}";
        }

        private static string ResolveDirectory()
        {
            if (s_directory is { } existing)
                return existing;

            string? root = null;
            try
            {
                root = RuntimeRenderingHostServices.Assets.GameCachePath;
            }
            catch (InvalidOperationException)
            {
            }

            string spillRoot = Path.Combine(string.IsNullOrWhiteSpace(root) ? Path.GetTempPath() : root, "BufferSpill");
            string directory = Path.Combine(spillRoot, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            s_directory = directory;
            RemoveDirectoriesOfExitedProcesses(spillRoot);
            return directory;
        }

        /// <summary>
        /// Spill files delete themselves when their handle closes, which the OS also
        /// does for a process that exits or crashes, so exited sessions leave empty
        /// folders. Deleting a non-empty folder fails, which skips live sessions.
        /// </summary>
        private static void RemoveDirectoriesOfExitedProcesses(string spillRoot)
        {
            foreach (string folder in Directory.EnumerateDirectories(spillRoot))
            {
                if (string.Equals(folder, s_directory, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    Directory.Delete(folder, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
