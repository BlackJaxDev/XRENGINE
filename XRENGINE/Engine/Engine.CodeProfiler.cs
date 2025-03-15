using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using XREngine.Core;

namespace XREngine
{
    public static partial class Engine
    {
        public class CodeProfiler
        {
            public CodeProfiler()
            {
                Time.Timer.SwapBuffers += ClearFrameLog;
            }
            ~CodeProfiler()
            {
                Time.Timer.SwapBuffers -= ClearFrameLog;
            }

            public class CodeProfilerTimer(string? name = null) : IPoolable
            {
                public float StartTime { get; private set; } = new();
                public float EndTime { get; private set; } = new();
                public float ElapsedSec => MathF.Round(EndTime - StartTime, 5);
                public float ElapsedMs => MathF.Round((EndTime - StartTime) * 1000.0f, 5);
                public string? Name { get; set; } = name;
                public int ThreadId { get; set; }
                public int Depth { get; set; }

                /// <summary>
                /// Completed sub-entries that are ready to be printed.
                /// </summary>
                private readonly ConcurrentQueue<CodeProfilerTimer> _completedSubEntries = new();
                /// <summary>
                /// The currently active sub-entry that's being timed.
                /// </summary>
                private CodeProfilerTimer? _activeSubEntry;

                public void End()
                    => EndTime = Time.Timer.Time();

                public void OnPoolableDestroyed() { }
                public void OnPoolableReleased() { }
                public void OnPoolableReset()
                    => StartTime = Time.Timer.Time();

                public void PushEntry(CodeProfilerTimer entry)
                {
                    entry.Depth = Depth + 1;
                    if (_activeSubEntry is null)
                        _activeSubEntry = entry;
                    else
                        _activeSubEntry.PushEntry(entry);
                }

                public void PopEntry(CodeProfilerTimer entry)
                {
                    if (_activeSubEntry == entry)
                    {
                        _activeSubEntry = null;
                        _completedSubEntries.Enqueue(entry);
                    }
                    else
                    {
                        _activeSubEntry?.PopEntry(entry);
                    }
                }

                internal void PrintSubEntries(StringBuilder sb, ResourcePool<CodeProfilerTimer> timerPool, float debugOutputMinElapsedMs)
                {
                    foreach (var entry in _completedSubEntries)
                    {
                        string indent = new(' ', entry.Depth * 2);
                        if (entry.ElapsedMs >= debugOutputMinElapsedMs)
                        {
                            sb.Append($"{indent}{entry.Name} took {FormatMs(entry.ElapsedMs)}\n");
                            entry.PrintSubEntries(sb, timerPool, debugOutputMinElapsedMs);
                        }
                        timerPool.Release(entry);
                    }
                }
            }

            public bool EnableFrameLogging { get; set; } = true;
            public float DebugOutputMinElapsedMs { get; set; } = 1.0f;

            public ConcurrentDictionary<int, CodeProfilerTimer> RootEntriesPerThread { get; } = [];
            private ConcurrentQueue<CodeProfilerTimer> _completedEntriesPrinting = [];
            private ConcurrentQueue<CodeProfilerTimer> _completedEntries = [];

            public void ClearFrameLog()
            {
                StringBuilder sb = new();
                _completedEntriesPrinting.Clear();
                _completedEntriesPrinting = Interlocked.Exchange(ref _completedEntries, _completedEntriesPrinting);
                foreach (var entry in _completedEntriesPrinting)
                {
                    string indent = new(' ', entry.Depth * 2);
                    if (entry.ElapsedMs >= DebugOutputMinElapsedMs)
                    {
                        sb.Append($"{indent}{entry.Name} took {FormatMs(entry.ElapsedMs)}\n");
                        entry.PrintSubEntries(sb, _timerPool, DebugOutputMinElapsedMs);
                    }
                    _timerPool.Release(entry);
                }
                string logStr = sb.ToString();
                if (!string.IsNullOrWhiteSpace(logStr))
                    Debug.Out(logStr);
                sb.Clear();
            }

            private static string FormatMs(float elapsedMs)
            {
                if (elapsedMs < 1.0f)
                    return $"{MathF.Round(elapsedMs * 1000.0f, 2)}μs";
                if (elapsedMs >= 1000.0f)
                    return $"{MathF.Round(elapsedMs / 1000.0f, 2)}s";
                return $"{MathF.Round(elapsedMs, 2)}ms";
            }

            private void PushEntry(CodeProfilerTimer entry)
            {
                RootEntriesPerThread.AddOrUpdate(
                    entry.ThreadId, //key
                    entry, //add
                    (tid, rootEntry) => //update
                    {
                        rootEntry.PushEntry(entry);
                        return rootEntry;
                    });
            }

            private void PopEntry(CodeProfilerTimer entry)
            {
                entry.End();

                if (!RootEntriesPerThread.TryGetValue(entry.ThreadId, out var rootEntry))
                    return;

                if (rootEntry != entry)
                    rootEntry.PopEntry(entry);
                else
                {
                    RootEntriesPerThread.TryRemove(entry.ThreadId, out _);
                    _completedEntries.Enqueue(entry);
                }
            }

            private readonly ResourcePool<CodeProfilerTimer> _timerPool = new(() => new CodeProfilerTimer());
            private readonly ConcurrentDictionary<Guid, CodeProfilerTimer> _asyncTimers = [];

            public delegate void DelTimerCallback(string? methodName, float elapsedMs);

            /// <summary>
            /// Starts a timer and returns a StateObject that will stop the timer when it is disposed.
            /// </summary>
            /// <param name="callback"></param>
            /// <param name="methodName"></param>
            /// <returns></returns>
            public StateObject Start(DelTimerCallback? callback, [CallerMemberName] string? methodName = null)
            {
                if (!EnableFrameLogging)
                    return StateObject.New();

                var entry = _timerPool.Take();
                entry.Name = methodName;
                entry.ThreadId = Environment.CurrentManagedThreadId;
                PushEntry(entry);
                return StateObject.New(() => Stop(entry, callback));
            }
            public StateObject Start([CallerMemberName] string? methodName = null)
                => Start(null, methodName);
            /// <summary>
            /// Stops a timer and calls the callback if available.
            /// </summary>
            /// <param name="entry"></param>
            /// <param name="callback"></param>
            private void Stop(CodeProfilerTimer entry, DelTimerCallback? callback)
            {
                callback?.Invoke(entry.Name ?? string.Empty, entry.ElapsedMs);
                PopEntry(entry);
            }
            /// <summary>
            /// Starts an async timer and returns the id of the timer.
            /// </summary>
            /// <param name="callback"></param>
            /// <param name="methodName"></param>
            /// <returns></returns>
            public Guid StartAsync(DelTimerCallback? callback = null, [CallerMemberName] string? methodName = null)
            {
                Guid id = Guid.NewGuid();
                if (!EnableFrameLogging)
                    return id;

                var entry = _timerPool.Take();
                entry.Name = methodName ?? string.Empty;
                _asyncTimers.TryAdd(id, entry);
                return id;
            }
            /// <summary>
            /// Stops an async timer by id and returns the elapsed time in milliseconds.
            /// </summary>
            /// <param name="id"></param>
            /// <param name="methodName"></param>
            /// <returns></returns>
            public float StopAsync(Guid id, out string? methodName)
            {
                if (!EnableFrameLogging)
                {
                    methodName = string.Empty;
                    return 0.0f;
                }

                if (_asyncTimers.TryRemove(id, out var entry))
                    PopEntry(entry);

                methodName = entry?.Name;
                return entry?.ElapsedMs ?? 0.0f;
            }
            /// <summary>
            /// Stops an async timer by id and returns the elapsed time in milliseconds.
            /// </summary>
            /// <param name="id"></param>
            /// <param name="methodName"></param>
            /// <returns></returns>
            public float StopAsync(Guid id)
            {
                if (!EnableFrameLogging)
                    return 0.0f;

                if (_asyncTimers.TryRemove(id, out var entry))
                    PopEntry(entry);
                
                return entry?.ElapsedMs ?? 0.0f;
            }
        }
    }
}
