namespace XREngine.Browser;

/// <summary>One axis-aligned static collider in a cooked collision world.</summary>
internal sealed class BrowserCookedCollisionBoxDto
{
    public required float[] Minimum { get; init; }
    public required float[] Maximum { get; init; }
}
