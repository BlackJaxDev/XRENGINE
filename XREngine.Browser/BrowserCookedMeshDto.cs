namespace XREngine.Browser;

/// <summary>Cooked indexed position/UV geometry with no engine type names.</summary>
internal sealed class BrowserCookedMeshDto
{
    public required float[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
}
