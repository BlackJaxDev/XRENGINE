namespace XREngine.Data.Runtime.Ecs;

public struct RuntimeSystemAllocationStats
{
    public long LastBytes { get; private set; }
    public double AverageBytes { get; private set; }
    public long MaxBytes { get; private set; }
    public long SampleCount { get; private set; }
    public long OverBudgetCount { get; private set; }

    public void AddSample(long bytes, long budgetBytes)
    {
        if (bytes < 0L)
            bytes = 0L;

        LastBytes = bytes;
        MaxBytes = Math.Max(MaxBytes, bytes);
        SampleCount++;
        AverageBytes += (bytes - AverageBytes) / SampleCount;
        if (budgetBytes >= 0L && bytes > budgetBytes)
            OverBudgetCount++;
    }
}
