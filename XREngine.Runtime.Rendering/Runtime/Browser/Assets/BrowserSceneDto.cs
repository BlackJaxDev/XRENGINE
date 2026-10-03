namespace XREngine.Rendering;

/// <summary>Versioned wire fields for a portable browser scene.</summary>
internal sealed class BrowserSceneDto
{
    public BrowserSceneDto() { }

    public int Version { get; set; }
    public float[]? CameraView { get; set; }
    public float[]? CameraProjection { get; set; }
    public BrowserSceneMeshDto[]? Meshes { get; set; }
    public BrowserSceneMaterialDto[]? Materials { get; set; }
    public BrowserSceneInstanceDto[]? Instances { get; set; }
}
