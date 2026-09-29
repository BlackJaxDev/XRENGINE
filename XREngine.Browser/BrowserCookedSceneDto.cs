namespace XREngine.Browser;

/// <summary>A bounded increment of static scene instances and its camera.</summary>
internal sealed class BrowserCookedSceneDto
{
    public required float[] CameraView { get; init; }
    public required float[] CameraProjection { get; init; }
    public required BrowserCookedInstanceDto[] Instances { get; init; }
}
