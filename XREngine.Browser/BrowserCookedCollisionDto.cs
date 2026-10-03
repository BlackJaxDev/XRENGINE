namespace XREngine.Browser;

/// <summary>Static collider world and initial character pose for one cooked scene.</summary>
internal sealed class BrowserCookedCollisionDto
{
    public required BrowserCookedCollisionBoxDto[] Boxes { get; init; }
    public required float[] Spawn { get; init; }
    public required float Yaw { get; init; }
    public required float Pitch { get; init; }
}
