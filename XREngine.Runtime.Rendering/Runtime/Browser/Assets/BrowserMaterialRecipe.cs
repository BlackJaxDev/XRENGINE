using System.Numerics;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

/// <summary>
/// Author-owned, explicit browser representation for one desktop material identity.
/// The decoded RGBA8 pixels are supplied by the caller; desktop texture loaders are not used.
/// </summary>
public sealed class BrowserMaterialRecipe
{
    public BrowserMaterialRecipe(XRMaterial source, Vector4 linearTint, BrowserTextureData? decodedTexture = null,
        string alphaMode = "opaque", string shading = "unlit", string cullMode = "none",
        float alphaCutoff = 0.5f, bool castShadow = true, bool receiveShadow = true)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Data = new BrowserMaterialData(linearTint, decodedTexture, alphaMode, shading, cullMode, alphaCutoff, castShadow, receiveShadow);
    }

    public XRMaterial Source { get; }
    public BrowserMaterialData Data { get; }

    /// <summary>Generates the explicitly authored unlit opaque shader for this recipe.</summary>
    public ShaderArtifact GenerateShader(ShaderCompileTarget target)
    {
        if (Data.AlphaMode != "opaque" || Data.Shading != "unlit" || Data.CullMode != "none")
            throw new NotSupportedException("This shader artifact generator supports only unculled opaque unlit recipes; focused forward variants are owned by the browser pipeline.");
        return BrowserMaterialShaderGenerator.Generate(
            new BrowserMaterialShaderDefinition("browser-unlit", "unlit", "opaque",
                Data.Texture is null ? "tint" : "texture"), target);
    }
}
