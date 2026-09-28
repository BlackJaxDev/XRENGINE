namespace XREngine.Rendering;

/// <summary>Packed position/UV vertices and triangle indices on the scene wire.</summary>
internal sealed class BrowserSceneMeshDto
{
    public BrowserSceneMeshDto() { }

    public float[]? Vertices { get; set; }
    public uint[]? Indices { get; set; }
}
