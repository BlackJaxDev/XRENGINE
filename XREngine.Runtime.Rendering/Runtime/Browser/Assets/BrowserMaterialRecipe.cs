using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Author-owned, explicit unlit browser representation for one desktop material identity.
/// The decoded RGBA8 pixels are supplied by the caller; desktop texture loaders are not used.
/// </summary>
public sealed class BrowserMaterialRecipe
{
    public BrowserMaterialRecipe(XRMaterial source, Vector4 linearTint, BrowserTextureData? decodedTexture = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Data = new BrowserMaterialData(linearTint, decodedTexture);
    }

    public XRMaterial Source { get; }
    public BrowserMaterialData Data { get; }
}
