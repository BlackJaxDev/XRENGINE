namespace XREngine.RenderBench;

/// <summary>Explicit content fixtures; paths resolve relative to the manifest, not the working directory.</summary>
public sealed record RuntimeMeasurementManifest
{
    public int Version { get; init; } = 1;
    public RuntimeMeasurementAsset? MonkeyBall { get; init; }
    public RuntimeMeasurementAsset? Avatar { get; init; }
}
