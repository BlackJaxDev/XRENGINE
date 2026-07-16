namespace XREngine.Rendering;

public readonly record struct MaterialTextureBindingResolution(
    int TextureIndex,
    string SamplerName,
    XRTexture? Texture,
    MaterialTextureBindingRung Rung,
    string Reason)
{
    public bool HasTexture => Texture is not null;
}
