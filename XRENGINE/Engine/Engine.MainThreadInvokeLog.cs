using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        public readonly struct MainThreadInvokeEntry
        {
            public long Sequence { get; }
            public DateTimeOffset Timestamp { get; }
            public string Reason { get; }
            public MainThreadInvokeMode Mode { get; }
            public int CallerThreadId { get; }

            public MainThreadInvokeEntry(long sequence, DateTimeOffset timestamp, string reason, MainThreadInvokeMode mode, int callerThreadId)
            {
                Sequence = sequence;
                Timestamp = timestamp;
                Reason = reason;
                Mode = mode;
                CallerThreadId = callerThreadId;
            }
        }

        private static long _mainThreadInvokeSequence;
        private static readonly ConcurrentQueue<MainThreadInvokeEntry> _mainThreadInvokeLog = new();

        internal static void LogMainThreadInvoke(string? reason, MainThreadInvokeMode mode)
        {
            string label = string.IsNullOrWhiteSpace(reason) ? "MainThreadInvoke" : reason.Trim();
            var entry = new MainThreadInvokeEntry(
                Interlocked.Increment(ref _mainThreadInvokeSequence),
                DateTimeOffset.UtcNow,
                label,
                mode,
                Environment.CurrentManagedThreadId);

            _mainThreadInvokeLog.Enqueue(entry);
        }

        public static IReadOnlyList<MainThreadInvokeEntry> GetMainThreadInvokeLogSnapshot()
            => _mainThreadInvokeLog.ToArray();
    }
}
