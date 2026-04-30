using System.Collections.Concurrent;
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
    private const string PointShadowCasterPassDefine = "XRENGINE_POINT_SHADOW_CASTER_PASS";
    private const string WeightedBlendedOitDefine = "XRENGINE_FORWARD_WEIGHTED_OIT";
    private const string PerPixelLinkedListDefine = "XRENGINE_FORWARD_PPLL";
    private const string DepthPeelingDefine = "XRENGINE_FORWARD_DEPTH_PEEL";
    private const string UberImportMaterialDefine = "XRENGINE_UBER_IMPORT_MATERIAL";
    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<DefinedVariantCacheKey, string>> DefinedVariantSourceCache = new();
    private static readonly ConcurrentDictionary<DefinedVariantShaderCacheKey, XRShader> DefinedVariantShaderCache = new();
    private static readonly ConcurrentDictionary<EngineShaderCacheKey, Task<XRShader>> EngineShaderLoadTasks = new();
    private static readonly HashSet<string> DefineBasedTransparencyForwardShaderFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "LitTexturedForward.fs",
        "LitTexturedSilhouettePOMForward.fs",
        "LitTexturedNormalForward.fs",
        "LitTexturedNormalSpecForward.fs",
        "LitTexturedNormalAlphaForward.fs",
        "LitTexturedNormalSpecAlphaForward.fs",
        "LitTexturedSpecForward.fs",
        "LitTexturedAlphaForward.fs",
        "LitTexturedSpecAlphaForward.fs",
        "UnlitTexturedForward.fs",
        "UnlitTexturedStereoForward.fs",
        "UnlitAlphaTexturedForward.fs",
        "UberShader.frag",
    };

    private static IRuntimeShaderServices Services
        => RuntimeShaderServices.Current
        ?? throw new InvalidOperationException("RuntimeShaderServices.Current has not been configured.");

    public static XRShader UberFragForward()
        => LoadEngineShader(Path.Combine("Uber", "UberShader.frag"), EShaderType.Fragment);

    private readonly record struct DefinedVariantCacheKey(string DefineName, string SourceText);
    private readonly record struct DefinedVariantShaderCacheKey(string DefineName, EShaderType ShaderType, string SourceText, string? FilePath, string? SourceName);
    private readonly record struct EngineShaderCacheKey(string RelativePath, EShaderType ShaderType);

    internal static void ClearDefinedVariantSourceCache()
    {
        DefinedVariantSourceCache.Clear();
        DefinedVariantShaderCache.Clear();
    }

    private static Task<XRShader> GetOrCreateEngineShaderTask(string relativePath, EShaderType shaderType)
    {
        string normalizedPath = relativePath.Replace('\\', '/');
        return EngineShaderLoadTasks.GetOrAdd(
            new EngineShaderCacheKey(normalizedPath, shaderType),
            static key => LoadAndWarmEngineShaderAsync(key));
    }

    private static async Task<XRShader> LoadAndWarmEngineShaderAsync(EngineShaderCacheKey key)
    {
        XRShader source = await Services.LoadEngineAssetAsync<XRShader>(JobPriority.Highest, bypassJobThread: false, "Shaders", key.RelativePath).ConfigureAwait(false);
        source._type = key.ShaderType;
        source.TryGetResolvedSource(out _, annotateIncludes: false, logFailures: true);
        return source;
    }

    public static void WarmEngineShader(string relativePath, EShaderType? type = null)
    {
        EShaderType shaderType = type ?? XRShader.ResolveType(Path.GetExtension(relativePath));
        _ = GetOrCreateEngineShaderTask(relativePath, shaderType);
    }

    /// <summary>
    /// Loads an engine shader from the Shaders asset directory.
    /// </summary>
    /// <param name="relativePath">Path relative to the Shaders directory (e.g., "Common/TexturedDeferred.fs")</param>
    /// <param name="type">Optional shader type override. If null, type is inferred from file extension.</param>
    public static XRShader LoadEngineShader(string relativePath, EShaderType? type = null)
    {
        EShaderType shaderType = type ?? XRShader.ResolveType(Path.GetExtension(relativePath));
        XRShader source = GetOrCreateEngineShaderTask(relativePath, shaderType).GetAwaiter().GetResult();
        source._type = shaderType;
        return source;
    }

    /// <summary>
    /// Asynchronously loads an engine shader from the Shaders asset directory.
    /// </summary>
    /// <param name="relativePath">Path relative to the Shaders directory</param>
    /// <param name="type">Optional shader type override</param>
    public static async Task<XRShader> LoadEngineShaderAsync(string relativePath, EShaderType? type = null)
    {
        EShaderType shaderType = type ?? XRShader.ResolveType(Path.GetExtension(relativePath));
        XRShader source = await GetOrCreateEngineShaderTask(relativePath, shaderType).ConfigureAwait(false);
        source._type = shaderType;
        return source;
    }

    /// <summary>
    /// Lean import-focused Uber fragment shader variant that strips optional features
    /// the model importer never binds, keeping GL fragment uniform pressure below
    /// older driver register budgets.
    /// </summary>
    public static XRShader UberImportFragForward()
        => CreateDefinedShaderVariant(
            UberFragForward(),
            UberImportMaterialDefine)!;

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

    public static XRShader LitTextureNormalSpecFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedNormalSpecDeferred.fs"));

    public static XRShader LitTextureNormalAlphaFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedNormalAlphaDeferred.fs"));

    public static XRShader LitTextureNormalSpecAlphaFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedNormalSpecAlphaDeferred.fs"));

    public static XRShader LitTextureNormalMetallicFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedNormalMetallicDeferred.fs"));

    public static XRShader LitTextureNormalRoughnessMetallicDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedNormalMetallicRoughnessDeferred.fs"));

    public static XRShader LitTextureSpecFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedSpecDeferred.fs"));

    public static XRShader LitTextureAlphaFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedAlphaDeferred.fs"));

    public static XRShader LitTextureSpecAlphaFragDeferred()
        => LoadEngineShader(Path.Combine("Common", "TexturedSpecAlphaDeferred.fs"));

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

    public static XRShader DynamicWaterFragForward()
        => LoadEngineShader(Path.Combine("Common", "WaterDynamicForward.fs"));

    public static XRShader DynamicWaterTessCtrlForward()
        => LoadEngineShader(Path.Combine("Common", "WaterDynamicForward.tesc"));

    public static XRShader DynamicWaterTessEvalForward()
        => LoadEngineShader(Path.Combine("Common", "WaterDynamicForward.tese"));

    #endregion

    #region Weighted OIT Forward Shaders

    public static XRShader LitTextureFragForwardWeightedOit()
        => CreateDefinedShaderVariant(LitTextureFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader LitTextureSilhouettePOMFragForwardWeightedOit()
        => CreateDefinedShaderVariant(LitTextureSilhouettePOMFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader LitTextureNormalFragForwardWeightedOit()
        => CreateDefinedShaderVariant(LitTextureNormalFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader LitTextureNormalSpecFragForwardWeightedOit()
        => CreateDefinedShaderVariant(LitTextureNormalSpecFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader LitTextureNormalAlphaFragForwardWeightedOit()
        => CreateDefinedShaderVariant(LitTextureNormalAlphaFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader LitTextureNormalSpecAlphaFragForwardWeightedOit()
        => CreateDefinedShaderVariant(LitTextureNormalSpecAlphaFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader LitTextureSpecFragForwardWeightedOit()
        => CreateDefinedShaderVariant(LitTextureSpecFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader LitTextureAlphaFragForwardWeightedOit()
        => CreateDefinedShaderVariant(LitTextureAlphaFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader LitTextureSpecAlphaFragForwardWeightedOit()
        => CreateDefinedShaderVariant(LitTextureSpecAlphaFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader LitColorFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "LitColoredForwardWeightedOit.fs"));

    public static XRShader UnlitTextureFragForwardWeightedOit()
        => CreateDefinedShaderVariant(UnlitTextureFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader UnlitTextureStereoFragForwardWeightedOit()
        => CreateDefinedShaderVariant(UnlitTextureStereoFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader UnlitAlphaTextureFragForwardWeightedOit()
        => CreateDefinedShaderVariant(UnlitAlphaTextureFragForward(), WeightedBlendedOitDefine)!;

    public static XRShader UnlitColorFragForwardWeightedOit()
        => LoadEngineShader(Path.Combine("Common", "UnlitColoredForwardWeightedOit.fs"));

    public static XRShader DeferredDecalForwardWeightedOit()
        => XRShader.EngineShader(Path.Combine("Scene3D", "DeferredDecalForwardWeightedOit.fs"), EShaderType.Fragment);

    public static XRShader LitTextureFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(LitTextureFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader LitTextureSilhouettePOMFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(LitTextureSilhouettePOMFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader LitTextureNormalFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(LitTextureNormalFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader LitTextureNormalSpecFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(LitTextureNormalSpecFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader LitTextureNormalAlphaFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(LitTextureNormalAlphaFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader LitTextureNormalSpecAlphaFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(LitTextureNormalSpecAlphaFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader LitTextureSpecFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(LitTextureSpecFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader LitTextureAlphaFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(LitTextureAlphaFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader LitTextureSpecAlphaFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(LitTextureSpecAlphaFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader LitColorFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "LitColoredForwardPpll.fs"));

    public static XRShader UnlitTextureFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(UnlitTextureFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader UnlitTextureStereoFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(UnlitTextureStereoFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader UnlitAlphaTextureFragForwardPerPixelLinkedList()
        => CreateDefinedShaderVariant(UnlitAlphaTextureFragForward(), PerPixelLinkedListDefine)!;

    public static XRShader UnlitColorFragForwardPerPixelLinkedList()
        => LoadEngineShader(Path.Combine("Common", "UnlitColoredForwardPpll.fs"));

    public static XRShader LitTextureFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(LitTextureFragForward(), DepthPeelingDefine)!;

    public static XRShader LitTextureSilhouettePOMFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(LitTextureSilhouettePOMFragForward(), DepthPeelingDefine)!;

    public static XRShader LitTextureNormalFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(LitTextureNormalFragForward(), DepthPeelingDefine)!;

    public static XRShader LitTextureNormalSpecFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(LitTextureNormalSpecFragForward(), DepthPeelingDefine)!;

    public static XRShader LitTextureNormalAlphaFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(LitTextureNormalAlphaFragForward(), DepthPeelingDefine)!;

    public static XRShader LitTextureNormalSpecAlphaFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(LitTextureNormalSpecAlphaFragForward(), DepthPeelingDefine)!;

    public static XRShader LitTextureSpecFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(LitTextureSpecFragForward(), DepthPeelingDefine)!;

    public static XRShader LitTextureAlphaFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(LitTextureAlphaFragForward(), DepthPeelingDefine)!;

    public static XRShader LitTextureSpecAlphaFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(LitTextureSpecAlphaFragForward(), DepthPeelingDefine)!;

    public static XRShader LitColorFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "LitColoredForwardDepthPeel.fs"));

    public static XRShader UnlitTextureFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(UnlitTextureFragForward(), DepthPeelingDefine)!;

    public static XRShader UnlitTextureStereoFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(UnlitTextureStereoFragForward(), DepthPeelingDefine)!;

    public static XRShader UnlitAlphaTextureFragForwardDepthPeeling()
        => CreateDefinedShaderVariant(UnlitAlphaTextureFragForward(), DepthPeelingDefine)!;

    public static XRShader UnlitColorFragForwardDepthPeeling()
        => LoadEngineShader(Path.Combine("Common", "UnlitColoredForwardDepthPeel.fs"));

    private static bool HasTransparencyForwardVariantDefine(XRShader? shader)
    {
        string? sourceText = shader?.Source?.Text;
        if (string.IsNullOrWhiteSpace(sourceText))
            return false;

        return sourceText.Contains($"#define {WeightedBlendedOitDefine}", StringComparison.Ordinal) ||
            sourceText.Contains($"#define {PerPixelLinkedListDefine}", StringComparison.Ordinal) ||
            sourceText.Contains($"#define {DepthPeelingDefine}", StringComparison.Ordinal);
    }

    private static bool HasDefine(XRShader? shader, string defineName)
    {
        string? sourceText = shader?.Source?.Text;
        return !string.IsNullOrWhiteSpace(sourceText) &&
            sourceText.Contains($"#define {defineName}", StringComparison.Ordinal);
    }

    private static XRShader? LoadStandardForwardShaderByFileName(string fileName)
        => fileName switch
        {
            "LitTexturedForward.fs" => LitTextureFragForward(),
            "LitTexturedSilhouettePOMForward.fs" => LitTextureSilhouettePOMFragForward(),
            "LitTexturedNormalForward.fs" => LitTextureNormalFragForward(),
            "LitTexturedNormalSpecForward.fs" => LitTextureNormalSpecFragForward(),
            "LitTexturedNormalAlphaForward.fs" => LitTextureNormalAlphaFragForward(),
            "LitTexturedNormalSpecAlphaForward.fs" => LitTextureNormalSpecAlphaFragForward(),
            "LitTexturedSpecForward.fs" => LitTextureSpecFragForward(),
            "LitTexturedAlphaForward.fs" => LitTextureAlphaFragForward(),
            "LitTexturedSpecAlphaForward.fs" => LitTextureSpecAlphaFragForward(),
            "LitColoredForward.fs" => LitColorFragForward(),
            "UnlitTexturedForward.fs" => UnlitTextureFragForward(),
            "UnlitTexturedStereoForward.fs" => UnlitTextureStereoFragForward(),
            "UnlitAlphaTexturedForward.fs" => UnlitAlphaTextureFragForward(),
            "UnlitColoredForward.fs" => UnlitColorFragForward(),
            "UberShader.frag" => UberFragForward(),
            "DeferredDecal.fs" => XRShader.EngineShader(Path.Combine("Scene3D", "DeferredDecal.fs"), EShaderType.Fragment),
            _ => null,
        };

    private static XRShader? LoadDeferredShaderByFileName(string fileName)
        => fileName switch
        {
            "TexturedDeferred.fs" => LitTextureFragDeferred(),
            "TexturedSilhouettePOMDeferred.fs" => LitTextureSilhouettePOMFragDeferred(),
            "ColoredDeferred.fs" => LitColorFragDeferred(),
            "TexturedNormalDeferred.fs" => LitTextureNormalFragDeferred(),
            "TexturedNormalSpecDeferred.fs" => LitTextureNormalSpecFragDeferred(),
            "TexturedNormalAlphaDeferred.fs" => LitTextureNormalAlphaFragDeferred(),
            "TexturedNormalSpecAlphaDeferred.fs" => LitTextureNormalSpecAlphaFragDeferred(),
            "TexturedNormalMetallicDeferred.fs" => LitTextureNormalMetallicFragDeferred(),
            "TexturedNormalMetallicRoughnessDeferred.fs" => LitTextureNormalRoughnessMetallicDeferred(),
            "TexturedSpecDeferred.fs" => LitTextureSpecFragDeferred(),
            "TexturedAlphaDeferred.fs" => LitTextureAlphaFragDeferred(),
            "TexturedSpecAlphaDeferred.fs" => LitTextureSpecAlphaFragDeferred(),
            "TexturedMetallicDeferred.fs" => LitTextureMetallicFragDeferred(),
            "TexturedMetallicRoughnessDeferred.fs" => LitTextureMetallicRoughnessDeferred(),
            "TexturedRoughnessDeferred.fs" => LitTextureRoughnessFragDeferred(),
            "TexturedMatcapDeferred.fs" => LitTextureMatcapDeferred(),
            "TexturedEmissiveDeferred.fs" => LitTextureEmissiveDeferred(),
            _ => null,
        };

    private static bool TryGetStandardForwardFileName(string fileName, out string standardFileName)
    {
        standardFileName = fileName switch
        {
            "DeferredDecalForwardWeightedOit.fs" => "DeferredDecal.fs",
            _ => string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(standardFileName))
            return true;

        const string weightedSuffix = "ForwardWeightedOit.fs";
        const string ppllSuffix = "ForwardPpll.fs";
        const string depthPeelSuffix = "ForwardDepthPeel.fs";

        if (fileName.EndsWith(weightedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            standardFileName = fileName[..^weightedSuffix.Length] + "Forward.fs";
            return true;
        }

        if (fileName.EndsWith(ppllSuffix, StringComparison.OrdinalIgnoreCase))
        {
            standardFileName = fileName[..^ppllSuffix.Length] + "Forward.fs";
            return true;
        }

        if (fileName.EndsWith(depthPeelSuffix, StringComparison.OrdinalIgnoreCase))
        {
            standardFileName = fileName[..^depthPeelSuffix.Length] + "Forward.fs";
            return true;
        }

        standardFileName = string.Empty;
        return false;
    }

    private static bool TryGetDeferredFileName(string fileName, out string deferredFileName)
    {
        deferredFileName = fileName switch
        {
            "TexturedDeferred.fs" => "TexturedDeferred.fs",
            "TexturedSilhouettePOMDeferred.fs" => "TexturedSilhouettePOMDeferred.fs",
            "ColoredDeferred.fs" => "ColoredDeferred.fs",
            "TexturedNormalDeferred.fs" => "TexturedNormalDeferred.fs",
            "TexturedNormalMetallicDeferred.fs" => "TexturedNormalMetallicDeferred.fs",
            "TexturedNormalMetallicRoughnessDeferred.fs" => "TexturedNormalMetallicRoughnessDeferred.fs",
            "TexturedMetallicDeferred.fs" => "TexturedMetallicDeferred.fs",
            "TexturedMetallicRoughnessDeferred.fs" => "TexturedMetallicRoughnessDeferred.fs",
            "TexturedRoughnessDeferred.fs" => "TexturedRoughnessDeferred.fs",
            "TexturedMatcapDeferred.fs" => "TexturedMatcapDeferred.fs",
            "TexturedEmissiveDeferred.fs" => "TexturedEmissiveDeferred.fs",
            "LitTexturedForward.fs" => "TexturedDeferred.fs",
            "LitTexturedSilhouettePOMForward.fs" => "TexturedSilhouettePOMDeferred.fs",
            "LitColoredForward.fs" => "ColoredDeferred.fs",
            "LitTexturedNormalForward.fs" => "TexturedNormalDeferred.fs",
            _ => string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(deferredFileName))
            return true;

        if (TryGetStandardForwardFileName(fileName, out string standardFileName))
            return TryGetDeferredFileName(standardFileName, out deferredFileName);

        deferredFileName = string.Empty;
        return false;
    }

    public static XRShader? GetWeightedBlendedOitForwardVariant(XRShader? shader)
    {
        XRShader? sourceShader = GetStandardForwardVariant(shader) ?? GetForwardVariantOfDeferredShader(shader) ?? shader;

        string? path = sourceShader?.Source?.FilePath ?? sourceShader?.FilePath;
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
            "UberShader.frag" => CreateDefinedShaderVariant(sourceShader, WeightedBlendedOitDefine),
            "DeferredDecal.fs" => DeferredDecalForwardWeightedOit(),
            // Deferred → lit forward WBOIT variants
            "TexturedDeferred.fs" => LitTextureFragForwardWeightedOit(),
            "TexturedNormalDeferred.fs" => LitTextureNormalFragForwardWeightedOit(),
            "ColoredDeferred.fs" => LitColorFragForwardWeightedOit(),
            "TexturedSilhouettePOMDeferred.fs" => LitTextureSilhouettePOMFragForwardWeightedOit(),
            _ => null,
        };
    }

    public static XRShader? GetPerPixelLinkedListForwardVariant(XRShader? shader)
    {
        XRShader? sourceShader = GetStandardForwardVariant(shader) ?? GetForwardVariantOfDeferredShader(shader) ?? shader;

        string? path = sourceShader?.Source?.FilePath ?? sourceShader?.FilePath;
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
            "UberShader.frag" => CreateDefinedShaderVariant(sourceShader, PerPixelLinkedListDefine),
            // Deferred → lit forward PPLL variants
            "TexturedDeferred.fs" => LitTextureFragForwardPerPixelLinkedList(),
            "TexturedNormalDeferred.fs" => LitTextureNormalFragForwardPerPixelLinkedList(),
            "ColoredDeferred.fs" => LitColorFragForwardPerPixelLinkedList(),
            "TexturedSilhouettePOMDeferred.fs" => LitTextureSilhouettePOMFragForwardPerPixelLinkedList(),
            _ => null,
        };
    }

    public static XRShader? GetDepthPeelingForwardVariant(XRShader? shader)
    {
        XRShader? sourceShader = GetStandardForwardVariant(shader) ?? GetForwardVariantOfDeferredShader(shader) ?? shader;

        string? path = sourceShader?.Source?.FilePath ?? sourceShader?.FilePath;
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
            "UberShader.frag" => CreateDefinedShaderVariant(sourceShader, DepthPeelingDefine),
            // Deferred → lit forward depth-peeling variants
            "TexturedDeferred.fs" => LitTextureFragForwardDepthPeeling(),
            "TexturedNormalDeferred.fs" => LitTextureNormalFragForwardDepthPeeling(),
            "ColoredDeferred.fs" => LitColorFragForwardDepthPeeling(),
            "TexturedSilhouettePOMDeferred.fs" => LitTextureSilhouettePOMFragForwardDepthPeeling(),
            _ => null,
        };
    }

    /// <summary>
    /// Returns the closest lit forward shader for a deferred shader, or null if the shader is not a known deferred shader.
    /// </summary>
    public static XRShader? GetForwardVariantOfDeferredShader(XRShader? shader)
    {
        string? path = shader?.Source?.FilePath ?? shader?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return fileName switch
        {
            "TexturedDeferred.fs" => LitTextureFragForward(),
            "TexturedNormalDeferred.fs" => LitTextureNormalFragForward(),
            "ColoredDeferred.fs" => LitColorFragForward(),
            "TexturedSilhouettePOMDeferred.fs" => LitTextureSilhouettePOMFragForward(),
            "TexturedNormalMetallicDeferred.fs" => LitTextureNormalFragForward(),
            "TexturedNormalMetallicRoughnessDeferred.fs" => LitTextureNormalFragForward(),
            "TexturedMetallicDeferred.fs" => LitTextureFragForward(),
            "TexturedMetallicRoughnessDeferred.fs" => LitTextureFragForward(),
            "TexturedRoughnessDeferred.fs" => LitTextureFragForward(),
            "TexturedEmissiveDeferred.fs" => LitTextureFragForward(),
            "TexturedMatcapDeferred.fs" => LitTextureFragForward(),
            _ => null,
        };
    }

    /// <summary>
    /// Returns true if the shader is a known deferred GBuffer fragment shader.
    /// </summary>
    public static bool IsDeferredShader(XRShader? shader)
    {
        string? path = shader?.Source?.FilePath ?? shader?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string fileName = Path.GetFileName(path);
        return fileName.EndsWith("Deferred.fs", System.StringComparison.OrdinalIgnoreCase);
    }

    public static XRShader? GetDeferredVariantOfShader(XRShader? shader)
    {
        string? path = shader?.Source?.FilePath ?? shader?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return TryGetDeferredFileName(fileName, out string deferredFileName)
            ? LoadDeferredShaderByFileName(deferredFileName)
            : null;
    }

    public static XRShader? GetStandardForwardVariant(XRShader? shader)
    {
        string? path = shader?.Source?.FilePath ?? shader?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        if (fileName.Equals("UberShader.frag", StringComparison.OrdinalIgnoreCase) && HasTransparencyForwardVariantDefine(shader))
            return HasDefine(shader, UberImportMaterialDefine) ? UberImportFragForward() : UberFragForward();

        if (TryGetStandardForwardFileName(fileName, out string standardFileName))
            return LoadStandardForwardShaderByFileName(standardFileName);

        if (HasTransparencyForwardVariantDefine(shader) && DefineBasedTransparencyForwardShaderFiles.Contains(fileName))
            return LoadStandardForwardShaderByFileName(fileName);

        return null;
    }

    public static XRShader? GetDepthNormalPrePassForwardVariant(XRShader? shader)
    {
        if (HasTransparencyForwardVariantDefine(shader))
            return null;

        string? path = shader?.Source?.FilePath ?? shader?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return fileName switch
        {
            "LitTexturedForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedAlphaForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitColoredForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedNormalForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedNormalSpecForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedNormalAlphaForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedNormalSpecAlphaForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedSpecForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedSpecAlphaForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "LitTexturedSilhouettePOMForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "UnlitTexturedForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "UnlitTexturedStereoForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "UnlitTexturedArraySliceForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "UnlitAlphaTexturedForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "UnlitColoredForward.fs" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            "UberShader.frag" => CreateDefinedShaderVariant(shader, DepthNormalPrePassDefine),
            _ => null,
        };
    }

    public static XRShader? GetShadowCasterForwardVariant(XRShader? shader)
    {
        XRShader? sourceShader = GetStandardForwardVariant(shader) ?? shader;

        string? path = sourceShader?.Source?.FilePath ?? sourceShader?.FilePath;
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
            "UberShader.frag" => CreateDefinedShaderVariant(sourceShader, ShadowCasterPassDefine),
            _ => null,
        };
    }

    public static XRShader? GetPointShadowCasterForwardVariant(XRShader? shader)
    {
        XRShader? sourceShader = GetStandardForwardVariant(shader) ?? shader;

        string? path = sourceShader?.Source?.FilePath ?? sourceShader?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path);
        return fileName switch
        {
            "LitTexturedAlphaForward.fs" => CreateDefinedShaderVariant(sourceShader, PointShadowCasterPassDefine),
            "LitTexturedSpecAlphaForward.fs" => CreateDefinedShaderVariant(sourceShader, PointShadowCasterPassDefine),
            "LitTexturedNormalAlphaForward.fs" => CreateDefinedShaderVariant(sourceShader, PointShadowCasterPassDefine),
            "LitTexturedNormalSpecAlphaForward.fs" => CreateDefinedShaderVariant(sourceShader, PointShadowCasterPassDefine),
            "UnlitAlphaTexturedForward.fs" => CreateDefinedShaderVariant(sourceShader, PointShadowCasterPassDefine),
            "UberShader.frag" => CreateDefinedShaderVariant(sourceShader, PointShadowCasterPassDefine),
            _ => null,
        };
    }

    /// <summary>
    /// Creates a shader variant with a preprocessor define injected after the #version directive.
    /// Used to create compile-time variants of the same shader source (e.g., MSAA deferred, shadow caster).
    /// </summary>
    public static XRShader? CreateDefinedShaderVariant(XRShader? shader, string defineName)
    {
        if (shader?.Source?.Text is not { Length: > 0 } source)
            return null;

        DefinedVariantShaderCacheKey cacheKey = new(
            defineName,
            shader.Type,
            source,
            shader.Source?.FilePath,
            shader.Source?.Name);

        return DefinedVariantShaderCache.GetOrAdd(
            cacheKey,
            _ =>
            {
                string variantSource = GetOrCreateDefinedVariantSource(source, defineName);
                TextFile variantText = TextFile.FromText(variantSource);
                variantText.FilePath = shader.Source?.FilePath;
                variantText.Name = shader.Source?.Name;
                return new XRShader(shader.Type, variantText)
                {
                    Name = shader.Name,
                    GenerateAsync = shader.GenerateAsync,
                };
            });
    }

    private static string GetOrCreateDefinedVariantSource(string source, string defineName)
    {
        ulong sourceHash = ComputeDefinedVariantSourceHash(source, defineName);
        ConcurrentDictionary<DefinedVariantCacheKey, string> bucket = DefinedVariantSourceCache.GetOrAdd(
            sourceHash,
            static _ => new());

        return bucket.GetOrAdd(
            new(defineName, source),
            static key => InjectDefineAfterVersion(key.SourceText, key.DefineName));
    }

    private static string InjectDefineAfterVersion(string source, string defineName)
    {
        int searchIndex = 0;
        while (searchIndex < source.Length)
        {
            int lineEnd = source.IndexOf('\n', searchIndex);
            int lineLength = (lineEnd >= 0 ? lineEnd : source.Length) - searchIndex;
            string line = source.Substring(searchIndex, lineLength);
            if (line.TrimStart(' ', '\t', '\r').StartsWith("#version", StringComparison.Ordinal))
            {
                int insertionIndex = lineEnd >= 0 ? lineEnd + 1 : source.Length;
                string header = source[..insertionIndex];
                string body = insertionIndex < source.Length
                    ? source[insertionIndex..].TrimStart('\r', '\n')
                    : string.Empty;

                return string.IsNullOrEmpty(body)
                    ? header + Environment.NewLine + $"#define {defineName}" + Environment.NewLine
                    : header + Environment.NewLine + $"#define {defineName}" + Environment.NewLine + Environment.NewLine + body;
            }

            if (lineEnd < 0)
                break;

            searchIndex = lineEnd + 1;
        }

        return $"#define {defineName}{Environment.NewLine}{source}";
    }

    private static ulong ComputeDefinedVariantSourceHash(string source, string defineName)
    {
        const ulong fnvOffset = 14695981039346656037ul;
        const ulong fnvPrime = 1099511628211ul;

        ulong hash = fnvOffset;
        for (int i = 0; i < defineName.Length; i++)
        {
            hash ^= defineName[i];
            hash *= fnvPrime;
        }

        hash ^= 0xffu;
        hash *= fnvPrime;

        for (int i = 0; i < source.Length; i++)
        {
            hash ^= source[i];
            hash *= fnvPrime;
        }

        hash ^= (ulong)source.Length;
        hash *= fnvPrime;
        return hash;
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

    /// <summary>
    /// Fragment shader source that outputs normalized linear camera depth.
    /// Use with shadow atlas color targets so receiver shaders can do manual compares.
    /// </summary>
    public static readonly string Frag_LinearDepthOutput = @"#version 450
layout(location = 0) out float Depth;
uniform float CameraNearZ;
uniform float CameraFarZ;

float LinearizeDepth(float depth, float nearZ, float farZ)
{
    float z = depth * 2.0 - 1.0;
    return (2.0 * nearZ * farZ) / (farZ + nearZ - z * (farZ - nearZ));
}

void main()
{
    float nearZ = max(CameraNearZ, 0.0001);
    float farZ = max(CameraFarZ, nearZ + 0.0001);
    float linearZ = LinearizeDepth(gl_FragCoord.z, nearZ, farZ);
    Depth = clamp((linearZ - nearZ) / (farZ - nearZ), 0.0, 1.0);
}";

    /// <summary>
    /// Fragment shader source that writes VSM/EVSM moments for spot shadow maps.
    /// </summary>
    public static readonly string Frag_ShadowMomentOutput = @"#version 450
layout(location = 0) out vec4 ShadowMoments;

uniform int ShadowMapEncoding = 1;
uniform float ShadowMomentPositiveExponent = 5.0;
uniform float ShadowMomentNegativeExponent = 5.0;
uniform float CameraNearZ = 0.1;
uniform float CameraFarZ = 1000.0;

#define XRENGINE_SHADOW_ENCODING_DEPTH 0
#define XRENGINE_SHADOW_ENCODING_VARIANCE2 1
#define XRENGINE_SHADOW_ENCODING_EVSM2 2
#define XRENGINE_SHADOW_ENCODING_EVSM4 3

float LinearizeShadowDepth01(float depth, float nearZ, float farZ)
{
    float n = max(nearZ, 0.0001);
    float f = max(farZ, n + 0.0001);
    float z = depth * 2.0 - 1.0;
    float linearZ = (2.0 * n * f) / (f + n - z * (f - n));
    return clamp((linearZ - n) / (f - n), 0.0, 1.0);
}

vec2 EncodeVsmMoments(float depth)
{
    float clampedDepth = clamp(depth, 0.0, 1.0);
    float dx = dFdx(clampedDepth);
    float dy = dFdy(clampedDepth);
    return vec2(clampedDepth, clampedDepth * clampedDepth + 0.25 * (dx * dx + dy * dy));
}

vec2 EncodeEvsm2Moments(float depth, float positiveExponent)
{
    float warpedDepth = exp(max(positiveExponent, 0.0) * clamp(depth, 0.0, 1.0));
    float dx = dFdx(warpedDepth);
    float dy = dFdy(warpedDepth);
    return vec2(warpedDepth, warpedDepth * warpedDepth + 0.25 * (dx * dx + dy * dy));
}

vec4 EncodeEvsm4Moments(float depth, float positiveExponent, float negativeExponent)
{
    float clampedDepth = clamp(depth, 0.0, 1.0);
    float positive = exp(max(positiveExponent, 0.0) * clampedDepth);
    float negative = -exp(-max(negativeExponent, 0.0) * clampedDepth);
    float positiveDx = dFdx(positive);
    float positiveDy = dFdy(positive);
    float negativeDx = dFdx(negative);
    float negativeDy = dFdy(negative);
    return vec4(
        positive,
        positive * positive + 0.25 * (positiveDx * positiveDx + positiveDy * positiveDy),
        negative,
        negative * negative + 0.25 * (negativeDx * negativeDx + negativeDy * negativeDy));
}

void main()
{
    float depth = LinearizeShadowDepth01(gl_FragCoord.z, CameraNearZ, CameraFarZ);
    if (ShadowMapEncoding == XRENGINE_SHADOW_ENCODING_EVSM4)
        ShadowMoments = EncodeEvsm4Moments(depth, ShadowMomentPositiveExponent, ShadowMomentNegativeExponent);
    else if (ShadowMapEncoding == XRENGINE_SHADOW_ENCODING_EVSM2)
        ShadowMoments = vec4(EncodeEvsm2Moments(depth, ShadowMomentPositiveExponent), 0.0, 0.0);
    else if (ShadowMapEncoding == XRENGINE_SHADOW_ENCODING_VARIANCE2)
        ShadowMoments = vec4(EncodeVsmMoments(depth), 0.0, 0.0);
    else
        ShadowMoments = vec4(gl_FragCoord.z, 0.0, 0.0, 0.0);
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
