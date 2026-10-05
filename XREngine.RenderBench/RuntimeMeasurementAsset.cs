namespace XREngine.RenderBench;

/// <summary>Identifies measured content independently of a workstation's filesystem layout.</summary>
public sealed record RuntimeMeasurementAsset
{
    public required string Identity { get; init; }
    public string? Archive { get; init; }
    public string? Entry { get; init; }
    public string? Source { get; init; }
}
