using System.Diagnostics;

namespace XREngine.Rendering.Resources;

public sealed class RenderResourceGeneration(ResourceGenerationKey key, RenderPipelineResourceLayout layout) : IDisposable
{
    private readonly List<string> _diagnostics = [];
    private readonly Stopwatch _buildTimer = new();

    public ResourceGenerationKey Key { get; } = key;
    public RenderPipelineResourceLayout Layout { get; } = layout;
    public RenderResourceRegistry Registry { get; } = new();
    public RenderResourceGenerationStatus Status { get; private set; } = RenderResourceGenerationStatus.Created;
    public string? CommitReason { get; private set; }
    public string? RetirementReason { get; private set; }
    public TimeSpan BuildDuration { get; private set; }
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public bool IsReady => Status == RenderResourceGenerationStatus.Ready;
    public bool IsTerminal => Status is RenderResourceGenerationStatus.Failed or RenderResourceGenerationStatus.Superseded or RenderResourceGenerationStatus.Disposed;
    public int MaterializedSpecCount { get; internal set; }

    public int TextureCount => Registry.TextureRecords.Count;
    public int FrameBufferCount => Registry.FrameBufferRecords.Count;
    public int BufferCount => Registry.BufferRecords.Count;
    public int RenderBufferCount => Registry.RenderBufferRecords.Count;

    public void BeginBuild()
    {
        if (Status != RenderResourceGenerationStatus.Created)
            throw new InvalidOperationException($"Cannot begin building generation in state {Status}.");

        Status = RenderResourceGenerationStatus.Building;
        _buildTimer.Restart();
    }

    public void AddDiagnostic(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            _diagnostics.Add(message);
    }

    public void MarkReady()
    {
        _buildTimer.Stop();
        BuildDuration = _buildTimer.Elapsed;
        Status = RenderResourceGenerationStatus.Ready;
    }

    public void MarkActive(string reason)
    {
        CommitReason = reason;
        Status = RenderResourceGenerationStatus.Active;
    }

    public void MarkFailed(string diagnostic)
    {
        _buildTimer.Stop();
        BuildDuration = _buildTimer.Elapsed;
        AddDiagnostic(diagnostic);
        Status = RenderResourceGenerationStatus.Failed;
    }

    public void MarkSuperseded(string reason)
    {
        RetirementReason = reason;
        Status = RenderResourceGenerationStatus.Superseded;
    }

    public void MarkRetired(string reason)
    {
        RetirementReason = reason;
        Status = RenderResourceGenerationStatus.Retired;
    }

    public void Dispose()
    {
        if (Status == RenderResourceGenerationStatus.Disposed)
            return;

        Registry.DestroyAllPhysicalResources();
        Status = RenderResourceGenerationStatus.Disposed;
    }

    public override string ToString()
        => $"{Key} status={Status} textures={TextureCount} fbos={FrameBufferCount} buffers={BufferCount} renderbuffers={RenderBufferCount}";
}
