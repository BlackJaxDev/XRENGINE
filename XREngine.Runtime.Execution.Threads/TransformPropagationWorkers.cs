using XREngine.Data.Runtime.AotParity;
using XREngine.Scene.Transforms;
using System.Runtime.ExceptionServices;

namespace XREngine.Execution;

/// <summary>Persistent workers avoid per-pass task, delegate and scheduler allocations.</summary>
internal sealed class TransformPropagationWorkers : ITransformPropagationWorkerPool
{
    private readonly TransformHierarchyStore _store;
    private readonly Thread[] _threads;
    private readonly AutoResetEvent[] _wake;
    private readonly CountdownEvent _complete;
    private readonly Action<int> _execute;
    private int _next, _count;
    private bool _stopping;
    private bool _playerPath;
    private ExceptionDispatchInfo? _failure;
    private long _allocated;

    internal TransformPropagationWorkers(TransformHierarchyStore store, Action<int> execute)
    {
        _store = store;
        _execute = execute;
        int count = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
        _threads = new Thread[count];
        _wake = new AutoResetEvent[count];
        _complete = new CountdownEvent(count);
        for (int i = 0; i < count; i++)
        {
            _wake[i] = new AutoResetEvent(false);
            _threads[i] = new Thread(Work) { IsBackground = true, Name = "Transform propagation" };
            _threads[i].Start(i);
        }
    }

    public long Run(int count)
    {
        _playerPath = AotParityDiagnostics.IsPlayerPath;
        _next = -1;
        _count = count;
        _failure = null;
        _allocated = 0;
        _complete.Reset(_threads.Length);
        foreach (var wake in _wake) wake.Set();
        _complete.Wait();
        _failure?.Throw();
        return _allocated;
    }

    private void Work(object? state)
    {
        int worker = (int)state!;
        while (true)
        {
            _wake[worker].WaitOne();
            if (Volatile.Read(ref _stopping)) return;
            long before = GC.GetAllocatedBytesForCurrentThread();
            using var parityScope = _playerPath
                ? AotParityDiagnostics.EnterSynchronousPlayerPath(EAotParityPlayerPathKind.PlayMode) : default;
            TransformHierarchyStore.BeginEvaluation(_store);
            try
            {
                int next;
                while ((next = Interlocked.Increment(ref _next)) < _count) _execute(next);
            }
            catch (Exception error) { Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(error), null); }
            finally
            {
                TransformHierarchyStore.EndEvaluation();
                Interlocked.Add(ref _allocated, GC.GetAllocatedBytesForCurrentThread() - before);
                _complete.Signal();
            }
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _stopping, true);
        foreach (var wake in _wake) wake.Set();
        foreach (var thread in _threads) thread.Join();
        foreach (var wake in _wake) wake.Dispose();
        _complete.Dispose();
    }
}
