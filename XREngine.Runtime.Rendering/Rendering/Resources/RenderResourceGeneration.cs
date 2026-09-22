using System.Diagnostics;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a single generation of render resources for a specific <see cref="RenderPipelineResourceLayout"/>.
/// </summary>
/// <param name="key">The key identifying the resource generation.</param>
/// <param name="layout">The layout of the render pipeline resources.</param>
/// <param name="ownerPipeline">The pipeline asset whose layout and callbacks own this generation.</param>
/// <param name="pipelineRevision">The viewport-instance-local pipeline revision that created this generation.</param>
public sealed class RenderResourceGeneration(
    ResourceGenerationKey key,
    RenderPipelineResourceLayout layout,
    RenderPipeline? ownerPipeline = null,
    ulong pipelineRevision = 0,
    bool isInitialBuild = false,
    IReadOnlyCollection<RenderPassMetadata>? passMetadata = null) : IDisposable
{
    private readonly List<string> _diagnostics = [];
    private readonly Dictionary<string, IIncrementalFrameBufferFactory> _incrementalFrameBufferFactories = new(StringComparer.Ordinal);
    private readonly Stopwatch _buildTimer = new();
    private XRGpuFence? _retirementFence;
    private int _failedRetirementFenceRetryCount;

    /// <summary>
    /// The key identifying the resource generation.
    /// </summary>
    public ResourceGenerationKey Key { get; } = key;
    /// <summary>
    /// Gets the pipeline asset that created this generation. The owner remains stable after an
    /// editor camera changes to another asset so destruction callbacks reach the correct asset.
    /// </summary>
    public RenderPipeline? OwnerPipeline { get; } = ownerPipeline;
    /// <summary>
    /// Gets the viewport-instance-local pipeline revision that created this generation.
    /// </summary>
    public ulong PipelineRevision { get; } = pipelineRevision;
    /// <summary>
    /// Gets whether this generation was requested before an active generation existed.
    /// </summary>
    public bool IsInitialBuild { get; } = isInitialBuild;
    /// <summary>
    /// The layout of the render pipeline resources.
    /// </summary>
    public RenderPipelineResourceLayout Layout { get; } = layout;
    /// <summary>
    /// Immutable render-graph metadata generated from <see cref="Layout"/>. Frame packages
    /// and backend planners consume this snapshot so AA switches cannot reuse declarations
    /// from an earlier resource layout.
    /// </summary>
    public IReadOnlyCollection<RenderPassMetadata> PassMetadata { get; } =
        passMetadata ?? Array.Empty<RenderPassMetadata>();
    /// <summary>
    /// The registry of render resources for this generation.
    /// </summary>
    public RenderResourceRegistry Registry { get; } = new();
    /// <summary>
    /// The current status of the resource generation.
    /// </summary>
    public RenderResourceGenerationStatus Status { get; private set; } = RenderResourceGenerationStatus.Created;
    /// <summary>
    /// The reason for committing the resource generation.
    /// </summary>
    public string? CommitReason { get; private set; }
    /// <summary>
    /// The reason for retiring the resource generation.
    /// </summary>
    public string? RetirementReason { get; private set; }
    /// <summary>
    /// The duration of the build process for the resource generation.
    /// </summary>
    public TimeSpan BuildDuration { get; private set; }
    /// <summary>
    /// The diagnostics messages for the resource generation.
    /// </summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    /// <summary>
    /// Indicates whether the resource generation is ready.
    /// </summary>
    public bool IsReady => Status == RenderResourceGenerationStatus.Ready;
    /// <summary>
    /// Indicates whether the resource generation is in a terminal state.
    /// </summary>
    public bool IsTerminal => Status is RenderResourceGenerationStatus.Failed or RenderResourceGenerationStatus.Superseded or RenderResourceGenerationStatus.Disposed;
    /// <summary>
    /// The count of materialized specifications for the resource generation.
    /// </summary>
    public int MaterializedSpecCount { get; internal set; }
    /// <summary>
    /// Gets the number of owner-thread materialization slices executed for this generation.
    /// </summary>
    public int MaterializationSliceCount { get; private set; }
    /// <summary>
    /// Gets the cumulative owner-thread time spent materializing this generation.
    /// </summary>
    public TimeSpan MaterializationWorkDuration { get; private set; }
    /// <summary>
    /// Gets the duration of the most recent materialization slice.
    /// </summary>
    public TimeSpan LastMaterializationSliceDuration { get; private set; }
    /// <summary>
    /// Gets the longest materialization slice observed for this generation.
    /// </summary>
    public TimeSpan WorstMaterializationSliceDuration { get; private set; }
    /// <summary>
    /// Gets the longest individual resource-spec materialization duration.
    /// </summary>
    public TimeSpan WorstMaterializationSpecDuration { get; private set; }
    /// <summary>
    /// Gets the name of the longest individual resource spec.
    /// </summary>
    public string? WorstMaterializationSpecName { get; private set; }
    /// <summary>
    /// Gets the kind of the longest individual resource spec.
    /// </summary>
    public RenderPipelineResourceKind? WorstMaterializationSpecKind { get; private set; }

    /// <summary>
    /// The count of textures in the resource generation.
    /// </summary>
    public int TextureCount => Registry.TextureRecords.Count;
    /// <summary>
    /// The count of frame buffers in the resource generation.
    /// </summary>
    public int FrameBufferCount => Registry.FrameBufferRecords.Count;
    /// <summary>
    /// The count of buffers in the resource generation.
    /// </summary>
    public int BufferCount => Registry.BufferRecords.Count;
    /// <summary>
    /// The count of render buffers in the resource generation.
    /// </summary>
    public int RenderBufferCount => Registry.RenderBufferRecords.Count;

    /// <summary>
    /// Begins the build process for the resource generation, transitioning its status to <see cref="RenderResourceGenerationStatus.Building"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public void BeginBuild()
    {
        if (Status != RenderResourceGenerationStatus.Created)
            throw new InvalidOperationException($"Cannot begin building generation in state {Status}.");

        Status = RenderResourceGenerationStatus.Building;
        _buildTimer.Restart();
    }

    /// <summary>
    /// Adds a diagnostic message to the resource generation.
    /// </summary>
    /// <param name="message">The diagnostic message to add.</param>
    public void AddDiagnostic(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            _diagnostics.Add(message);
    }

    /// <summary>
    /// Marks the resource generation as ready, stopping the build timer and recording the build duration.
    /// </summary>
    public void MarkReady()
    {
        _buildTimer.Stop();
        BuildDuration = _buildTimer.Elapsed;
        Status = RenderResourceGenerationStatus.Ready;
    }

    internal void RecordMaterializationSlice(TimeSpan duration)
    {
        MaterializationSliceCount++;
        MaterializationWorkDuration += duration;
        LastMaterializationSliceDuration = duration;
        if (duration > WorstMaterializationSliceDuration)
            WorstMaterializationSliceDuration = duration;
    }

    internal void RecordMaterializationSpec(RenderPipelineResourceSpec spec, TimeSpan duration)
    {
        if (duration <= WorstMaterializationSpecDuration)
            return;

        WorstMaterializationSpecDuration = duration;
        WorstMaterializationSpecName = spec.Name;
        WorstMaterializationSpecKind = spec.Kind;
    }

    internal IIncrementalFrameBufferFactory GetOrCreateIncrementalFrameBufferFactory(
        string resourceName,
        Func<IIncrementalFrameBufferFactory> factory)
    {
        if (_incrementalFrameBufferFactories.TryGetValue(resourceName, out IIncrementalFrameBufferFactory? existing))
            return existing;

        IIncrementalFrameBufferFactory created = factory();
        _incrementalFrameBufferFactories.Add(resourceName, created);
        return created;
    }

    internal void TransferIncrementalFrameBufferFactoryOwnership(string resourceName)
    {
        if (!_incrementalFrameBufferFactories.Remove(resourceName))
        {
            throw new InvalidOperationException(
                $"Incremental framebuffer factory '{resourceName}' was not retained by generation '{Key}'.");
        }
    }

    /// <summary>
    /// Marks the resource generation as active, indicating that it is currently in use and preventing it from being retired or superseded.
    /// </summary>
    /// <param name="reason">The reason for marking the resource generation as active.</param>
    public void MarkActive(string reason)
    {
        CommitReason = reason;
        Status = RenderResourceGenerationStatus.Active;
    }

    /// <summary>
    /// Marks the resource generation as failed, stopping the build timer, recording the build duration, and adding a diagnostic message.
    /// </summary>
    /// <param name="diagnostic">The diagnostic message to add when marking the resource generation as failed.</param>
    public void MarkFailed(string diagnostic)
    {
        _buildTimer.Stop();
        BuildDuration = _buildTimer.Elapsed;
        AddDiagnostic(diagnostic);
        Status = RenderResourceGenerationStatus.Failed;
    }

    /// <summary>
    /// Marks the resource generation as superseded, indicating that it has been replaced by a newer generation and is no longer valid for use.
    /// </summary>
    /// <param name="reason">The reason for marking the resource generation as superseded.</param>
    public void MarkSuperseded(string reason)
    {
        RetirementReason = reason;
        Status = RenderResourceGenerationStatus.Superseded;
    }

    /// <summary>
    /// Marks the resource generation as retired, indicating that it is no longer in use and has been retired from active service.
    /// </summary>
    /// <param name="reason">The reason for marking the resource generation as retired.</param>
    public void MarkRetired(string reason)
    {
        RetirementReason = reason;
        Status = RenderResourceGenerationStatus.Retired;
    }

    internal void ArmRetirementFence(XRGpuFence? fence)
    {
        _retirementFence?.Dispose();
        _retirementFence = fence;
        _failedRetirementFenceRetryCount = 0;
    }

    internal bool HasRetirementFence => _retirementFence is not null;

    /// <summary>Arms a generation that was retired while no completion receipt could be submitted.</summary>
    internal bool TryArmMissingRetirementFence(XRGpuFence? fence)
    {
        if (_retirementFence is not null || fence is null)
            return false;

        _retirementFence = fence;
        return true;
    }

    /// <summary>Replaces a failed completion receipt without treating an unsubmitted fence as completion.</summary>
    internal int ReplaceFailedRetirementFence(XRGpuFence fence)
    {
        _retirementFence?.Dispose();
        _retirementFence = fence;
        return ++_failedRetirementFenceRetryCount;
    }

    internal EGpuFenceStatus PollRetirementFence()
        => _retirementFence?.Poll() ?? EGpuFenceStatus.Pending;
    /// <summary>
    /// Disposes the resource generation, destroying all physical resources in the registry and transitioning its status to <see cref="RenderResourceGenerationStatus.Disposed"/>.
    /// </summary>
    public void Dispose()
    {
        if (Status == RenderResourceGenerationStatus.Disposed)
            return;

        Exception? firstFailure = null;
        try
        {
            _retirementFence?.Dispose();
        }
        catch (Exception ex)
        {
            firstFailure = ex;
        }
        _retirementFence = null;

        foreach (IIncrementalFrameBufferFactory factory in _incrementalFrameBufferFactories.Values)
        {
            try
            {
                factory.Dispose();
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }
        _incrementalFrameBufferFactories.Clear();

        try
        {
            Registry.DestroyAllPhysicalResources();
        }
        catch (Exception ex)
        {
            firstFailure ??= ex;
        }

        Status = RenderResourceGenerationStatus.Disposed;
        if (firstFailure is not null)
        {
            throw new InvalidOperationException(
                $"Render resource generation '{Key}' completed disposal with failures.",
                firstFailure);
        }
    }

    /// <summary>
    /// Returns a string representation of the resource generation, including its key, status, and counts of various resource types.
    /// </summary>
    /// <returns>A string representation of the resource generation.</returns>
    public override string ToString()
        => $"{Key} status={Status} textures={TextureCount} fbos={FrameBufferCount} buffers={BufferCount} renderbuffers={RenderBufferCount}";
}
