using System.Diagnostics;
using System.Globalization;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Maps the GPU timing snapshot of the profiler to Workbench commands and passes. The map refreshes at most four times
/// each second, and only when the profiler snapshot, the instance, or the structure changed. Views draw the cached
/// badge strings.
/// </summary>
public sealed class RenderPipelineWorkbenchTiming
{
    private const double RefreshIntervalSeconds = 0.25;

    private readonly Dictionary<int, RenderPipelineTimingEntry> _byNode = new(128);
    private readonly Dictionary<int, string> _badgeByNode = new(128);
    private readonly Dictionary<int, string> _tooltipByNode = new(128);
    private readonly Dictionary<int, double> _byPass = new(64);
    private readonly Dictionary<int, string> _badgeByPass = new(64);
    private long _lastRefreshTimestamp;
    private RenderPipelineTimingSnapshot? _snapshot;
    private XRRenderPipelineInstance? _instance;
    private string _profilerKey = string.Empty;
    private int _structureBuild = -1;

    /// <summary>Gets whether GPU pipeline timing is enabled.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Gets whether the active renderer supports GPU pipeline timing.</summary>
    public bool Supported { get; private set; } = true;

    /// <summary>Gets whether any command of the selected instance has a timing.</summary>
    public bool HasData => _byNode.Count > 0;

    /// <summary>Gets the profiler status text.</summary>
    public string StatusText { get; private set; } = string.Empty;

    /// <summary>Gets the GPU time of the whole pipeline as text, or an empty string.</summary>
    public string TotalText { get; private set; } = string.Empty;

    /// <summary>Gets the highest newest-frame time of one leaf command, used to scale the heat overlay.</summary>
    public double MaxCommandMilliseconds { get; private set; }

    /// <summary>Gets a number that changes when the map changes.</summary>
    public int Version { get; private set; }

    /// <summary>Refreshes the map when the refresh interval passed and an input changed.</summary>
    public void Update(RenderPipelineWorkbenchModel model)
    {
        long now = Stopwatch.GetTimestamp();
        bool inputsChanged = !ReferenceEquals(_instance, model.Instance) || _structureBuild != model.StructureBuildCount;
        if (!inputsChanged && model.IsFrozen)
            return;
        if (!inputsChanged && Stopwatch.GetElapsedTime(_lastRefreshTimestamp, now).TotalSeconds < RefreshIntervalSeconds)
            return;

        _lastRefreshTimestamp = now;
        RenderPipelineTimingSnapshot snapshot = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GetTimingSnapshot();
        if (!inputsChanged && ReferenceEquals(snapshot, _snapshot))
            return;

        if (!ReferenceEquals(_instance, model.Instance))
        {
            _instance = model.Instance;
            _profilerKey = model.Instance?.ProfilerKey ?? string.Empty;
        }

        _snapshot = snapshot;
        _structureBuild = model.StructureBuildCount;
        Rebuild(model, snapshot);
    }

    /// <summary>Finds the timing of a command node.</summary>
    public bool TryGetNode(int nodeId, out RenderPipelineTimingEntry entry)
        => _byNode.TryGetValue(nodeId, out entry);

    /// <summary>Returns the badge text of a command node, or an empty string.</summary>
    public string GetNodeBadge(int nodeId)
        => _badgeByNode.TryGetValue(nodeId, out string? badge) ? badge : string.Empty;

    /// <summary>Returns the cached tooltip text of a command node, or an empty string.</summary>
    public string GetNodeTooltip(int nodeId)
        => _tooltipByNode.TryGetValue(nodeId, out string? tooltip) ? tooltip : string.Empty;

    /// <summary>Returns the newest-frame time of a pass, or zero.</summary>
    public double GetPassMilliseconds(int passIndex)
        => _byPass.GetValueOrDefault(passIndex);

    /// <summary>Returns the badge text of a pass, or an empty string.</summary>
    public string GetPassBadge(int passIndex)
        => _badgeByPass.TryGetValue(passIndex, out string? badge) ? badge : string.Empty;

    private void Rebuild(RenderPipelineWorkbenchModel model, RenderPipelineTimingSnapshot snapshot)
    {
        Version++;
        _byNode.Clear();
        _badgeByNode.Clear();
        _tooltipByNode.Clear();
        _byPass.Clear();
        _badgeByPass.Clear();
        MaxCommandMilliseconds = 0.0;
        TotalText = string.Empty;
        Enabled = snapshot.Enabled;
        Supported = snapshot.Supported;
        StatusText = snapshot.StatusMessage;

        if (_profilerKey.Length == 0 || !snapshot.HasPipeline(_profilerKey))
            return;

        if (snapshot.TryGetPipelineTotal(_profilerKey, out RenderPipelineTimingEntry total))
            TotalText = string.Concat("GPU ", RenderPipelineWorkbenchText.FormatMilliseconds(total.LastMilliseconds));

        RenderPipelineStructureSnapshot structure = model.Structure;
        for (int i = 0; i < structure.Commands.Count; i++)
        {
            RenderPipelineStructureCommand entry = structure.Commands[i];
            if (!snapshot.TryGet(_profilerKey, entry.ProfilingPath, out RenderPipelineTimingEntry timing))
                continue;

            int nodeId = RenderPipelineWorkbenchNodeIds.Get(entry.Command);
            _byNode[nodeId] = timing;
            _badgeByNode[nodeId] = RenderPipelineWorkbenchText.FormatMilliseconds(timing.LastMilliseconds);
            _tooltipByNode[nodeId] = FormatTooltip(timing);
            if (entry.Command is not ViewportStateRenderCommandBase && timing.LastMilliseconds > MaxCommandMilliseconds)
                MaxCommandMilliseconds = timing.LastMilliseconds;

            // A pass gets the time of the commands that contribute to it. A command with more than one pass counts
            // once for each pass, because the profiler does not split command time by pass.
            for (int p = 0; p < entry.PassIndices.Count; p++)
                _byPass[entry.PassIndices[p]] = _byPass.GetValueOrDefault(entry.PassIndices[p]) + timing.LastMilliseconds;
        }

        foreach (KeyValuePair<int, double> pair in _byPass)
            _badgeByPass[pair.Key] = RenderPipelineWorkbenchText.FormatMilliseconds(pair.Value);
    }

    /// <summary>Builds the tooltip lines of a timing entry.</summary>
    private static string FormatTooltip(in RenderPipelineTimingEntry entry)
        => string.Concat(
            "GPU last ", RenderPipelineWorkbenchText.FormatMilliseconds(entry.LastMilliseconds),
            "\nAverage ", RenderPipelineWorkbenchText.FormatMilliseconds(entry.AverageMilliseconds),
            "  min ", RenderPipelineWorkbenchText.FormatMilliseconds(entry.MinMilliseconds),
            "  max ", RenderPipelineWorkbenchText.FormatMilliseconds(entry.MaxMilliseconds),
            "\nFrames ", entry.FrameCount.ToString(CultureInfo.InvariantCulture));
}
