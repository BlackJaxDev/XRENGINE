using System.Diagnostics;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a single generation of render resources for a specific <see cref="RenderPipelineResourceLayout"/>.
/// </summary>
/// <param name="key">The key identifying the resource generation.</param>
/// <param name="layout">The layout of the render pipeline resources.</param>
public sealed class RenderResourceGeneration(ResourceGenerationKey key, RenderPipelineResourceLayout layout) : IDisposable
{
    private readonly List<string> _diagnostics = [];
    private readonly Stopwatch _buildTimer = new();

    /// <summary>
    /// The key identifying the resource generation.
    /// </summary>
    public ResourceGenerationKey Key { get; } = key;
    /// <summary>
    /// The layout of the render pipeline resources.
    /// </summary>
    public RenderPipelineResourceLayout Layout { get; } = layout;
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

    /// <summary>
    /// Disposes the resource generation, destroying all physical resources in the registry and transitioning its status to <see cref="RenderResourceGenerationStatus.Disposed"/>.
    /// </summary>
    public void Dispose()
    {
        if (Status == RenderResourceGenerationStatus.Disposed)
            return;

        Registry.DestroyAllPhysicalResources();
        Status = RenderResourceGenerationStatus.Disposed;
    }

    /// <summary>
    /// Returns a string representation of the resource generation, including its key, status, and counts of various resource types.
    /// </summary>
    /// <returns>A string representation of the resource generation.</returns>
    public override string ToString()
        => $"{Key} status={Status} textures={TextureCount} fbos={FrameBufferCount} buffers={BufferCount} renderbuffers={RenderBufferCount}";
}
