using XREngine.Execution;

namespace XREngine.UnitTests.Core;

internal sealed class SchedulerTestRenderWorkExecutor : IRenderWorkExecutor, IDisposable
{
    private readonly int[]? _output;
    private readonly ManualResetEventSlim _overlap = new(false);
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private readonly object? _expectedAttachment;
    private int _active;
    private int _peakConcurrency;
    private int _dependencyState;
    private int _laneMask;
    private int _quarantineCount;
    private int _quarantineThreadId;

    internal SchedulerTestRenderWorkExecutor(int[]? output = null, object? expectedAttachment = null)
    {
        _output = output;
        _expectedAttachment = expectedAttachment;
    }

    internal int PeakConcurrency => Volatile.Read(ref _peakConcurrency);
    internal int LaneMask => Volatile.Read(ref _laneMask);
    internal int DependencyState => Volatile.Read(ref _dependencyState);
    internal int QuarantineCount => Volatile.Read(ref _quarantineCount);
    internal int QuarantineThreadId => Volatile.Read(ref _quarantineThreadId);
    internal ManualResetEventSlim Entered => _entered;
    internal ManualResetEventSlim Release => _release;

    public void Execute(in RenderWorkItem item, ref RenderWorkerContext context)
    {
        Interlocked.Or(ref _laneMask, 1 << context.LaneId);
        switch (item.OperationKind)
        {
            case 1:
                WriteDeterministicRange(item);
                return;
            case 2:
                ExecuteOverlapProbe();
                return;
            case 10:
                Interlocked.Or(ref _dependencyState, 0x1);
                return;
            case 11:
                RequireDependency(0x1);
                Interlocked.Or(ref _dependencyState, 0x2);
                return;
            case 12:
                RequireDependency(0x1);
                Interlocked.Or(ref _dependencyState, 0x4);
                return;
            case 13:
                RequireDependency(0x7);
                Interlocked.Or(ref _dependencyState, 0x8);
                return;
            case 20:
                throw new InvalidOperationException("Synthetic executor fault.");
            case 30:
                if (!ReferenceEquals(context.BackendAttachment, _expectedAttachment))
                    throw new InvalidOperationException("Lane-local backend attachment was not preserved.");
                return;
            case 40:
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(2));
                return;
            case 41:
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("Synthetic fault after cancellation.");
            case 50:
                return;
            default:
                throw new InvalidOperationException($"Unknown test operation {item.OperationKind}.");
        }
    }

    public void QuarantineFaultedBatch(in RenderWorkBatchFaultContext context)
    {
        Volatile.Write(ref _quarantineThreadId, Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref _quarantineCount);
    }

    public void Dispose()
    {
        _overlap.Dispose();
        _entered.Dispose();
        _release.Dispose();
    }

    private void WriteDeterministicRange(in RenderWorkItem item)
    {
        if (_output is null)
            throw new InvalidOperationException("No output buffer was configured.");

        int end = checked(item.SourceStart + item.SourceCount);
        for (int index = item.SourceStart; index < end; index++)
            _output[index] = unchecked(((index + 1) * 31) ^ 0x5A5A);
    }

    private void ExecuteOverlapProbe()
    {
        int active = Interlocked.Increment(ref _active);
        UpdatePeak(active);
        if (active >= 2)
            _overlap.Set();

        _overlap.Wait(TimeSpan.FromMilliseconds(500));
        Thread.SpinWait(50_000);
        Interlocked.Decrement(ref _active);
    }

    private void RequireDependency(int requiredMask)
    {
        int state = Volatile.Read(ref _dependencyState);
        if ((state & requiredMask) != requiredMask)
        {
            throw new InvalidOperationException(
                $"Dependency order violation: required=0x{requiredMask:X}, actual=0x{state:X}.");
        }
    }

    private void UpdatePeak(int candidate)
    {
        while (true)
        {
            int current = Volatile.Read(ref _peakConcurrency);
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref _peakConcurrency, candidate, current) == current)
                return;
        }
    }
}
