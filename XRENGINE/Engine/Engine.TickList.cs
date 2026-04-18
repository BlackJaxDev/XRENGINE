using XREngine.Extensions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using XREngine.Components;

namespace XREngine
{
    public static partial class Engine
    {
        public class TickList
        {
            public TickList(bool parallel, ETickGroup group, int order)
            {
                _parallel = parallel;
                _group = group;
                Tick = parallel ? ExecuteParallel : ExecuteSequential;
            }

            public bool Parallel
            {
                get => _parallel;
                set
                {
                    _parallel = value;
                    Tick = _parallel ? ExecuteParallel : ExecuteSequential;
                }
            }

            /// <summary>
            /// Ticks all items in this list.
            /// </summary>
            public Action Tick { get; private set; }

            private sealed class TickEntry
            {
                public required WorldTick Func { get; init; }
                public XRComponent? ComponentOwner { get; init; }
            }

            private readonly List<TickEntry> _methods = [];
            private readonly ConcurrentQueue<(bool Add, WorldTick Func)> _queue = new();
            private bool _parallel = true;
            private readonly ETickGroup _group;

            public int Count => _methods.Count;

            public void Add(WorldTick tickMethod) 
                => _queue.Enqueue((true, tickMethod));
            public void Remove(WorldTick tickMethod)
                => _queue.Enqueue((false, tickMethod));
            private void ExecuteParallel()
            {
                Dequeue();
                //float time = ElapsedTime;
                //Use tasks
                //Task[] tasks = new Task[_methods.Count];
                //for (int i = 0; i < _methods.Count; i++)
                //    tasks[i] = Task.Run(() => ExecTick(_methods[i]));
                //Task.WaitAll(tasks);
                _methods.ForEachParallelIList(ExecTick);
                //Debug.Out($"TickList Parallel: {Math.Round((ElapsedTime - time) * 1000.0f, 2)} ms");
            }
            private void ExecuteSequential()
            {
                Dequeue();
                //float time = ElapsedTime;
                _methods.ForEach(ExecTick);
                //Debug.Out($"TickList Sequential: {Math.Round((ElapsedTime - time) * 1000.0f, 2)} ms");
            }
            private void ExecTick(TickEntry entry)
            {
                if (entry.ComponentOwner is null || !Engine.Profiler.HasActiveComponentTimingFrame())
                {
                    entry.Func();
                    return;
                }

                long startTicks = Stopwatch.GetTimestamp();
                try
                {
                    entry.Func();
                }
                finally
                {
                    long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                    if (elapsedTicks > 0)
                        Engine.Profiler.RecordComponentTick(entry.ComponentOwner, _group, elapsedTicks);
                }
            }

            private void Dequeue()
            {
                //Add or remove the list of methods that tried to register to or unregister from this group while it was ticking.
                while (!_queue.IsEmpty && _queue.TryDequeue(out (bool Add, WorldTick Func) result))
                {
                    if (result.Add)
                    {
                        bool alreadyRegistered = false;
                        for (int i = 0; i < _methods.Count; i++)
                        {
                            if (_methods[i].Func == result.Func)
                            {
                                alreadyRegistered = true;
                                break;
                            }
                        }

                        if (alreadyRegistered)
                        {
                            Debug.LogWarning($"TickList: Duplicate registration of {DescribeTick(result.Func)} in {_group}. Ignoring.");
                            continue;
                        }

                        _methods.Add(new TickEntry
                        {
                            Func = result.Func,
                            ComponentOwner = ResolveComponentOwner(result.Func),
                        });
                    }
                    else
                    {
                        for (int i = 0; i < _methods.Count; i++)
                        {
                            if (_methods[i].Func == result.Func)
                            {
                                _methods.RemoveAt(i);
                                break;
                            }
                        }
                    }
                }
            }

            private static XRComponent? ResolveComponentOwner(WorldTick tickMethod)
            {
                if (tickMethod.Target is XRComponent component)
                    return component;

                object? target = tickMethod.Target;
                if (target is null)
                    return null;

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var field in target.GetType().GetFields(flags))
                {
                    if (field.GetValue(target) is XRComponent capturedComponent)
                        return capturedComponent;
                }

                return null;
            }

            private static string DescribeTick(WorldTick tickMethod)
            {
                try
                {
                    string methodName = tickMethod.Method?.Name ?? "<unknown-method>";

                    if (tickMethod.Target is not null)
                        return $"{GetSafeTypeName(tickMethod.Target.GetType())}.{methodName}";

                    if (tickMethod.Method?.DeclaringType is Type declaringType)
                        return $"{GetSafeTypeName(declaringType)}.{methodName}";

                    return methodName;
                }
                catch (Exception ex)
                {
                    return $"<unresolved-tick:{ex.GetType().Name}>";
                }
            }

            private static string GetSafeTypeName(Type type)
            {
                try
                {
                    return type.FullName ?? type.Name ?? "<unknown-type>";
                }
                catch (Exception)
                {
                    return "<unknown-type>";
                }
            }
        }
    }
}
