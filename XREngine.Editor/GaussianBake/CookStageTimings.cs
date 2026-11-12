using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Measures the wall time of each named stage of one Unity prefab cook and prints a summary table.
/// A stage can run more than once; its times add up, and the table shows the call count.
/// </summary>
/// <remarks>
/// Use <see cref="Measure"/> in a <c>using</c> statement around the work of one stage. The cook prints the summary at
/// the end, also when the cook fails, so a slow or failing stage is visible in every run.
/// </remarks>
internal sealed class CookStageTimings
{
    private readonly List<StageEntry> _stages = [];
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// Starts timing <paramref name="stageName"/>. Disposing the returned scope adds the elapsed time to the stage.
    /// </summary>
    public StageScope Measure(string stageName)
        => new(this, stageName, Stopwatch.GetTimestamp());

    /// <summary>
    /// Adds <paramref name="elapsedTicks"/> stopwatch ticks to <paramref name="stageName"/>, and creates the stage
    /// at the end of the table on first use.
    /// </summary>
    public void Add(string stageName, long elapsedTicks)
    {
        for (int i = 0; i < _stages.Count; i++)
        {
            if (!string.Equals(_stages[i].Name, stageName, StringComparison.Ordinal))
                continue;

            StageEntry existing = _stages[i];
            _stages[i] = existing with { Ticks = existing.Ticks + elapsedTicks, Count = existing.Count + 1 };
            return;
        }

        _stages.Add(new StageEntry(stageName, elapsedTicks, 1));
    }

    /// <summary>
    /// Formats one line per stage with its seconds, its share of the total cook time, and its call count. The last
    /// line shows the time that no stage measured, so missing timers are easy to see.
    /// </summary>
    public string FormatSummary(string prefix)
    {
        long totalTicks = Stopwatch.GetTimestamp() - _startTimestamp;
        double totalSeconds = totalTicks / (double)Stopwatch.Frequency;
        long measuredTicks = 0L;
        StringBuilder builder = new();
        builder.Append(CultureInfo.InvariantCulture, $"{prefix} Stage timings (total {totalSeconds:F1}s):");
        foreach (StageEntry stage in _stages)
        {
            measuredTicks += stage.Ticks;
            AppendLine(builder, prefix, stage.Name, stage.Ticks, totalTicks, stage.Count);
        }

        AppendLine(builder, prefix, "unmeasured", Math.Max(0L, totalTicks - measuredTicks), totalTicks, count: 0);
        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, string prefix, string name, long ticks, long totalTicks, int count)
    {
        double seconds = ticks / (double)Stopwatch.Frequency;
        double percent = totalTicks > 0L ? ticks * 100.0 / totalTicks : 0.0;
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"{prefix}   {name,-28} {seconds,8:F2}s {percent,5:F1}%");
        if (count > 1)
            builder.Append(CultureInfo.InvariantCulture, $"  (x{count})");
    }

    private readonly record struct StageEntry(string Name, long Ticks, int Count);

    /// <summary>
    /// Times one stage from creation until disposal.
    /// </summary>
    public readonly struct StageScope : IDisposable
    {
        private readonly CookStageTimings _owner;
        private readonly string _stageName;
        private readonly long _startTimestamp;

        internal StageScope(CookStageTimings owner, string stageName, long startTimestamp)
        {
            _owner = owner;
            _stageName = stageName;
            _startTimestamp = startTimestamp;
        }

        /// <summary>Adds the time since creation to the stage.</summary>
        public void Dispose()
            => _owner?.Add(_stageName, Stopwatch.GetTimestamp() - _startTimestamp);
    }
}
