using System.Diagnostics;

namespace XREngine.Editor;

/// <summary>
/// Records a bounded inspector-only timing/allocation window. Read GetSnapshot through
/// the existing MCP invoke_method action; snapshot serialization is outside the draw path.
/// </summary>
internal readonly struct InspectorDrawMeasurement : IDisposable
{
    private const int Capacity = 128;
    private static readonly object Gate = new();
    private static readonly double[] CpuMilliseconds = new double[Capacity];
    private static readonly long[] AllocatedBytes = new long[Capacity];
    private static int _next;
    private static int _count;
    private static long _totalDraws;
    private static long _lastTimestamp;
    private readonly long _startTimestamp;
    private readonly long _startAllocatedBytes;

    public InspectorDrawMeasurement()
    {
        _startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        long timestamp = Stopwatch.GetTimestamp();
        double milliseconds = (timestamp - _startTimestamp) * 1000d / Stopwatch.Frequency;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - _startAllocatedBytes;
        lock (Gate)
        {
            CpuMilliseconds[_next] = milliseconds;
            AllocatedBytes[_next] = bytes;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
            _totalDraws++;
            _lastTimestamp = timestamp;
        }
    }

    /// <summary>Returns the most recent bounded draw window without collecting or pausing the engine.</summary>
    public static object GetSnapshot()
    {
        lock (Gate)
        {
            double cpuSum = 0, cpuPeak = 0;
            long bytesSum = 0, bytesPeak = 0;
            for (int i = 0; i < _count; i++)
            {
                cpuSum += CpuMilliseconds[i];
                cpuPeak = Math.Max(cpuPeak, CpuMilliseconds[i]);
                bytesSum += AllocatedBytes[i];
                bytesPeak = Math.Max(bytesPeak, AllocatedBytes[i]);
            }
            return new
            {
                samples = _count,
                total_draws = _totalDraws,
                average_cpu_ms = _count > 0 ? cpuSum / _count : 0,
                peak_cpu_ms = cpuPeak,
                average_allocated_bytes = _count > 0 ? (double)bytesSum / _count : 0,
                peak_allocated_bytes = bytesPeak,
                sample_age_ms = _count > 0 ? (Stopwatch.GetTimestamp() - _lastTimestamp) * 1000d / Stopwatch.Frequency : (double?)null,
            };
        }
    }
}
