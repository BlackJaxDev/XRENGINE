using XREngine.Components;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Owns desktop threads and signals for one physics-chain scheduler.</summary>
internal sealed class DesktopPhysicsChainCpuWorkerGroup : IPhysicsChainCpuWorkerGroup
{
    private readonly Thread[] _threads;
    private readonly AutoResetEvent[] _workSignals;
    private readonly CountdownEvent _completion;
    private readonly Action<int> _processRanges;
    private int _disposed;

    public DesktopPhysicsChainCpuWorkerGroup(int workerCount, Action<int> processRanges)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentNullException.ThrowIfNull(processRanges);

        _threads = new Thread[workerCount];
        _workSignals = new AutoResetEvent[workerCount];
        _completion = new CountdownEvent(workerCount);
        _processRanges = processRanges;

        int createdSignalCount = 0;
        int startedWorkerCount = 0;
        try
        {
            for (int workerIndex = 0; workerIndex < workerCount; ++workerIndex)
            {
                _workSignals[workerIndex] = new AutoResetEvent(false);
                ++createdSignalCount;
            }

            for (int workerIndex = 0; workerIndex < workerCount; ++workerIndex)
            {
                var thread = new Thread(WorkerMain)
                {
                    IsBackground = true,
                    Name = $"PhysicsChainCpu-{workerIndex}",
                };
                _threads[workerIndex] = thread;
                thread.Start(workerIndex);
                ++startedWorkerCount;
            }
        }
        catch
        {
            Volatile.Write(ref _disposed, 1);
            for (int workerIndex = 0; workerIndex < startedWorkerCount; ++workerIndex)
                _workSignals[workerIndex].Set();
            for (int workerIndex = 0; workerIndex < startedWorkerCount; ++workerIndex)
                _threads[workerIndex].Join();
            for (int workerIndex = 0; workerIndex < createdSignalCount; ++workerIndex)
                _workSignals[workerIndex].Dispose();
            _completion.Dispose();
            throw;
        }
    }

    public int FixedWorkerCount => _threads.Length;

    public void SynchronousRun()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _completion.Reset(_threads.Length);
        for (int workerIndex = 0; workerIndex < _workSignals.Length; ++workerIndex)
            _workSignals[workerIndex].Set();
        try
        {
            _processRanges(_threads.Length);
        }
        finally
        {
            _completion.Wait();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        for (int workerIndex = 0; workerIndex < _workSignals.Length; ++workerIndex)
            _workSignals[workerIndex].Set();
        for (int workerIndex = 0; workerIndex < _threads.Length; ++workerIndex)
            _threads[workerIndex].Join();
        for (int workerIndex = 0; workerIndex < _workSignals.Length; ++workerIndex)
            _workSignals[workerIndex].Dispose();
        _completion.Dispose();
    }

    private void WorkerMain(object? state)
    {
        int workerIndex = (int)state!;
        AutoResetEvent signal = _workSignals[workerIndex];
        while (true)
        {
            signal.WaitOne();
            if (Volatile.Read(ref _disposed) != 0)
                return;
            // The caller waits for every worker. Signal even when the callback
            // throws, or SynchronousRun would wait forever.
            try { _processRanges(workerIndex); }
            finally { _completion.Signal(); }
        }
    }
}
