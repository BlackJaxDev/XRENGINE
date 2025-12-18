using System.IO;
using System.Threading.Tasks;
using XREngine;

namespace XREngine.Rendering.Models.Materials;

/// <summary>
/// Helper class for loading and managing shaders.
/// All shader code is stored in external .glsl/.fs/.vs files in Build/CommonAssets/Shaders.
/// Shader snippets can be included using #pragma snippet "SnippetName" directive.
/// </summary>
public static class ShaderHelper
{
    /// <summary>
    /// Loads an engine shader from the Shaders asset directory.
    /// </summary>
    /// <param name="relativePath">Path relative to the Shaders directory (e.g., "Common/TexturedDeferred.fs")</param>
    /// <param name="type">Optional shader type override. If null, type is inferred from file extension.</param>
    public static XRShader LoadEngineShader(string relativePath, EShaderType? type = null)
    {
        XRShader source = Engine.Assets.LoadEngineAsset<XRShader>(JobPriority.Highest, bypassJobThread: true, "Shaders", relativePath);
        source._type = type ?? XRShader.ResolveType(Path.GetExtension(relativePath));
        return source;
    }

    /// <summary>
    /// Asynchronously loads an engine shader from the Shaders asset directory.
    /// </summary>
    /// <param name="relativePath">Path relative to the Shaders directory</param>
    /// <param name="type">Optional shader type override</param>
    public static async Task<XRShader> LoadEngineShaderAsync(string relativePath, EShaderType? type = null)
    {
        XRShader source = await Engine.Assets.LoadEngineAssetAsync<XRShader>(JobPriority.Highest, bypassJobThread: true, "Shaders", relativePath);
        source._type = type ?? XRShader.ResolveType(Path.GetExtension(relativePath));
        return source;
    }

    #region Forward Lit Shaders

    /// <summary>
    /// Basic lit textured forward shader with Forward+ lighting support.
    /// Uses ForwardLighting snippet.
    /// </summary>
    public static XRShader LitTextureFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedForward.fs"));

    /// <summary>
    /// Lit textured forward shader with Silhouette Parallax Occlusion Mapping.
    /// Uses ForwardLighting and ParallaxMapping snippets.
    /// </summary>
    public static XRShader LitTextureSilhouettePOMFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSilhouettePOMForward.fs"));

    /// <summary>
    /// Lit textured forward shader that also applies a normal map (Texture1) and supports Forward+ lighting.
    /// Uses derivative-based TBN reconstruction, so no explicit tangent attribute is required.
    /// </summary>
    public static XRShader LitTextureNormalFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalForward.fs"));

    /// <summary>
    /// Basic lit colored (no texture) forward shader with Forward+ lighting support.
    /// Uses ForwardLighting snippet.
    /// </summary>
    public static XRShader LitColorFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitColoredForward.fs"));

    #endregion

    #region Deferred Lit Shaders

    public static XRShader? LitTextureFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedDeferred.fs"));

    public static XRShader LitTextureSilhouettePOMFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedSilhouettePOMDeferred.fs"));

    public static XRShader? LitColorFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "ColoredDeferred.fs"));

    public static XRShader LitTextureNormalFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedNormalDeferred.fs"));

    public static XRShader LitTextureNormalMetallicFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedNormalMetallicDeferred.fs"));

    public static XRShader LitTextureNormalRoughnessMetallicDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedNormalMetallicRoughnessDeferred.fs"));

    public static XRShader LitTextureMetallicFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedMetallicDeferred.fs"));

    public static XRShader LitTextureMetallicRoughnessDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedMetallicRoughnessDeferred.fs"));

    public static XRShader LitTextureRoughnessFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedRoughnessDeferred.fs"));

    public static XRShader LitTextureMatcapDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedMatcapDeferred.fs"));

    public static XRShader LitTextureEmissiveDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedEmissiveDeferred.fs"));

    #endregion

    #region Unlit Shaders

    public static XRShader? UnlitTextureFragForward()
         => LoadEngineShader(Path.Combine("Common", "UnlitTexturedForward.fs"));

    public static XRShader? UnlitTextureStereoFragForward()
         => LoadEngineShader(Path.Combine("Common", "UnlitTexturedStereoForward.fs"));

    public static XRShader? UnlitAlphaTextureFragForward()
        => LoadEngineShader(Path.Combine("Common", "UnlitAlphaTexturedForward.fs"));

    public static XRShader? UnlitColorFragForward()
         => LoadEngineShader(Path.Combine("Common", "UnlitColoredForward.fs"));

    #endregion

    #region Utility Shaders

    /// <summary>
    /// Empty fragment shader that does nothing.
    /// </summary>
    public static XRShader FragNothing()
        => LoadEngineShader(Path.Combine("Common", "Nothing.fs"));

    /// <summary>
    /// Empty fragment shader source that does nothing.
    /// Use with: new XRShader(EShaderType.Fragment, ShaderHelper.Frag_Nothing)
    /// </summary>
    public static readonly string Frag_Nothing = @"#version 100
void main() { }";

    /// <summary>
    /// Fragment shader that outputs gl_FragCoord.z to depth.
    /// </summary>
    public static XRShader FragDepthOutput()
        => LoadEngineShader(Path.Combine("Common", "DepthOutput.fs"));

    /// <summary>
    /// Fragment shader source that outputs gl_FragCoord.z to depth.
    /// Use with: new XRShader(EShaderType.Fragment, ShaderHelper.Frag_DepthOutput)
    /// </summary>
    public static readonly string Frag_DepthOutput = @"#version 450
layout(location = 0) out float Depth;
void main()
{
    Depth = gl_FragCoord.z;
}";

    #endregion

    #region Gaussian Splatting Shaders

    public static XRShader? GaussianSplatVertex()
        => LoadEngineShader(Path.Combine("Gaussian", "GaussianSplat.vs"));

    public static XRShader? GaussianSplatFragment()
        => LoadEngineShader(Path.Combine("Gaussian", "GaussianSplat.fs"));

    #endregion

    #region Light Attenuation

    /// <summary>
    /// GLSL format string for light falloff calculation.
    /// Parameters: {0} = radius, {1} = distance
    /// </summary>
    public const string LightFalloffFormat = "pow(clamp(1.0 - pow({1} / {0}, 4), 0.0, 1.0), 2.0) / ({1} * {1} + 1.0);";

    /// <summary>
    /// Gets a GLSL light falloff expression with the given variable names.
    /// </summary>
    /// <param name="radiusName">Variable name for the light radius</param>
    /// <param name="distanceName">Variable name for the distance to light</param>
    /// <returns>GLSL expression for light attenuation</returns>
    public static string GetLightFalloff(string radiusName, string distanceName)
        => string.Format(LightFalloffFormat, radiusName, distanceName);

    #endregion
}
