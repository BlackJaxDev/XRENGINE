namespace XREngine.Browser;

/// <summary>A bounded increment of scene instances and its camera, with optional cooked character collision.</summary>
internal sealed class BrowserCookedSceneDto
{
    public required float[] CameraView { get; init; }
    public required float[] CameraProjection { get; init; }
    public required BrowserCookedInstanceDto[] Instances { get; init; }
    public string? Collision { get; init; }
}
