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

                public void End()
                    => EndTime = Time.Timer.Time();

                public void OnPoolableDestroyed() { }
                public void OnPoolableReleased() { }
                public void OnPoolableReset()
                    => StartTime = Time.Timer.Time();
            }

            public bool EnableFrameLogging { get; set; } = true;
            public float DebugOutputMinElapsedMs { get; set; } = 1.0f;

            public ConcurrentDictionary<int, ConcurrentBag<CodeProfilerTimer>> FrameLog { get; } = [];
            private readonly ConcurrentDictionary<int, int> _depth = [];
            
            public void ClearFrameLog()
            {
                StringBuilder sb = new();
                foreach (var queue in FrameLog)
                {
                    if (queue.Value.IsEmpty)
                        continue;
                    
                    //sb.Append($"Frame log for thread with tid {queue.Key}:\n");
                    var bag = queue.Value;
                    var sorted = bag.OrderBy(x => x.StartTime).ThenBy(x => x.Depth);
                    foreach (var log in sorted)
                    {
                        string indent = new(' ', log.Depth * 2);
                        if (log.ElapsedMs >= DebugOutputMinElapsedMs)
                            sb.Append($"{indent}{log.Name} took {FormatMs(log.ElapsedMs)}\n");
                        _timerPool.Release(log);
                    }
                    string logStr = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(logStr))
                        Debug.Out(logStr);
                    sb.Clear();
                }
                FrameLog.Clear();
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
                => _depth.AddOrUpdate(entry.ThreadId, 1, (_, depth) => depth + 1);
            private void PopEntry(CodeProfilerTimer entry)
            {
                entry.End();
                entry.Depth = _depth.AddOrUpdate(entry.ThreadId, 0, (_, depth) => depth - 1);
                FrameLog.GetOrAdd(entry.ThreadId, _ => []).Add(entry);
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
