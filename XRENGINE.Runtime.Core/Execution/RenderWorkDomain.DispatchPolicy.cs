using System.Diagnostics;

namespace XREngine.Execution;

public sealed partial class RenderWorkDomain
{
    private static readonly long InitialEstimatedTicksPerCostUnit =
        Math.Max(1L, Stopwatch.Frequency / 10_000L);
    private static readonly long MinimumDispatchHysteresisTicks =
        Math.Max(1L, Stopwatch.Frequency / 20_000L);

    private readonly long[] _laneSignalTimestamps;
    private readonly ManualResetEventSlim _workerWakeCalibrationSignal;
    private int _workerWakeCalibrationState;
    private int _workerWakeCalibrationRemaining;
    private long _migratableItemCount;
    private long _capPinnedMigratableItemCount;
    private long _parallelMigratableBatchCount;
    private long _inlineMigratableBatchCount;
    private long _insufficientParallelismBatchCount;
    private long _unprofitableBatchCount;
    private long _queueCostSampleCount;
    private long _queueCostTicks;
    private long _wakeCostSampleCount;
    private long _wakeCostTicks;
    private long _mergeCostSampleCount;
    private long _mergeCostTicks;
    private long _executorCostTicks;
    private long _estimatedTicksPerCostUnit = InitialEstimatedTicksPerCostUnit;

    internal bool ShouldMigrateWork(in RenderWorkDispatchProfile profile, bool requestInline)
    {
        Interlocked.Add(ref _migratableItemCount, profile.MigratableItemCount);
        Interlocked.Add(ref _capPinnedMigratableItemCount, profile.CapPinnedItemCount);

        if (profile.MigratableItemCount == 0)
            return false;

        if (requestInline || BackgroundWorkerCount == 0)
        {
            Interlocked.Increment(ref _inlineMigratableBatchCount);
            return false;
        }

        if (profile.IndependentMigratableItemCount < 2)
        {
            Interlocked.Increment(ref _inlineMigratableBatchCount);
            Interlocked.Increment(ref _insufficientParallelismBatchCount);
            return false;
        }

        long estimatedTicksPerCostUnit = Math.Max(1L, Volatile.Read(ref _estimatedTicksPerCostUnit));
        long totalEstimatedTicks = MultiplySaturating(
            profile.IndependentEstimatedCost,
            estimatedTicksPerCostUnit);
        long longestItemTicks = MultiplySaturating(
            profile.MaximumIndependentEstimatedCost,
            estimatedTicksPerCostUnit);
        int participatingLaneCount = Math.Min(
            LogicalLaneCount,
            profile.IndependentMigratableItemCount);
        long balancedParallelTicks = DivideCeiling(totalEstimatedTicks, participatingLaneCount);
        long predictedParallelTicks = Math.Max(longestItemTicks, balancedParallelTicks);
        long predictedSavingsTicks = Math.Max(0L, totalEstimatedTicks - predictedParallelTicks);

        long queueOperationTicks = GetMeasuredAverage(
            Interlocked.Read(ref _queueCostTicks),
            Interlocked.Read(ref _queueCostSampleCount));
        long wakeTicks = GetMeasuredAverage(
            Interlocked.Read(ref _wakeCostTicks),
            Interlocked.Read(ref _wakeCostSampleCount));
        long mergeTicks = GetMeasuredAverage(
            Interlocked.Read(ref _mergeCostTicks),
            Interlocked.Read(ref _mergeCostSampleCount));
        long queueCostTicks = MultiplySaturating(
            queueOperationTicks,
            2L * profile.MigratableItemCount);
        long wakeCostTicks = MultiplySaturating(
            wakeTicks,
            Math.Min(BackgroundWorkerCount, profile.IndependentMigratableItemCount - 1));
        long mergeCostTicks = MultiplySaturating(mergeTicks, 2L);
        long measuredOverheadTicks = AddSaturating(
            AddSaturating(queueCostTicks, wakeCostTicks),
            mergeCostTicks);
        long hysteresisTicks = Math.Max(
            MinimumDispatchHysteresisTicks,
            measuredOverheadTicks / 4L);
        long requiredSavingsTicks = AddSaturating(measuredOverheadTicks, hysteresisTicks);

        if (predictedSavingsTicks <= requiredSavingsTicks)
        {
            Interlocked.Increment(ref _inlineMigratableBatchCount);
            Interlocked.Increment(ref _unprofitableBatchCount);
            return false;
        }

        Interlocked.Increment(ref _parallelMigratableBatchCount);
        return true;
    }

    private void RecordQueueCost(long costStarted)
        => RecordCostSample(
            ref _queueCostSampleCount,
            ref _queueCostTicks,
            Math.Max(1L, Stopwatch.GetTimestamp() - costStarted));

    private void RecordWorkerWakeCost(int laneId)
    {
        long signalTimestamp = Interlocked.Exchange(ref _laneSignalTimestamps[laneId], 0L);
        if (signalTimestamp == 0L)
            return;

        RecordCostSample(
            ref _wakeCostSampleCount,
            ref _wakeCostTicks,
            Math.Max(1L, Stopwatch.GetTimestamp() - signalTimestamp));
    }

    private void RecordExecutorCost(int estimatedCost, long costStarted)
    {
        long elapsedTicks = Math.Max(1L, Stopwatch.GetTimestamp() - costStarted);
        Interlocked.Add(ref _executorCostTicks, elapsedTicks);
        UpdateExponentialAverage(
            ref _estimatedTicksPerCostUnit,
            Math.Max(1L, elapsedTicks / estimatedCost));
    }

    private void MeasureQueueAndMergeBaselines()
    {
        long queueCostStarted = Stopwatch.GetTimestamp();
        if (!_migratableQueues[0].TryEnqueue(
                default,
                out _,
                out _,
                out long enqueueLockWaitTicks))
            throw new InvalidOperationException("Render-work queue baseline calibration could not enqueue its probe.");
        RecordQueueLockWait(enqueueLockWaitTicks);
        RecordQueueCost(queueCostStarted);

        queueCostStarted = Stopwatch.GetTimestamp();
        if (!_migratableQueues[0].TryDequeue(
                out _,
                out long dequeueLockWaitTicks))
            throw new InvalidOperationException("Render-work queue baseline calibration could not dequeue its probe.");
        RecordQueueLockWait(dequeueLockWaitTicks);
        RecordQueueCost(queueCostStarted);

        long mergeCostStarted = Stopwatch.GetTimestamp();
        lock (_laneZeroExecutionSync)
        {
        }
        RecordCostSample(
            ref _mergeCostSampleCount,
            ref _mergeCostTicks,
            Math.Max(1L, Stopwatch.GetTimestamp() - mergeCostStarted));
    }

    private void CalibrateWorkerWakeCosts()
    {
        if (BackgroundWorkerCount == 0)
            return;

        _workerWakeCalibrationSignal.Reset();
        Volatile.Write(ref _workerWakeCalibrationRemaining, BackgroundWorkerCount);
        Volatile.Write(ref _workerWakeCalibrationState, 1);
        for (int laneId = 1; laneId < LogicalLaneCount; laneId++)
            SignalBackgroundLane(laneId);

        if (!_workerWakeCalibrationSignal.Wait(FatalBatchWait))
        {
            var exception = new TimeoutException(
                "Persistent render workers did not complete wake-cost calibration within the fatal lifecycle bound.");
            if (!Shutdown(waitForWorkers: true))
                Environment.FailFast(exception.Message, exception);
            throw exception;
        }

        Volatile.Write(ref _workerWakeCalibrationState, 0);
    }

    private static void RecordCostSample(ref long sampleCount, ref long totalTicks, long elapsedTicks)
    {
        Interlocked.Increment(ref sampleCount);
        Interlocked.Add(ref totalTicks, elapsedTicks);
    }

    private static void UpdateExponentialAverage(ref long target, long sample)
    {
        while (true)
        {
            long current = Volatile.Read(ref target);
            long next = current + ((sample - current) / 8L);
            if (next == current && sample != current)
                next += sample > current ? 1L : -1L;
            next = Math.Max(1L, next);
            if (Interlocked.CompareExchange(ref target, next, current) == current)
                return;
        }
    }

    private static long GetMeasuredAverage(long totalTicks, long sampleCount)
        => sampleCount <= 0L ? 0L : Math.Max(1L, totalTicks / sampleCount);

    private static long DivideCeiling(long value, int divisor)
        => value <= 0L ? 0L : 1L + ((value - 1L) / divisor);

    private static long MultiplySaturating(long left, long right)
    {
        if (left <= 0L || right <= 0L)
            return 0L;
        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }

    private static long AddSaturating(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;
}
