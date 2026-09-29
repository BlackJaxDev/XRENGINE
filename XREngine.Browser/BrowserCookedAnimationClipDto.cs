namespace XREngine.Browser;

/// <summary>Fixed-rate absolute local TRS samples; the engine's authored cadence defines clip duration and loop seams.</summary>
internal sealed class BrowserCookedAnimationClipDto
{
    public required string Name { get; init; }
    public int FramesPerSecond { get; init; }
    public int FrameCount { get; init; }
    public bool Loop { get; init; } = true;
    public required float[] Frames { get; init; }
    public float[] MorphWeights { get; init; } = [];
}
