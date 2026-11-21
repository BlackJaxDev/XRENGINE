namespace XREngine.Data.Runtime.Ecs;

public static class RuntimeRangePartitioner
{
    public static int Partition(
        int itemCount,
        int maxRanges,
        int minItemsPerRange,
        Span<RuntimeRange> destination)
    {
        if (itemCount <= 0 || maxRanges <= 0 || destination.Length == 0)
            return 0;

        int clampedMin = Math.Max(1, minItemsPerRange);
        int desired = Math.Min(maxRanges, destination.Length);
        int rangeCount = Math.Min(desired, Math.Max(1, (itemCount + clampedMin - 1) / clampedMin));
        int baseSize = itemCount / rangeCount;
        int remainder = itemCount % rangeCount;
        int start = 0;

        for (int i = 0; i < rangeCount; i++)
        {
            int size = baseSize + (i < remainder ? 1 : 0);
            destination[i] = new RuntimeRange(start, start + size);
            start += size;
        }

        return rangeCount;
    }
}
