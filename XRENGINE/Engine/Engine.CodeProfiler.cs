using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using XREngine.Core;
using XREngine.Data.Core;

namespace XREngine
{
    public static partial class Engine
    {
        public class CodeProfiler : XRBase
        {
            private bool _enableFrameLogging = false;
            public bool EnableFrameLogging
            {
                get => _enableFrameLogging;
                set => SetField(ref _enableFrameLogging, value);
            }

            private float _debugOutputMinElapsedMs = 1.0f;
            public float DebugOutputMinElapsedMs
            {
                get => _debugOutputMinElapsedMs;
                set => SetField(ref _debugOutputMinElapsedMs, value);
            }

            public ConcurrentDictionary<int, CodeProfilerTimer> RootEntriesPerThread { get; } = [];

            private ConcurrentQueue<CodeProfilerTimer> _completedEntriesPrinting = [];
            private ConcurrentQueue<CodeProfilerTimer> _completedEntries = [];

            private readonly ResourcePool<CodeProfilerTimer> _timerPool = new(() => new CodeProfilerTimer());

            private readonly ConcurrentDictionary<Guid, CodeProfilerTimer> _asyncTimers = [];
            private readonly ThreadLocal<ThreadContext> _threadContext;

            public delegate void DelTimerCallback(string? methodName, float elapsedMs);

            public CodeProfiler()
            {
                _threadContext = new ThreadLocal<ThreadContext>(() => new ThreadContext(), trackAllValues: false);
                Time.Timer.SwapBuffers += ClearFrameLog;
            }
            ~CodeProfiler()
            {
                Time.Timer.SwapBuffers -= ClearFrameLog;
            }

            public class CodeProfilerTimer(string? name = null) : IPoolable
            {
                private readonly List<CodeProfilerTimer> _children = new(4);

                public float StartTime { get; private set; }
                public float EndTime { get; private set; }
                public float ElapsedMs { get; private set; }
                public float ElapsedSec => ElapsedMs * 0.001f;
                public string Name { get; private set; } = name ?? string.Empty;
                public int ThreadId { get; private set; }
                public int Depth { get; private set; }

                internal CodeProfilerTimer? Parent { get; private set; }
                internal DelTimerCallback? Callback { get; set; }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal void Begin(string? name, int threadId, CodeProfilerTimer? parent)
                {
                    Name = name ?? string.Empty;
                    ThreadId = threadId;
                    Parent = parent;
                    Depth = parent is null ? 0 : parent.Depth + 1;
                    StartTime = Time.Timer.Time();
                    EndTime = StartTime;
                    ElapsedMs = 0f;
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void Stop()
                {
                    EndTime = Time.Timer.Time();
                    ElapsedMs = (EndTime - StartTime) * 1000.0f;
                }

                internal void AddChild(CodeProfilerTimer child)
                    => _children.Add(child);

                public void OnPoolableDestroyed() { }
                public void OnPoolableReleased() { }
                public void OnPoolableReset()
                {
                    Depth = 0;
                    ThreadId = 0;
                    Parent = null;
                    Callback = null;
                    Name = string.Empty;
                    StartTime = 0f;
                    EndTime = 0f;
                    ElapsedMs = 0f;
                    _children.Clear();
                }

                internal void PrintSubEntries(StringBuilder sb, ResourcePool<CodeProfilerTimer> timerPool, float debugOutputMinElapsedMs)
                {
                    float totalMs = ElapsedMs;
                    bool hadSubEntries = false;

                    for (int i = 0; i < _children.Count; ++i)
                    {
                        var child = _children[i];
                        if (child.ElapsedMs >= debugOutputMinElapsedMs)
                        {
                            hadSubEntries = true;
                            string indent = new(' ', child.Depth * 2);
                            sb.Append($"{indent}{child.Name} took {FormatMs(child.ElapsedMs)}\n");
                            child.PrintSubEntries(sb, timerPool, debugOutputMinElapsedMs);
                            totalMs -= child.ElapsedMs;
                        }

                        timerPool.Release(child);
                    }

                    _children.Clear();

                    if (hadSubEntries && totalMs >= debugOutputMinElapsedMs)
                        sb.Append($"{new(' ', (Depth + 1) * 2)}<Remaining>: {FormatMs(totalMs)}\n");
                }
            }

            private sealed class ThreadContext
            {
                public ThreadContext()
                {
                    ThreadId = Environment.CurrentManagedThreadId;
                    ActiveStack = new Stack<CodeProfilerTimer>(32);
                }

                public int ThreadId { get; }
                public Stack<CodeProfilerTimer> ActiveStack { get; }
            }

            public void ClearFrameLog()
            {
                StringBuilder sb = new();
                _completedEntriesPrinting.Clear();
                _completedEntriesPrinting = Interlocked.Exchange(ref _completedEntries, _completedEntriesPrinting);
                while (_completedEntriesPrinting.TryDequeue(out var entry))
                {
                    if (entry.ElapsedMs >= DebugOutputMinElapsedMs)
                    {
                        sb.Append($"{new(' ', entry.Depth * 2)}{entry.Name} took {FormatMs(entry.ElapsedMs)}\n");
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
                    return $"{MathF.Round(elapsedMs * 1000.0f)}μs";
                if (elapsedMs >= 1000.0f)
                    return $"{MathF.Round(elapsedMs / 1000.0f, 1)}sec (slow)";
                return $"{MathF.Round(elapsedMs)}ms";
            }

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

                var context = _threadContext.Value;
                var stack = context.ActiveStack;
                var parent = stack.Count > 0 ? stack.Peek() : null;

                var entry = _timerPool.Take();
                entry.Begin(methodName ?? string.Empty, context.ThreadId, parent);
                entry.Callback = callback;

                stack.Push(entry);

                if (parent is null)
                    RootEntriesPerThread[context.ThreadId] = entry;

                return StateObject.New(() => Stop(entry));
            }
            public StateObject Start([CallerMemberName] string? methodName = null)
                => Start(null, methodName);
            /// <summary>
            /// Stops a timer and calls the callback if available.
            /// </summary>
            /// <param name="entry"></param>
            private void Stop(CodeProfilerTimer entry)
            {
                var context = _threadContext.Value;
                var stack = context.ActiveStack;
                CodeProfilerTimer? parent = null;

                if (stack.Count > 0 && ReferenceEquals(stack.Peek(), entry))
                {
                    stack.Pop();
                    parent = stack.Count > 0 ? stack.Peek() : null;
                }
                else if (stack.Count > 0 && stack.Contains(entry))
                {
                    // Out-of-order disposal; unwind until we drop the target entry.
                    while (stack.Count > 0 && !ReferenceEquals(stack.Peek(), entry))
                        stack.Pop();

                    if (stack.Count > 0 && ReferenceEquals(stack.Peek(), entry))
                    {
                        stack.Pop();
                        parent = stack.Count > 0 ? stack.Peek() : null;
                    }
                }
                else
                {
                    parent = entry.Parent;
                }

                entry.Stop();

                entry.Callback?.Invoke(entry.Name, entry.ElapsedMs);
                entry.Callback = null;

                if (parent is not null)
                {
                    parent.AddChild(entry);
                    return;
                }

                RootEntriesPerThread.TryRemove(entry.ThreadId, out _);
                _completedEntries.Enqueue(entry);
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
                entry.Begin(methodName ?? string.Empty, Environment.CurrentManagedThreadId, null);
                entry.Callback = callback;
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
                {
                    entry.Stop();
                    entry.Callback?.Invoke(entry.Name, entry.ElapsedMs);
                    entry.Callback = null;
                    methodName = entry.Name;
                    _completedEntries.Enqueue(entry);
                    return entry.ElapsedMs;
                }

                methodName = string.Empty;
                return 0.0f;
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
                {
                    entry.Stop();
                    entry.Callback?.Invoke(entry.Name, entry.ElapsedMs);
                    entry.Callback = null;
                    _completedEntries.Enqueue(entry);
                    return entry.ElapsedMs;
                }

                return 0.0f;
            }
        }
    }
}
