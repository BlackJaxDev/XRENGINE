using System.IO;
using System.Threading.Tasks;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Models.Materials;

/// <summary>
/// Helper class for loading and managing shaders.
/// All shader code is stored in external .glsl/.fs/.vs files in Build/CommonAssets/Shaders.
/// Shader snippets can be included using #pragma snippet "SnippetName" directive.
/// </summary>
public static class ShaderHelper
{
    private const string DepthNormalPrePassDefine = "XRENGINE_DEPTH_NORMAL_PREPASS";
    private const string ShadowCasterPassDefine = "XRENGINE_SHADOW_CASTER_PASS";

    private static IRuntimeShaderServices Services
        => RuntimeShaderServices.Current
        ?? throw new InvalidOperationException("RuntimeShaderServices.Current has not been configured.");

    /// <summary>
    /// Loads an engine shader from the Shaders asset directory.
    /// </summary>
    /// <param name="relativePath">Path relative to the Shaders directory (e.g., "Common/TexturedDeferred.fs")</param>
    /// <param name="type">Optional shader type override. If null, type is inferred from file extension.</param>
    public static XRShader LoadEngineShader(string relativePath, EShaderType? type = null)
    {
        XRShader source = Services.LoadEngineAsset<XRShader>(JobPriority.Highest, bypassJobThread: true, "Shaders", relativePath);
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
        XRShader source = await Services.LoadEngineAssetAsync<XRShader>(JobPriority.Highest, bypassJobThread: true, "Shaders", relativePath);
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
    /// Lit textured forward shader with normal map (Texture1), specular map (Texture2), and alpha mask (Texture3).
    /// Supports Forward+ lighting with specular intensity modulation and alpha cutoff.
    /// Texture layout: Texture0=Albedo, Texture1=Normal, Texture2=Specular, Texture3=AlphaMask.
    /// </summary>
    public static XRShader LitTextureNormalSpecFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalSpecForward.fs"));

    /// <summary>
    /// Lit textured forward shader with normal map (Texture1) and alpha mask (Texture2).
    /// Supports Forward+ lighting with alpha cutoff.
    /// Texture layout: Texture0=Albedo, Texture1=Normal, Texture2=AlphaMask.
    /// </summary>
    public static XRShader LitTextureNormalAlphaFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalAlphaForward.fs"));

    /// <summary>
    /// Lit textured forward shader with normal map (Texture1), specular map (Texture2), and alpha mask (Texture3).
    /// Supports Forward+ lighting with specular intensity modulation and alpha cutoff.
    /// Texture layout: Texture0=Albedo, Texture1=Normal, Texture2=Specular, Texture3=AlphaMask.
    /// </summary>
    public static XRShader LitTextureNormalSpecAlphaFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalSpecAlphaForward.fs"));

    /// <summary>
    /// Lit textured forward shader with specular map (Texture1).
    /// Supports Forward+ lighting with specular intensity modulation.
    /// Texture layout: Texture0=Albedo, Texture1=Specular.
    /// </summary>
    public static XRShader LitTextureSpecFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSpecForward.fs"));

    /// <summary>
    /// Lit textured forward shader with alpha mask (Texture1).
    /// Supports Forward+ lighting with alpha cutoff.
    /// Texture layout: Texture0=Albedo, Texture1=AlphaMask.
    /// </summary>
    public static XRShader LitTextureAlphaFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedAlphaForward.fs"));

    /// <summary>
    /// Lit textured forward shader with specular map (Texture1) and alpha mask (Texture2).
    /// Supports Forward+ lighting with specular intensity modulation and alpha cutoff.
    /// Texture layout: Texture0=Albedo, Texture1=Specular, Texture2=AlphaMask.
    /// </summary>
    public static XRShader LitTextureSpecAlphaFragForward()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSpecAlphaForward.fs"));

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

    #region Weighted OIT Forward Shaders

    public static XRShader LitTextureFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedForwardWeightedOit.fs"));

    public static XRShader LitTextureSilhouettePOMFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSilhouettePOMForwardWeightedOit.fs"));

    public static XRShader LitTextureNormalFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalForwardWeightedOit.fs"));

    public static XRShader LitTextureNormalSpecFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalSpecForwardWeightedOit.fs"));

    public static XRShader LitTextureNormalAlphaFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalAlphaForwardWeightedOit.fs"));

    public static XRShader LitTextureNormalSpecAlphaFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalSpecAlphaForwardWeightedOit.fs"));

    public static XRShader LitTextureSpecFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSpecForwardWeightedOit.fs"));

    public static XRShader LitTextureAlphaFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedAlphaForwardWeightedOit.fs"));

    public static XRShader LitTextureSpecAlphaFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSpecAlphaForwardWeightedOit.fs"));

    public static XRShader LitColorFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitColoredForwardWeightedOit.fs"));

    public static XRShader UnlitTextureFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "UnlitTexturedForwardWeightedOit.fs"));

    public static XRShader UnlitTextureStereoFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "UnlitTexturedStereoForwardWeightedOit.fs"));

    public static XRShader UnlitAlphaTextureFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "UnlitAlphaTexturedForwardWeightedOit.fs"));

    public static XRShader UnlitColorFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "UnlitColoredForwardWeightedOit.fs"));

    public static XRShader DeferredDecalForwardWeightedOit()
        => XRShader.EngineShader(Path.Combine("Scene3D", "DeferredDecalForwardWeightedOit.fs"), EShaderType.Fragment);

    public static XRShader LitTextureFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedForwardPpll.fs"));

    public static XRShader LitTextureSilhouettePOMFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSilhouettePOMForwardPpll.fs"));

    public static XRShader LitTextureNormalFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalForwardPpll.fs"));

    public static XRShader LitTextureNormalSpecFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalSpecForwardPpll.fs"));

    public static XRShader LitTextureNormalAlphaFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalAlphaForwardPpll.fs"));

    public static XRShader LitTextureNormalSpecAlphaFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalSpecAlphaForwardPpll.fs"));

    public static XRShader LitTextureSpecFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSpecForwardPpll.fs"));

    public static XRShader LitTextureAlphaFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedAlphaForwardPpll.fs"));

    public static XRShader LitTextureSpecAlphaFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSpecAlphaForwardPpll.fs"));

    public static XRShader LitColorFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitColoredForwardPpll.fs"));

    public static XRShader UnlitTextureFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "UnlitTexturedForwardPpll.fs"));

    public static XRShader UnlitTextureStereoFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "UnlitTexturedStereoForwardPpll.fs"));

    public static XRShader UnlitAlphaTextureFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "UnlitAlphaTexturedForwardPpll.fs"));

    public static XRShader UnlitColorFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "UnlitColoredForwardPpll.fs"));

    public static XRShader LitTextureFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedForwardDepthPeel.fs"));

    public static XRShader LitTextureSilhouettePOMFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSilhouettePOMForwardDepthPeel.fs"));

    public static XRShader LitTextureNormalFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalForwardDepthPeel.fs"));

    public static XRShader LitTextureNormalSpecFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalSpecForwardDepthPeel.fs"));

    public static XRShader LitTextureNormalAlphaFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalAlphaForwardDepthPeel.fs"));

    public static XRShader LitTextureNormalSpecAlphaFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedNormalSpecAlphaForwardDepthPeel.fs"));

    public static XRShader LitTextureSpecFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSpecForwardDepthPeel.fs"));

    public static XRShader LitTextureAlphaFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedAlphaForwardDepthPeel.fs"));

    public static XRShader LitTextureSpecAlphaFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitTexturedSpecAlphaForwardDepthPeel.fs"));

    public static XRShader LitColorFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitColoredForwardDepthPeel.fs"));

    public static XRShader UnlitTextureFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "UnlitTexturedForwardDepthPeel.fs"));

    public static XRShader UnlitTextureStereoFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "UnlitTexturedStereoForwardDepthPeel.fs"));

    public static XRShader UnlitAlphaTextureFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "UnlitAlphaTexturedForwardDepthPeel.fs"));

    public static XRShader UnlitColorFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "UnlitColoredForwardDepthPeel.fs"));

    public static XRShader? GetWeightedBlendedOitForwardVariant(XRShader? shader)
    {
        string? path = shader?.Source?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return fileName switch
        {
            "LitTexturedForward.fs" => LitTextureFragForwardWeightedOit(),
            "LitTexturedSilhouettePOMForward.fs" => LitTextureSilhouettePOMFragForwardWeightedOit(),
            "LitTexturedNormalForward.fs" => LitTextureNormalFragForwardWeightedOit(),
            "LitTexturedNormalSpecForward.fs" => LitTextureNormalSpecFragForwardWeightedOit(),
            "LitTexturedNormalAlphaForward.fs" => LitTextureNormalAlphaFragForwardWeightedOit(),
            "LitTexturedNormalSpecAlphaForward.fs" => LitTextureNormalSpecAlphaFragForwardWeightedOit(),
            "LitTexturedSpecForward.fs" => LitTextureSpecFragForwardWeightedOit(),
            "LitTexturedAlphaForward.fs" => LitTextureAlphaFragForwardWeightedOit(),
            "LitTexturedSpecAlphaForward.fs" => LitTextureSpecAlphaFragForwardWeightedOit(),
            "LitColoredForward.fs" => LitColorFragForwardWeightedOit(),
            "UnlitTexturedForward.fs" => UnlitTextureFragForwardWeightedOit(),
            "UnlitTexturedStereoForward.fs" => UnlitTextureStereoFragForwardWeightedOit(),
            "UnlitAlphaTexturedForward.fs" => UnlitAlphaTextureFragForwardWeightedOit(),
            "UnlitColoredForward.fs" => UnlitColorFragForwardWeightedOit(),
            "DeferredDecal.fs" => DeferredDecalForwardWeightedOit(),
            _ => null,
        };
    }

    public static XRShader? GetPerPixelLinkedListForwardVariant(XRShader? shader)
    {
        string? path = shader?.Source?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return fileName switch
        {
            "LitTexturedForward.fs" => LitTextureFragForwardPerPixelLinkedList(),
            "LitTexturedSilhouettePOMForward.fs" => LitTextureSilhouettePOMFragForwardPerPixelLinkedList(),
            "LitTexturedNormalForward.fs" => LitTextureNormalFragForwardPerPixelLinkedList(),
            "LitTexturedNormalSpecForward.fs" => LitTextureNormalSpecFragForwardPerPixelLinkedList(),
            "LitTexturedNormalAlphaForward.fs" => LitTextureNormalAlphaFragForwardPerPixelLinkedList(),
            "LitTexturedNormalSpecAlphaForward.fs" => LitTextureNormalSpecAlphaFragForwardPerPixelLinkedList(),
            "LitTexturedSpecForward.fs" => LitTextureSpecFragForwardPerPixelLinkedList(),
            "LitTexturedAlphaForward.fs" => LitTextureAlphaFragForwardPerPixelLinkedList(),
            "LitTexturedSpecAlphaForward.fs" => LitTextureSpecAlphaFragForwardPerPixelLinkedList(),
            "LitColoredForward.fs" => LitColorFragForwardPerPixelLinkedList(),
            "UnlitTexturedForward.fs" => UnlitTextureFragForwardPerPixelLinkedList(),
            "UnlitTexturedStereoForward.fs" => UnlitTextureStereoFragForwardPerPixelLinkedList(),
            "UnlitAlphaTexturedForward.fs" => UnlitAlphaTextureFragForwardPerPixelLinkedList(),
            "UnlitColoredForward.fs" => UnlitColorFragForwardPerPixelLinkedList(),
            _ => null,
        };
    }

    public static XRShader? GetDepthPeelingForwardVariant(XRShader? shader)
    {
        string? path = shader?.Source?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return fileName switch
        {
            "LitTexturedForward.fs" => LitTextureFragForwardDepthPeeling(),
            "LitTexturedSilhouettePOMForward.fs" => LitTextureSilhouettePOMFragForwardDepthPeeling(),
            "LitTexturedNormalForward.fs" => LitTextureNormalFragForwardDepthPeeling(),
            "LitTexturedNormalSpecForward.fs" => LitTextureNormalSpecFragForwardDepthPeeling(),
            "LitTexturedNormalAlphaForward.fs" => LitTextureNormalAlphaFragForwardDepthPeeling(),
            "LitTexturedNormalSpecAlphaForward.fs" => LitTextureNormalSpecAlphaFragForwardDepthPeeling(),
            "LitTexturedSpecForward.fs" => LitTextureSpecFragForwardDepthPeeling(),
            "LitTexturedAlphaForward.fs" => LitTextureAlphaFragForwardDepthPeeling(),
            "LitTexturedSpecAlphaForward.fs" => LitTextureSpecAlphaFragForwardDepthPeeling(),
            "LitColoredForward.fs" => LitColorFragForwardDepthPeeling(),
            "UnlitTexturedForward.fs" => UnlitTextureFragForwardDepthPeeling(),
            "UnlitTexturedStereoForward.fs" => UnlitTextureStereoFragForwardDepthPeeling(),
            "UnlitAlphaTexturedForward.fs" => UnlitAlphaTextureFragForwardDepthPeeling(),
            "UnlitColoredForward.fs" => UnlitColorFragForwardDepthPeeling(),
            _ => null,
        };
    }

    public static XRShader? GetStandardForwardVariant(XRShader? shader)
    {
        string? path = shader?.Source?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return fileName switch
        {
            "LitTexturedForwardWeightedOit.fs" => LitTextureFragForward(),
            "LitTexturedSilhouettePOMForwardWeightedOit.fs" => LitTextureSilhouettePOMFragForward(),
            "LitTexturedNormalForwardWeightedOit.fs" => LitTextureNormalFragForward(),
            "LitTexturedNormalSpecForwardWeightedOit.fs" => LitTextureNormalSpecFragForward(),
            "LitTexturedNormalAlphaForwardWeightedOit.fs" => LitTextureNormalAlphaFragForward(),
            "LitTexturedNormalSpecAlphaForwardWeightedOit.fs" => LitTextureNormalSpecAlphaFragForward(),
            "LitTexturedSpecForwardWeightedOit.fs" => LitTextureSpecFragForward(),
            "LitTexturedAlphaForwardWeightedOit.fs" => LitTextureAlphaFragForward(),
            "LitTexturedSpecAlphaForwardWeightedOit.fs" => LitTextureSpecAlphaFragForward(),
            "LitColoredForwardWeightedOit.fs" => LitColorFragForward(),
            "UnlitTexturedForwardWeightedOit.fs" => UnlitTextureFragForward(),
            "UnlitTexturedStereoForwardWeightedOit.fs" => UnlitTextureStereoFragForward(),
            "UnlitAlphaTexturedForwardWeightedOit.fs" => UnlitAlphaTextureFragForward(),
            "UnlitColoredForwardWeightedOit.fs" => UnlitColorFragForward(),
            "LitTexturedForwardPpll.fs" => LitTextureFragForward(),
            "LitTexturedSilhouettePOMForwardPpll.fs" => LitTextureSilhouettePOMFragForward(),
            "LitTexturedNormalForwardPpll.fs" => LitTextureNormalFragForward(),
            "LitTexturedNormalSpecForwardPpll.fs" => LitTextureNormalSpecFragForward(),
            "LitTexturedNormalAlphaForwardPpll.fs" => LitTextureNormalAlphaFragForward(),
            "LitTexturedNormalSpecAlphaForwardPpll.fs" => LitTextureNormalSpecAlphaFragForward(),
            "LitTexturedSpecForwardPpll.fs" => LitTextureSpecFragForward(),
            "LitTexturedAlphaForwardPpll.fs" => LitTextureAlphaFragForward(),
            "LitTexturedSpecAlphaForwardPpll.fs" => LitTextureSpecAlphaFragForward(),
            "LitColoredForwardPpll.fs" => LitColorFragForward(),
            "UnlitTexturedForwardPpll.fs" => UnlitTextureFragForward(),
            "UnlitTexturedStereoForwardPpll.fs" => UnlitTextureStereoFragForward(),
            "UnlitAlphaTexturedForwardPpll.fs" => UnlitAlphaTextureFragForward(),
            "UnlitColoredForwardPpll.fs" => UnlitColorFragForward(),
            "LitTexturedForwardDepthPeel.fs" => LitTextureFragForward(),
            "LitTexturedSilhouettePOMForwardDepthPeel.fs" => LitTextureSilhouettePOMFragForward(),
            "LitTexturedNormalForwardDepthPeel.fs" => LitTextureNormalFragForward(),
            "LitTexturedNormalSpecForwardDepthPeel.fs" => LitTextureNormalSpecFragForward(),
            "LitTexturedNormalAlphaForwardDepthPeel.fs" => LitTextureNormalAlphaFragForward(),
            "LitTexturedNormalSpecAlphaForwardDepthPeel.fs" => LitTextureNormalSpecAlphaFragForward(),
            "LitTexturedSpecForwardDepthPeel.fs" => LitTextureSpecFragForward(),
            "LitTexturedAlphaForwardDepthPeel.fs" => LitTextureAlphaFragForward(),
            "LitTexturedSpecAlphaForwardDepthPeel.fs" => LitTextureSpecAlphaFragForward(),
            "LitColoredForwardDepthPeel.fs" => LitColorFragForward(),
            "UnlitTexturedForwardDepthPeel.fs" => UnlitTextureFragForward(),
            "UnlitTexturedStereoForwardDepthPeel.fs" => UnlitTextureStereoFragForward(),
            "UnlitAlphaTexturedForwardDepthPeel.fs" => UnlitAlphaTextureFragForward(),
            "UnlitColoredForwardDepthPeel.fs" => UnlitColorFragForward(),
            "DeferredDecalForwardWeightedOit.fs" => XRShader.EngineShader(Path.Combine("Scene3D", "DeferredDecal.fs"), EShaderType.Fragment),
            _ => null,
        };
    }

    public static XRShader? GetDepthNormalPrePassForwardVariant(XRShader? shader)
    {
        string? path = shader?.Source?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return fileName switch
        {
            "LitTexturedNormalForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedNormalSpecForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedNormalAlphaForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedNormalSpecAlphaForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedSilhouettePOMForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            _ => null,
        };
    }

    public static XRShader? GetShadowCasterForwardVariant(XRShader? shader)
    {
        XRShader? sourceShader = GetStandardForwardVariant(shader) ?? shader;

        string? path = sourceShader?.Source?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return fileName switch
        {
            "LitTexturedAlphaForward.fs" => CreateDefinedShaderVariant(sourceShader, ShadowCasterPassDefine),
            "LitTexturedSpecAlphaForward.fs" => CreateDefinedShaderVariant(sourceShader, ShadowCasterPassDefine),
            "LitTexturedNormalAlphaForward.fs" => CreateDefinedShaderVariant(sourceShader, ShadowCasterPassDefine),
            "LitTexturedNormalSpecAlphaForward.fs" => CreateDefinedShaderVariant(sourceShader, ShadowCasterPassDefine),
            "UnlitAlphaTexturedForward.fs" => CreateDefinedShaderVariant(sourceShader, ShadowCasterPassDefine),
            _ => null,
        };
    }

    private static XRShader? CreateDefinedShaderVariant(XRShader? shader, string defineName)
    {
        if (shader?.Source?.Text is not { Length: > 0 } source)
            return null;

        string variantSource = InjectDefineAfterVersion(source, defineName);
        TextFile variantText = TextFile.FromText(variantSource);
        variantText.FilePath = shader.Source?.FilePath;
        variantText.Name = shader.Source?.Name;
        return new XRShader(shader.Type, variantText)
        {
            Name = shader.Name,
            GenerateAsync = shader.GenerateAsync,
        };
    }

    private static string InjectDefineAfterVersion(string source, string defineName)
    {
        int versionLineEnd = source.IndexOf('\n');
        if (versionLineEnd < 0)
            return $"#define {defineName}{Environment.NewLine}{source}";

        string header = source[..(versionLineEnd + 1)];
        string body = source[(versionLineEnd + 1)..].TrimStart('\r', '\n');
        return header + Environment.NewLine + $"#define {defineName}" + Environment.NewLine + Environment.NewLine + body;
    }

    public static int ResolveTransparentRenderPass(ETransparencyMode mode)
        => mode switch
        {
            ETransparencyMode.WeightedBlendedOit => (int)EDefaultRenderPass.WeightedBlendedOitForward,
            ETransparencyMode.PerPixelLinkedList => (int)EDefaultRenderPass.PerPixelLinkedListForward,
            ETransparencyMode.DepthPeeling => (int)EDefaultRenderPass.DepthPeelingForward,
            _ => (int)EDefaultRenderPass.TransparentForward,
        };

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
    public static readonly string Frag_Nothing = @"#version 450
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
