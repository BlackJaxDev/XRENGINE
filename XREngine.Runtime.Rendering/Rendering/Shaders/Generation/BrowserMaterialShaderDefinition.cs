namespace XREngine.Rendering.Shaders.Generation;

/// <summary>
/// Authored browser material semantics. Unsupported requests remain visible to the
/// generator instead of being inferred from desktop shader source.
/// </summary>
public sealed class BrowserMaterialShaderDefinition
{
    public BrowserMaterialShaderDefinition(string name, string shadingModel, string surface, string baseColor,
        bool skinning = false, bool storage = false, bool bindless = false)
    {
        Name = name;
        ShadingModel = shadingModel;
        Surface = surface;
        BaseColor = baseColor;
        Skinning = skinning;
        Storage = storage;
        Bindless = bindless;
    }

    public string Name { get; }
    public string ShadingModel { get; }
    public string Surface { get; }
    public string BaseColor { get; }
    public bool Skinning { get; }
    public bool Storage { get; }
    public bool Bindless { get; }
}
