using System.Collections.Generic;
using System.Threading;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Writes render-readiness failures that would otherwise only stop a frame or an output: a
/// resource generation that fails to describe or materialize, or a native compute closure that
/// cannot find a required resource. Each line names the failure category, the pipeline, its
/// resource key (profile and feature mask) and the reason. The lines go to the auxiliary log
/// <see cref="LogFileName"/>, which Release sessions also write, and to the rendering warning log.
/// One failure (same category, pipeline and reason) is written at most once per
/// <see cref="ReportInterval"/>, so a failure that repeats every frame stays readable. Only the
/// failure path allocates.
/// </summary>
public static class RenderReadinessFailureLog
{
    /// <summary>Name of the auxiliary log file.</summary>
    public const string LogFileName = "render-readiness-failures.log";

    /// <summary>Shortest time between two lines for one failure.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);

    private static readonly object s_sync = new();
    private static readonly Dictionary<string, long> s_lastReportTicks = [];
    private static long s_reportCount;

    /// <summary>Number of failures reported since startup, including rate-limited repeats.</summary>
    public static long ReportCount => Interlocked.Read(ref s_reportCount);

    /// <summary>
    /// Reports one readiness failure. <paramref name="category"/> names the stage (for example
    /// "ResourceGeneration" or "NativeComputeClosure"), <paramref name="pipeline"/> the pipeline
    /// instance, and <paramref name="target"/> its resource key or profile.
    /// </summary>
    public static void Report(string category, string pipeline, string target, string reason)
    {
        Interlocked.Increment(ref s_reportCount);
        string key = string.Concat(category, "|", pipeline, "|", reason);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long interval = (long)(ReportInterval.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        lock (s_sync)
        {
            if (s_lastReportTicks.TryGetValue(key, out long last) && now - last < interval)
                return;
            s_lastReportTicks[key] = now;
        }

        string message = $"ERROR {category}: pipeline={pipeline} target={target} reason={reason}";
        Debug.WriteAuxiliaryLog(LogFileName, message);
        Debug.RenderingWarning("[RenderReadiness] {0}", message);
    }
}
