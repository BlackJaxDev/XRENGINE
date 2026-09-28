using System.Numerics;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

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

    /// <summary>Generates the explicitly authored unlit opaque shader for this recipe.</summary>
    public ShaderArtifact GenerateShader(ShaderCompileTarget target) =>
        BrowserMaterialShaderGenerator.Generate(
            new BrowserMaterialShaderDefinition("browser-unlit", "unlit", "opaque",
                Data.Texture is null ? "tint" : "texture"), target);
}
