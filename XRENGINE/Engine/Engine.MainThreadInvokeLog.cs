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

        public readonly struct MainThreadInvokeEntry(long sequence, DateTimeOffset timestamp, string reason, Engine.MainThreadInvokeMode mode, int callerThreadId)
        {
            public long Sequence { get; } = sequence;
            public DateTimeOffset Timestamp { get; } = timestamp;
            public string Reason { get; } = reason;
            public MainThreadInvokeMode Mode { get; } = mode;
            public int CallerThreadId { get; } = callerThreadId;
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
            => [.. _mainThreadInvokeLog];
    }
}
