using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine.Audio
{
    /// <summary>
    /// Lightweight runtime diagnostics for the audio subsystem.
    /// Tracks source state transitions, buffer queue depth, and underflow events
    /// so that parity failures between the legacy OpenAL path and future architecture
    /// changes are visible quickly.
    /// <para>
    /// All methods are designed for minimal overhead when <see cref="Enabled"/> is <c>false</c> (default).
    /// Enable diagnostics at startup or at runtime via <see cref="Enabled"/> to begin collecting.
    /// </para>
    /// </summary>
    public static class AudioDiagnostics
    {
        /// <summary>
        /// Master switch. When <c>false</c>, all recording methods early-return at negligible cost.
        /// </summary>
        public static bool Enabled { get; set; } = false;

        /// <summary>
        /// When <c>true</c>, diagnostics events are also written to <see cref="Trace"/> output.
        /// Requires <see cref="Enabled"/> to also be <c>true</c>.
        /// </summary>
        public static bool TraceOutput { get; set; } = false;

        #region Counters

        private static long _sourceStateTransitions;
        private static long _bufferUnderflows;
        private static long _bufferOverflows;
        private static long _buffersQueued;
        private static long _buffersUnqueued;
        private static long _listenersCreated;
        private static long _listenersDisposed;

        /// <summary>Total number of source state transitions recorded.</summary>
        public static long SourceStateTransitions => Interlocked.Read(ref _sourceStateTransitions);

        /// <summary>Total number of buffer underflow events (attempted unqueue with nothing processed).</summary>
        public static long BufferUnderflows => Interlocked.Read(ref _bufferUnderflows);

        /// <summary>Total number of buffer overflow events (queue full, buffers leaked back to pool).</summary>
        public static long BufferOverflows => Interlocked.Read(ref _bufferOverflows);

        /// <summary>Total number of buffers queued across all sources.</summary>
        public static long BuffersQueued => Interlocked.Read(ref _buffersQueued);

        /// <summary>Total number of buffers unqueued/consumed across all sources.</summary>
        public static long BuffersUnqueued => Interlocked.Read(ref _buffersUnqueued);

        /// <summary>Total number of listener contexts created.</summary>
        public static long ListenersCreated => Interlocked.Read(ref _listenersCreated);

        /// <summary>Total number of listener contexts disposed.</summary>
        public static long ListenersDisposed => Interlocked.Read(ref _listenersDisposed);

        #endregion

        #region Recent Events Ring Buffer

        /// <summary>
        /// Describes a single diagnostics event.
        /// </summary>
        public readonly record struct DiagEvent(
            long Timestamp,
            DiagEventKind Kind,
            string? Detail);

        /// <summary>
        /// Categories of diagnostics events.
        /// </summary>
        public enum DiagEventKind
        {
            SourceStateChange,
            BufferQueued,
            BufferUnqueued,
            BufferUnderflow,
            BufferOverflow,
            ListenerCreated,
            ListenerDisposed,
            OpenALError,
        }

        private static readonly ConcurrentQueue<DiagEvent> _recentEvents = new();
        private const int MaxRecentEvents = 256;

        /// <summary>
        /// Returns a snapshot of recent diagnostics events (up to <see cref="MaxRecentEvents"/>).
        /// </summary>
        public static DiagEvent[] GetRecentEvents()
            => [.. _recentEvents];

        private static void PushEvent(DiagEventKind kind, string? detail)
        {
            var evt = new DiagEvent(Stopwatch.GetTimestamp(), kind, detail);
            _recentEvents.Enqueue(evt);
            // Trim to bounded size (approximate — concurrent, so may momentarily exceed)
            while (_recentEvents.Count > MaxRecentEvents)
                _recentEvents.TryDequeue(out _);

            if (TraceOutput)
                Trace.WriteLine($"[AudioDiag] {kind}: {detail}");
        }

        #endregion

        #region Recording Methods

        /// <summary>
        /// Record a source state transition (e.g. Initial → Playing).
        /// </summary>
        public static void RecordSourceStateChange(uint sourceHandle, string fromState, string toState)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _sourceStateTransitions);
            PushEvent(DiagEventKind.SourceStateChange, $"Source {sourceHandle}: {fromState} → {toState}");
        }

        /// <summary>
        /// Record that buffers were queued on a source.
        /// </summary>
        public static void RecordBuffersQueued(uint sourceHandle, int count, int totalQueued)
        {
            if (!Enabled) return;
            Interlocked.Add(ref _buffersQueued, count);
            PushEvent(DiagEventKind.BufferQueued, $"Source {sourceHandle}: +{count} buffers (total queued: {totalQueued})");
        }

        /// <summary>
        /// Record that buffers were unqueued/consumed from a source.
        /// </summary>
        public static void RecordBuffersUnqueued(uint sourceHandle, int count, int remaining)
        {
            if (!Enabled) return;
            Interlocked.Add(ref _buffersUnqueued, count);
            PushEvent(DiagEventKind.BufferUnqueued, $"Source {sourceHandle}: -{count} buffers (remaining: {remaining})");
        }

        /// <summary>
        /// Record a buffer underflow (attempted consumption with no processed buffers).
        /// </summary>
        public static void RecordBufferUnderflow(uint sourceHandle, int queueDepth)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _bufferUnderflows);
            PushEvent(DiagEventKind.BufferUnderflow, $"Source {sourceHandle}: underflow (queue depth: {queueDepth})");
        }

        /// <summary>
        /// Record a buffer overflow (queue full, buffers returned to pool).
        /// </summary>
        public static void RecordBufferOverflow(uint sourceHandle, int leaked)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _bufferOverflows);
            PushEvent(DiagEventKind.BufferOverflow, $"Source {sourceHandle}: overflow, {leaked} buffers leaked back to pool");
        }

        /// <summary>
        /// Record that a listener context was created.
        /// </summary>
        public static void RecordListenerCreated(string? name)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _listenersCreated);
            PushEvent(DiagEventKind.ListenerCreated, name ?? "(unnamed)");
        }

        /// <summary>
        /// Record that a listener context was disposed.
        /// </summary>
        public static void RecordListenerDisposed(string? name)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _listenersDisposed);
            PushEvent(DiagEventKind.ListenerDisposed, name ?? "(unnamed)");
        }

        /// <summary>
        /// Record an OpenAL error encountered during verification.
        /// </summary>
        public static void RecordOpenALError(string errorDescription)
        {
            if (!Enabled) return;
            PushEvent(DiagEventKind.OpenALError, errorDescription);
        }

        #endregion

        #region Snapshot

        /// <summary>
        /// Captures all current counters in a single read for logging/comparison.
        /// </summary>
        public readonly record struct Snapshot(
            long SourceStateTransitions,
            long BufferUnderflows,
            long BufferOverflows,
            long BuffersQueued,
            long BuffersUnqueued,
            long ListenersCreated,
            long ListenersDisposed);

        /// <summary>
        /// Take a snapshot of all current counters.
        /// </summary>
        public static Snapshot TakeSnapshot() => new(
            SourceStateTransitions,
            BufferUnderflows,
            BufferOverflows,
            BuffersQueued,
            BuffersUnqueued,
            ListenersCreated,
            ListenersDisposed);

        #endregion

        /// <summary>
        /// Reset all counters and clear the event ring buffer.
        /// Intended for test setup or diagnostics reset.
        /// </summary>
        public static void Reset()
        {
            Interlocked.Exchange(ref _sourceStateTransitions, 0);
            Interlocked.Exchange(ref _bufferUnderflows, 0);
            Interlocked.Exchange(ref _bufferOverflows, 0);
            Interlocked.Exchange(ref _buffersQueued, 0);
            Interlocked.Exchange(ref _buffersUnqueued, 0);
            Interlocked.Exchange(ref _listenersCreated, 0);
            Interlocked.Exchange(ref _listenersDisposed, 0);
            while (_recentEvents.TryDequeue(out _)) { }
        }
    }
}
