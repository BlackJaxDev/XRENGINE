using System.Globalization;
using System.Text;
using XREngine.Data.Profiling;

namespace XREngine.Editor;

internal static class ProfilerDiagnosticDumps
{
    internal readonly record struct DumpResult(
        bool Success,
        string Message,
        string[] FileNames,
        string? Error = null);

    public static DumpResult DumpCpuFrameTimingHistory()
    {
        bool enabledByDump = EnsureCpuFrameSnapshotAvailable(
            out Engine.CodeProfiler.ProfilerFrameSnapshot? snapshot,
            out Dictionary<int, float[]> history);
        if (snapshot is null)
        {
            const string error = "No CPU profiler frame snapshot has been captured yet.";
            return new DumpResult(false, error, [], error);
        }

        DateTimeOffset timestamp = DateTimeOffset.Now;
        string fileName = BuildCpuFrameDumpFileName(timestamp);
        string content = BuildCpuFrameDumpContent(snapshot, history, timestamp, fileName);

        Debug.WriteAuxiliaryLog(fileName, content);
        string message = enabledByDump
            ? $"CPU frame logging was enabled and frame dump written: {fileName}"
            : $"CPU frame dump written: {fileName}";
        return new DumpResult(true, message, [fileName]);
    }

    public static DumpResult DumpGpuRenderPipelineTimingHistory(string? pipelineName)
    {
        if (!string.IsNullOrWhiteSpace(pipelineName))
        {
            string trimmed = pipelineName.Trim();
            if (Engine.Rendering.Stats.GpuPipelineProfiler.TryDumpGpuRenderPipelineTimingHistory(trimmed, out string fileName, out string? error))
                return new DumpResult(true, $"GPU timing dump written: {fileName}", [fileName]);

            string message = string.IsNullOrWhiteSpace(error)
                ? $"GPU timing dump failed for '{trimmed}'."
                : error;
            return new DumpResult(false, message, [], message);
        }

        return DumpAllGpuRenderPipelineTimingHistories();
    }

    public static DumpResult DumpAllGpuRenderPipelineTimingHistories()
    {
        if (Engine.Rendering.Stats.GpuPipelineProfiler.TryDumpAllGpuRenderPipelineTimingHistories(out string[] fileNames, out string? error))
        {
            string message = fileNames.Length == 1
                ? $"GPU timing dump written: {fileNames[0]}"
                : $"GPU timing dumps written: {fileNames.Length} files";
            return new DumpResult(true, message, fileNames);
        }

        string errorMessage = string.IsNullOrWhiteSpace(error)
            ? "GPU timing dump failed."
            : error;
        return new DumpResult(false, errorMessage, [], errorMessage);
    }

    public static string[] GetAvailableGpuRenderPipelineNames()
    {
        GpuPipelineTimingNodeData[] roots = Engine.Rendering.Stats.GpuPipelineProfiler.GetGpuRenderPipelineTimingRoots();
        if (roots.Length == 0)
            return [];

        List<string> names = new(roots.Length);
        for (int i = 0; i < roots.Length; i++)
        {
            string name = roots[i].Name;
            if (!string.IsNullOrWhiteSpace(name) &&
                !string.Equals(name, "Render Thread (CPU+Present)", StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        return names.ToArray();
    }

    public static string GetCurrentLogDirectory()
        => Debug.EnsureLogRunDirectory();

    public static string[] BuildAbsoluteLogPaths(string[] fileNames)
    {
        if (fileNames.Length == 0)
            return [];

        string logDirectory = GetCurrentLogDirectory();
        string[] paths = new string[fileNames.Length];
        for (int i = 0; i < fileNames.Length; i++)
            paths[i] = Path.Combine(logDirectory, fileNames[i]);
        return paths;
    }

    private static string BuildCpuFrameDumpFileName(DateTimeOffset timestamp)
    {
        string timestampSegment = timestamp.ToString("yyyy-MM-dd-HH-mm-ss-fff", CultureInfo.InvariantCulture);
        string nonce = Guid.NewGuid().ToString("N")[..8];
        return $"profiler-cpu-frame-{timestampSegment}-{nonce}.log";
    }

    private static bool EnsureCpuFrameSnapshotAvailable(
        out Engine.CodeProfiler.ProfilerFrameSnapshot? snapshot,
        out Dictionary<int, float[]> history)
    {
        bool enabledByDump = false;
        if (!Engine.Profiler.EnableFrameLogging)
        {
            Engine.Profiler.EnableFrameLogging = true;
            enabledByDump = true;
        }

        if (Engine.Profiler.TryGetSnapshot(out snapshot, out history) && snapshot is not null)
            return enabledByDump;

        long deadlineTicks = Environment.TickCount64 + 1000;
        do
        {
            Thread.Sleep(25);
            if (Engine.Profiler.TryGetSnapshot(out snapshot, out history) && snapshot is not null)
                return enabledByDump;
        }
        while (Environment.TickCount64 < deadlineTicks);

        snapshot = null;
        history = [];
        return enabledByDump;
    }

    private static string BuildCpuFrameDumpContent(
        Engine.CodeProfiler.ProfilerFrameSnapshot snapshot,
        Dictionary<int, float[]> history,
        DateTimeOffset timestamp,
        string fileName)
    {
        Engine.CodeProfiler.ProfilerThreadSnapshot[] orderedThreads = snapshot.Threads
            .OrderByDescending(static thread => thread.TotalTimeMs)
            .ToArray();
        CpuRootAggregate[] rootAggregates = BuildRootAggregates(orderedThreads);

        float totalThreadMs = 0.0f;
        for (int i = 0; i < orderedThreads.Length; i++)
            totalThreadMs += orderedThreads[i].TotalTimeMs;

        Engine.CodeProfiler.ProfilerThreadSnapshot? worstThread = orderedThreads.Length > 0 ? orderedThreads[0] : null;

        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("CPU Frame Timing Dump");
        sb.AppendLine("=====================");
        sb.AppendLine("This dump is intended for LLM-readable frame-loop analysis.");
        sb.AppendLine();
        sb.Append("Timestamp: ").AppendLine(timestamp.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("File: ").AppendLine(fileName);
        sb.Append("Process: ").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("FrameTimeSeconds: ").AppendLine(FormatMs(snapshot.FrameTime));
        sb.Append("ThreadCount: ").AppendLine(orderedThreads.Length.ToString(CultureInfo.InvariantCulture));
        sb.Append("TotalThreadMs: ").AppendLine(FormatMs(totalThreadMs));
        if (worstThread is not null)
        {
            sb.Append("WorstThreadId: ").AppendLine(worstThread.ThreadId.ToString(CultureInfo.InvariantCulture));
            sb.Append("WorstThreadMs: ").AppendLine(FormatMs(worstThread.TotalTimeMs));
        }
        sb.Append("ThreadHistoryCount: ").AppendLine((history?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        AppendThreadHistorySummary(sb, history);
        AppendRootAggregateSummary(sb, rootAggregates);
        AppendThreadHierarchy(sb, orderedThreads);
        AppendComponentTimings(sb, snapshot.ComponentTimings);

        return sb.ToString();
    }

    private static CpuRootAggregate[] BuildRootAggregates(Engine.CodeProfiler.ProfilerThreadSnapshot[] threads)
    {
        var aggregates = new Dictionary<string, CpuRootAggregate>(StringComparer.Ordinal);

        for (int threadIndex = 0; threadIndex < threads.Length; threadIndex++)
        {
            IReadOnlyList<Engine.CodeProfiler.ProfilerNodeSnapshot> roots = threads[threadIndex].RootNodes;
            for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                Engine.CodeProfiler.ProfilerNodeSnapshot root = roots[rootIndex];
                string key = $"{root.Name}\u001f{root.ScopeKind}";
                if (!aggregates.TryGetValue(key, out CpuRootAggregate? aggregate))
                {
                    aggregate = new CpuRootAggregate(root.Name, root.ScopeKind);
                    aggregates.Add(key, aggregate);
                }

                aggregate.Add(root.ElapsedMs, CalculateSelfMs(root));
            }
        }

        CpuRootAggregate[] result = aggregates.Values.ToArray();
        Array.Sort(result, static (left, right) => right.TotalMs.CompareTo(left.TotalMs));
        return result;
    }

    private static void AppendThreadHistorySummary(StringBuilder sb, Dictionary<int, float[]>? history)
    {
        sb.AppendLine("Thread History Summary");
        sb.AppendLine("----------------------");
        if (history is null || history.Count == 0)
        {
            sb.AppendLine("No thread history samples were available.");
            sb.AppendLine();
            return;
        }

        foreach (KeyValuePair<int, float[]> entry in history.OrderByDescending(static entry => MaxOrZero(entry.Value)))
        {
            float[] samples = entry.Value;
            if (samples.Length == 0)
                continue;

            float min = float.MaxValue;
            float max = float.MinValue;
            double total = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                float sample = samples[i];
                min = Math.Min(min, sample);
                max = Math.Max(max, sample);
                total += sample;
            }

            double avg = total / samples.Length;
            float latest = samples[^1];
            sb.Append("Thread ")
                .Append(entry.Key.ToString(CultureInfo.InvariantCulture))
                .Append(": latest=")
                .Append(FormatMs(latest))
                .Append(" ms avg=")
                .Append(avg.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" ms max=")
                .Append(FormatMs(max))
                .Append(" ms min=")
                .Append(FormatMs(min))
                .Append(" ms samples=")
                .AppendLine(samples.Length.ToString(CultureInfo.InvariantCulture));
        }

        sb.AppendLine();
    }

    private static void AppendRootAggregateSummary(StringBuilder sb, CpuRootAggregate[] aggregates)
    {
        sb.AppendLine("Root Method Aggregate Summary");
        sb.AppendLine("-----------------------------");
        if (aggregates.Length == 0)
        {
            sb.AppendLine("No root methods were captured.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("Name | Scope | TotalMs | SelfMs | AvgMs | PeakMs | Calls");
        for (int i = 0; i < aggregates.Length; i++)
        {
            CpuRootAggregate aggregate = aggregates[i];
            sb.Append(SanitizeSingleLine(aggregate.Name))
                .Append(" | ")
                .Append(aggregate.ScopeKind)
                .Append(" | ")
                .Append(FormatMs(aggregate.TotalMs))
                .Append(" | ")
                .Append(FormatMs(aggregate.SelfMs))
                .Append(" | ")
                .Append(FormatMs(aggregate.AverageMs))
                .Append(" | ")
                .Append(FormatMs(aggregate.PeakMs))
                .Append(" | ")
                .AppendLine(aggregate.Calls.ToString(CultureInfo.InvariantCulture));
        }

        sb.AppendLine();
    }

    private static void AppendThreadHierarchy(StringBuilder sb, Engine.CodeProfiler.ProfilerThreadSnapshot[] orderedThreads)
    {
        sb.AppendLine("Per-Thread Hierarchy");
        sb.AppendLine("--------------------");
        if (orderedThreads.Length == 0)
        {
            sb.AppendLine("No thread hierarchy was captured.");
            sb.AppendLine();
            return;
        }

        for (int threadIndex = 0; threadIndex < orderedThreads.Length; threadIndex++)
        {
            Engine.CodeProfiler.ProfilerThreadSnapshot thread = orderedThreads[threadIndex];
            sb.Append("Thread ")
                .Append(thread.ThreadId.ToString(CultureInfo.InvariantCulture))
                .Append(" total=")
                .Append(FormatMs(thread.TotalTimeMs))
                .AppendLine(" ms");

            Engine.CodeProfiler.ProfilerNodeSnapshot[] roots = thread.RootNodes
                .OrderByDescending(static node => node.ElapsedMs)
                .ToArray();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                AppendNode(sb, roots[rootIndex], depth: 1);

            sb.AppendLine();
        }
    }

    private static void AppendComponentTimings(
        StringBuilder sb,
        Engine.CodeProfiler.ProfilerComponentFrameSnapshot? componentTimings)
    {
        sb.AppendLine("Component Timings");
        sb.AppendLine("-----------------");
        if (componentTimings is null || componentTimings.Components.Count == 0)
        {
            sb.AppendLine("No component timings were captured.");
            return;
        }

        sb.Append("FrameTimeSeconds: ").AppendLine(FormatMs(componentTimings.FrameTime));
        sb.AppendLine("Component | Scene Node | Type | TotalMs | Calls | AvgUs | TickGroupMask");
        foreach (Engine.CodeProfiler.ProfilerComponentTimingSnapshot component in componentTimings.Components)
        {
            double averageUs = component.CallCount > 0
                ? component.ElapsedMs * 1000.0 / component.CallCount
                : 0.0;

            sb.Append(SanitizeSingleLine(component.ComponentName))
                .Append(" | ")
                .Append(SanitizeSingleLine(component.SceneNodeName))
                .Append(" | ")
                .Append(SanitizeSingleLine(component.ComponentType))
                .Append(" | ")
                .Append(FormatMs(component.ElapsedMs))
                .Append(" | ")
                .Append(component.CallCount.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(averageUs.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" | ")
                .AppendLine(component.TickGroupMask.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendNode(StringBuilder sb, Engine.CodeProfiler.ProfilerNodeSnapshot node, int depth)
    {
        sb.Append(' ', depth * 2)
            .Append("- [")
            .Append(node.ScopeKind)
            .Append("] ")
            .Append(SanitizeSingleLine(node.Name))
            .Append(": total=")
            .Append(FormatMs(node.ElapsedMs))
            .Append(" ms self=")
            .Append(FormatMs(CalculateSelfMs(node)))
            .Append(" ms children=")
            .AppendLine(node.Children.Count.ToString(CultureInfo.InvariantCulture));

        Engine.CodeProfiler.ProfilerNodeSnapshot[] children = node.Children
            .OrderByDescending(static child => child.ElapsedMs)
            .ToArray();
        for (int i = 0; i < children.Length; i++)
            AppendNode(sb, children[i], depth + 1);
    }

    private static float CalculateSelfMs(Engine.CodeProfiler.ProfilerNodeSnapshot node)
    {
        float childTotal = 0.0f;
        IReadOnlyList<Engine.CodeProfiler.ProfilerNodeSnapshot> children = node.Children;
        for (int i = 0; i < children.Count; i++)
            childTotal += children[i].ElapsedMs;

        return Math.Max(0.0f, node.ElapsedMs - childTotal);
    }

    private static float MaxOrZero(float[] samples)
    {
        if (samples.Length == 0)
            return 0.0f;

        float max = samples[0];
        for (int i = 1; i < samples.Length; i++)
            max = Math.Max(max, samples[i]);
        return max;
    }

    private static string FormatMs(float value)
        => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string SanitizeSingleLine(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "<unnamed>"
            : value.Replace('\r', ' ').Replace('\n', ' ');

    private sealed class CpuRootAggregate(string name, ProfilerScopeKind scopeKind)
    {
        public string Name { get; } = name;
        public ProfilerScopeKind ScopeKind { get; } = scopeKind;
        public float TotalMs { get; private set; }
        public float SelfMs { get; private set; }
        public float PeakMs { get; private set; }
        public int Calls { get; private set; }
        public float AverageMs => Calls <= 0 ? 0.0f : TotalMs / Calls;

        public void Add(float totalMs, float selfMs)
        {
            TotalMs += totalMs;
            SelfMs += selfMs;
            PeakMs = Math.Max(PeakMs, totalMs);
            Calls++;
        }
    }
}
