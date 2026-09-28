namespace XREngine.Rendering;

/// <summary>Resource indices and row-vector transform on the scene wire.</summary>
internal sealed class BrowserSceneInstanceDto
{
    public BrowserSceneInstanceDto() { }

    public int MeshIndex { get; set; }
    public int MaterialIndex { get; set; }
    public float[]? ModelMatrix { get; set; }
}
