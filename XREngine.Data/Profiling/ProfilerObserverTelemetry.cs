using System.Threading;

namespace XREngine.Data.Profiling;

/// <summary>
/// Publishes the most recent cost and visible-work observations produced by
/// the in-editor profiler. Release benchmark mode leaves these values at zero.
/// </summary>
public static class ProfilerObserverTelemetry
{
    private static long _ingestionMicros;
    private static long _aggregationMicros;
    private static long _graphPreparationMicros;
    private static long _tablePreparationMicros;
    private static long _imguiDrawMicros;
    private static int _visibleRows;
    private static int _graphSamples;

    public static double IngestionMilliseconds => Volatile.Read(ref _ingestionMicros) / 1000.0;
    public static double AggregationMilliseconds => Volatile.Read(ref _aggregationMicros) / 1000.0;
    public static double GraphPreparationMilliseconds => Volatile.Read(ref _graphPreparationMicros) / 1000.0;
    public static double TablePreparationMilliseconds => Volatile.Read(ref _tablePreparationMicros) / 1000.0;
    public static double ImGuiDrawMilliseconds => Volatile.Read(ref _imguiDrawMicros) / 1000.0;
    public static int VisibleRows => Volatile.Read(ref _visibleRows);
    public static int GraphSamples => Volatile.Read(ref _graphSamples);

    public static void RecordIngestion(double milliseconds)
        => Interlocked.Exchange(ref _ingestionMicros, ToMicroseconds(milliseconds));

    public static void RecordProcessing(
        double aggregationMilliseconds,
        double graphPreparationMilliseconds,
        double tablePreparationMilliseconds,
        int visibleRows,
        int graphSamples)
    {
        Interlocked.Exchange(ref _aggregationMicros, ToMicroseconds(aggregationMilliseconds));
        Interlocked.Exchange(ref _graphPreparationMicros, ToMicroseconds(graphPreparationMilliseconds));
        Interlocked.Exchange(ref _tablePreparationMicros, ToMicroseconds(tablePreparationMilliseconds));
        Interlocked.Exchange(ref _visibleRows, Math.Max(0, visibleRows));
        Interlocked.Exchange(ref _graphSamples, Math.Max(0, graphSamples));
    }

    public static void RecordImGuiDraw(double milliseconds)
        => Interlocked.Exchange(ref _imguiDrawMicros, ToMicroseconds(milliseconds));

    public static void Clear()
    {
        Interlocked.Exchange(ref _ingestionMicros, 0L);
        Interlocked.Exchange(ref _aggregationMicros, 0L);
        Interlocked.Exchange(ref _graphPreparationMicros, 0L);
        Interlocked.Exchange(ref _tablePreparationMicros, 0L);
        Interlocked.Exchange(ref _imguiDrawMicros, 0L);
        Interlocked.Exchange(ref _visibleRows, 0);
        Interlocked.Exchange(ref _graphSamples, 0);
    }

    private static long ToMicroseconds(double milliseconds)
        => milliseconds <= 0.0 ? 0L : (long)Math.Round(milliseconds * 1000.0);
}
