using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public enum MainThreadInvokeMode
        {
            Queued,
            Inline,
            AlreadyOnRenderThread
        }

        public readonly struct MainThreadInvokeEntry(long sequence, DateTimeOffset timestamp, string reason, Engine.MainThreadInvokeMode mode, int callerThreadId)
        {
            public long Sequence { get; } = sequence;
            public DateTimeOffset Timestamp { get; } = timestamp;
            public string Reason { get; } = reason;
            public MainThreadInvokeMode Mode { get; } = mode;
            public int CallerThreadId { get; } = callerThreadId;
        }

        private const int MaxMainThreadInvokeEntries = 512;
        private static long _mainThreadInvokeSequence;
        private static int _mainThreadInvokeLogCount;
        private static readonly ConcurrentQueue<MainThreadInvokeEntry> _mainThreadInvokeLog = new();

        internal static MainThreadInvokeEntry LogMainThreadInvoke(string? reason, MainThreadInvokeMode mode)
        {
            string label = string.IsNullOrWhiteSpace(reason) ? "MainThreadInvoke" : reason.Trim();
            var entry = new MainThreadInvokeEntry(
                Interlocked.Increment(ref _mainThreadInvokeSequence),
                DateTimeOffset.UtcNow,
                label,
                mode,
                Environment.CurrentManagedThreadId);

            _mainThreadInvokeLog.Enqueue(entry);
            TrimMainThreadInvokeLogIfNeeded();

            if (ShouldWriteMainThreadInvokeDiagnostics())
                WriteMainThreadInvokeRequest(entry);
            return entry;
        }

        internal static void LogMainThreadInvokeExecution(in MainThreadInvokeEntry entry, double queueDelayMs, double executionMs, bool completed, Exception? exception)
        {
            if (!ShouldWriteMainThreadInvokeDiagnostics())
                return;

            try
            {
                Thread currentThread = Thread.CurrentThread;
                var builder = new StringBuilder(512);
                builder.Append("[").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).AppendLine("] Main-thread invoke execution");
                builder.Append("Sequence: ").Append(entry.Sequence).AppendLine();
                builder.Append("Reason: ").Append(entry.Reason).AppendLine();
                builder.Append("RequestedMode: ").Append(entry.Mode).AppendLine();
                builder.Append("RequestedAtUtc: ").Append(entry.Timestamp.ToString("O")).AppendLine();
                builder.Append("ExecutionThreadId: ").Append(Environment.CurrentManagedThreadId).AppendLine();
                builder.Append("ExecutionThreadName: ").Append(GetThreadName(currentThread)).AppendLine();
                builder.Append("ExecutionThreadPool: ").Append(currentThread.IsThreadPoolThread).AppendLine();
                builder.Append("ExecutionThreadBackground: ").Append(currentThread.IsBackground).AppendLine();
                builder.Append("QueueDelayMs: ").Append(queueDelayMs.ToString("F3")).AppendLine();
                builder.Append("ExecutionMs: ").Append(executionMs.ToString("F3")).AppendLine();
                builder.Append("QueuedRenderJobsNow: ").Append(GetQueuedRenderThreadJobCount()).AppendLine();
                builder.Append("IsDispatchingRenderFrame: ").Append(IsDispatchingRenderFrame).AppendLine();
                builder.Append("Completed: ").Append(completed).AppendLine();

                if (exception is not null)
                {
                    builder.AppendLine("Exception:");
                    builder.AppendLine(exception.ToString());
                }

                Debug.WriteAuxiliaryLog("profiler-main-thread-invokes.log", builder.ToString());
            }
            catch
            {
                // Diagnostics must never break dispatch.
            }
        }

        public static IReadOnlyList<MainThreadInvokeEntry> GetMainThreadInvokeLogSnapshot()
            => [.. _mainThreadInvokeLog];

        private static void WriteMainThreadInvokeRequest(in MainThreadInvokeEntry entry)
        {
            try
            {
                Thread currentThread = Thread.CurrentThread;
                var builder = new StringBuilder(768);
                builder.Append("[").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).AppendLine("] Main-thread invoke requested");
                builder.Append("Sequence: ").Append(entry.Sequence).AppendLine();
                builder.Append("Reason: ").Append(entry.Reason).AppendLine();
                builder.Append("Mode: ").Append(entry.Mode).AppendLine();
                builder.Append("RequestedAtUtc: ").Append(entry.Timestamp.ToString("O")).AppendLine();
                builder.Append("CallerThreadId: ").Append(entry.CallerThreadId).AppendLine();
                builder.Append("CallerThreadName: ").Append(GetThreadName(currentThread)).AppendLine();
                builder.Append("CallerThreadPool: ").Append(currentThread.IsThreadPoolThread).AppendLine();
                builder.Append("CallerThreadBackground: ").Append(currentThread.IsBackground).AppendLine();
                builder.Append("CallerApartmentState: ").Append(currentThread.GetApartmentState()).AppendLine();
                builder.Append("RenderThreadId: ").Append(RenderThreadId).AppendLine();
                builder.Append("UpdateThreadId: ").Append(UpdateThreadId).AppendLine();
                builder.Append("PhysicsThreadId: ").Append(PhysicsThreadId).AppendLine();
                builder.Append("QueuedRenderJobsNow: ").Append(GetQueuedRenderThreadJobCount()).AppendLine();
                builder.Append("IsDispatchingRenderFrame: ").Append(IsDispatchingRenderFrame).AppendLine();
                builder.AppendLine("StackTrace:");
                builder.AppendLine(Debug.GetStackTrace(3, 12, ignoreBeforeWndProc: false));

                Debug.WriteAuxiliaryLog("profiler-main-thread-invokes.log", builder.ToString());
            }
            catch
            {
                // Diagnostics must never affect the main-thread invoke path.
            }
        }

        private static int GetQueuedRenderThreadJobCount()
        {
            try
            {
                JobManager jobs = Jobs;
                int total = 0;
                for (int priority = (int)JobPriority.Lowest; priority <= (int)JobPriority.Highest; priority++)
                    total += Math.Max(0, jobs.GetQueuedCount((JobPriority)priority, JobAffinity.RenderThread));
                return total;
            }
            catch
            {
                return -1;
            }
        }

        private static void TrimMainThreadInvokeLogIfNeeded()
        {
            int count = Interlocked.Increment(ref _mainThreadInvokeLogCount);
            while (count > MaxMainThreadInvokeEntries && _mainThreadInvokeLog.TryDequeue(out _))
                count = Interlocked.Decrement(ref _mainThreadInvokeLogCount);
        }

        private static bool ShouldWriteMainThreadInvokeDiagnostics()
        {
            try
            {
                return EditorPreferences.Debug.EnableMainThreadInvokeDiagnostics;
            }
            catch
            {
                return false;
            }
        }

        private static string GetThreadName(Thread thread)
            => string.IsNullOrWhiteSpace(thread.Name)
                ? $"ManagedThread-{Environment.CurrentManagedThreadId}"
                : thread.Name!;
    }
}
