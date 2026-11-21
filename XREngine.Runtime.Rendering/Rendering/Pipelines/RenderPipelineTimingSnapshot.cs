namespace XREngine.Rendering;

/// <summary>
/// An immutable copy of the GPU timing statistics of every profiled pipeline. Paths are the command profiling names
/// joined with <c>/</c>, without the pipeline key, so they match
/// <see cref="RenderGraph.Diagnostics.RenderPipelineStructureCommand.ProfilingPath"/>.
/// </summary>
public sealed class RenderPipelineTimingSnapshot
{
    private readonly Dictionary<string, Dictionary<string, RenderPipelineTimingEntry>> _byPipeline;

    /// <summary>Gets an empty snapshot for the disabled state.</summary>
    public static RenderPipelineTimingSnapshot Empty { get; } = new(0, false, true, string.Empty, string.Empty, new(StringComparer.Ordinal));

    internal RenderPipelineTimingSnapshot(
        int version,
        bool enabled,
        bool supported,
        string backendName,
        string statusMessage,
        Dictionary<string, Dictionary<string, RenderPipelineTimingEntry>> byPipeline)
    {
        Version = version;
        Enabled = enabled;
        Supported = supported;
        BackendName = backendName;
        StatusMessage = statusMessage;
        _byPipeline = byPipeline;
    }

    /// <summary>Gets a number that changes when the profiler records a new frame.</summary>
    public int Version { get; }

    /// <summary>Gets whether GPU pipeline timing is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Gets whether the active renderer supports GPU pipeline timing.</summary>
    public bool Supported { get; }

    /// <summary>Gets the backend that produced the timings.</summary>
    public string BackendName { get; }

    /// <summary>Gets the profiler status, such as the reason that timing is pending.</summary>
    public string StatusMessage { get; }

    /// <summary>Gets the profiler keys of the pipelines that have timings.</summary>
    public IEnumerable<string> PipelineKeys => _byPipeline.Keys;

    /// <summary>Returns whether the snapshot has timings for <paramref name="pipelineKey"/>.</summary>
    public bool HasPipeline(string pipelineKey)
        => _byPipeline.ContainsKey(pipelineKey);

    /// <summary>Finds the timing of <paramref name="profilingPath"/> in <paramref name="pipelineKey"/>.</summary>
    public bool TryGet(string pipelineKey, string profilingPath, out RenderPipelineTimingEntry entry)
    {
        if (_byPipeline.TryGetValue(pipelineKey, out Dictionary<string, RenderPipelineTimingEntry>? paths) &&
            paths.TryGetValue(profilingPath, out entry))
        {
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>Returns the timing of the whole pipeline, which is the root profiling node.</summary>
    public bool TryGetPipelineTotal(string pipelineKey, out RenderPipelineTimingEntry entry)
        => TryGet(pipelineKey, string.Empty, out entry);
}
