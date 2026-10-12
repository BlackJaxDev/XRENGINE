using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Core.Files;
using XREngine.Imaging;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Runtime.Imaging.Magick;
using XREngine.Runtime.Text.FreeType;
using XREngine.Scene;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
{
    private const string BrowserDefaultUiFontPath = "/engine/Fonts/Roboto/Roboto-Regular.cooked.asset";
    private static readonly UTF8Encoding BrowserNoticeEncoding = new(false, true);

    private sealed record BrowserUiFontCookRequest(string SourcePath, string CatalogPath, string SourceName,
        List<string> Characters, Dictionary<string, FontGlyphSet.Glyph> LayoutGlyphs,
        float LayoutEmSize, string SourceHash,
        string NoticePath, string NoticeHash, string NoticeOutputName);

    /// <summary>Replaces desktop font objects with portable identities before cooking the world.</summary>
    private static bool PrepareBrowserUiFonts(IEnumerable<XRScene> scenes, string assetRoot,
        out IReadOnlyList<BrowserUiFontCookRequest> authoredFonts)
    {
        bool requiresDefault = false;
        Dictionary<string, BrowserUiFontCookRequest> selected = new(StringComparer.Ordinal);
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string configuredDefault = Path.GetFullPath(Engine.Assets.ResolveEngineAssetPath("Fonts",
            RuntimeEngine.Rendering.Settings.DefaultFontFolder,
            RuntimeEngine.Rendering.Settings.DefaultFontFileName));
        string canonicalDefault = Path.GetFullPath(Engine.Assets.ResolveEngineAssetPath("Fonts", "Roboto", "Roboto-Regular.ttf"));
        foreach (XRScene scene in scenes)
            foreach (SceneNode root in scene.RootNodes)
                Visit(root, 0);
        authoredFonts = selected.Values.OrderBy(static font => font.CatalogPath, StringComparer.Ordinal).ToArray();
        return requiresDefault;

        void Visit(SceneNode node, int depth)
        {
            if (depth >= 128 || !visited.Add(node) || visited.Count > 8192)
                throw new NotSupportedException("BrowserCook.SceneGraphUnsupported: font discovery exceeds the hierarchy budget or shares a scene node.");
            foreach (var component in node.Components)
            {
                if (component is not UITextComponent text)
                    continue;
                string nodePath = node.GetPath();
                using (XRBase.SuppressPropertyNotifications())
                    text.PublishedFontAssetPath = null;
                if (text.Font is not { } loadedFont)
                {
                    if (!string.Equals(configuredDefault, canonicalDefault, comparison))
                        throw new NotSupportedException($"BrowserCook.DefaultFontUnsupported: '{nodePath}' selects a noncanonical default font; the browser default bitmap profile cooks Roboto-Regular.ttf only.");
                    requiresDefault = true;
                    continue;
                }
                if (loadedFont.AtlasType != EFontAtlasType.Bitmap || !float.IsFinite(loadedFont.LayoutEmSize)
                    || loadedFont.LayoutEmSize is < 1 or > 512 || loadedFont.Atlas is not {
                        SizedInternalFormat: ESizedInternalFormat.R8, AutoGenerateMipmaps: false,
                        MinFilter: ETexMinFilter.LinearMipmapLinear, MagFilter: ETexMagFilter.Linear,
                        UWrap: ETexWrapMode.ClampToEdge, VWrap: ETexWrapMode.ClampToEdge })
                    throw new NotSupportedException($"BrowserCook.FontProfileUnsupported: '{nodePath}' requires a bitmap R8 font atlas with a 1–512-pixel layout em.");
                string? declaredPath = loadedFont.OriginalPath ?? loadedFont.FilePath;
                if (string.IsNullOrWhiteSpace(declaredPath))
                    throw new NotSupportedException($"BrowserCook.FontSourceMissing: '{nodePath}' has no source .ttf or .otf identity.");
                string sourcePath = Path.GetFullPath(declaredPath);
                if (string.Equals(sourcePath, canonicalDefault, comparison))
                {
                    if (!string.Equals(configuredDefault, canonicalDefault, comparison) || loadedFont.LayoutEmSize != 128.0f)
                        throw new NotSupportedException($"BrowserCook.DefaultFontUnsupported: '{nodePath}' selects a noncanonical default font profile.");
                    requiresDefault = true;
                }
                else
                {
                    BrowserUiFontCookRequest request = DescribeAuthoredFont(sourcePath, loadedFont, assetRoot, nodePath);
                    selected.TryAdd(request.CatalogPath, request);
                    using (XRBase.SuppressPropertyNotifications())
                        text.PublishedFontAssetPath = request.CatalogPath;
                }
                // Desktop imports never enter the browser world; the publisher owns the cooked reference.
                using (XRBase.SuppressPropertyNotifications())
                    text.Font = null;
            }
            foreach (var child in node.Transform.Children)
                if (child.SceneNode is SceneNode childNode)
                    Visit(childNode, depth + 1);
        }
    }

    private static BrowserUiFontCookRequest DescribeAuthoredFont(string sourcePath, FontGlyphSet loadedFont,
        string assetRoot, string nodePath)
    {
        if (Path.GetExtension(sourcePath).ToLowerInvariant() is not (".ttf" or ".otf")
            || !AssetReferencePath.TryCreate(assetRoot, sourcePath, AssetReferencePath.GamePrefix, out string? reference))
            throw new NotSupportedException($"BrowserCook.FontSourceUnsupported: '{nodePath}' requires a project Assets .ttf or .otf source.");
        RequireBrowserRegularFile(sourcePath, assetRoot, 16 * 1024 * 1024, "FontSource");
        if (loadedFont.Characters is not { Count: >= 1 and <= 65536 } characters ||
            characters.Distinct(StringComparer.Ordinal).Count() != characters.Count ||
            characters.Any(static character => string.IsNullOrEmpty(character)
                || !Rune.TryGetRuneAt(character, 0, out Rune rune)
                || rune.Utf16SequenceLength != character.Length || rune.Value < 0x20))
            throw new NotSupportedException($"BrowserCook.FontRepertoireUnsupported: '{nodePath}' requires 1–65536 distinct Unicode-scalar glyph keys.");
        if (loadedFont.Glyphs is not { } authoredGlyphs || authoredGlyphs.Count != characters.Count)
            throw new NotSupportedException($"BrowserCook.FontMetricsUnsupported: '{nodePath}' lacks the selected authored glyph metrics.");
        XRFontImportOptions options = Engine.Assets.GetOrCreateThirdPartyImportOptions(sourcePath, typeof(FontGlyphSet))
            as XRFontImportOptions ?? new XRFontImportOptions();
        string? noticeRelative = options.BrowserLicenseNoticePath;
        if (string.IsNullOrWhiteSpace(noticeRelative) || Path.IsPathRooted(noticeRelative)
            || AssetReferencePath.IsPortable(noticeRelative) || noticeRelative.Length > 512 ||
            !AssetReferencePath.TryResolve(AssetReferencePath.GamePrefix + noticeRelative,
                assetRoot, null, out string? noticePath))
            throw new NotSupportedException($"BrowserCook.FontNoticeMissing: '{nodePath}' requires BrowserLicenseNoticePath in its font import options, relative to project Assets.");
        RequireBrowserRegularFile(noticePath, assetRoot, 256 * 1024, "FontNotice");
        byte[] noticeBytes = File.ReadAllBytes(noticePath);
        if (string.IsNullOrWhiteSpace(BrowserNoticeEncoding.GetString(noticeBytes)) || Array.IndexOf(noticeBytes, (byte)0) >= 0)
            throw new InvalidDataException($"BrowserCook.FontNoticeInvalid: '{nodePath}' has an empty or invalid UTF-8 notice.");
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath)));
        List<string> selectedCharacters = [.. characters.Order(StringComparer.Ordinal)];
        Dictionary<string, FontGlyphSet.Glyph> layoutGlyphs = new(selectedCharacters.Count, StringComparer.Ordinal);
        StringBuilder descriptor = new StringBuilder(reference).Append('\n').Append(sourceHash).Append('\n')
            .Append(loadedFont.LayoutEmSize.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        foreach (string character in selectedCharacters)
        {
            if (!authoredGlyphs.TryGetValue(character, out FontGlyphSet.Glyph? glyph) || glyph is null ||
                !float.IsFinite(glyph.Size.X) || !float.IsFinite(glyph.Size.Y) ||
                !float.IsFinite(glyph.Bearing.X) || !float.IsFinite(glyph.Bearing.Y) ||
                !float.IsFinite(glyph.AdvanceX))
                throw new NotSupportedException($"BrowserCook.FontMetricsUnsupported: '{nodePath}' has an invalid authored glyph metric.");
            layoutGlyphs.Add(character, new FontGlyphSet.Glyph
            {
                Size = glyph.Size, Bearing = glyph.Bearing, AdvanceX = glyph.AdvanceX,
            });
            descriptor.Append(character).Append(':')
                .Append(glyph.Size.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(glyph.Size.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(glyph.Bearing.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(glyph.Bearing.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(glyph.AdvanceX.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.ToString())));
        if (File.Exists(Path.Combine(assetRoot, "Fonts", "Cooked", identity + ".cooked.asset")))
            throw new InvalidDataException("BrowserCook.UiFontIdentityConflict: generated identity collides with a project asset.");
        return new BrowserUiFontCookRequest(sourcePath, $"/game/Fonts/Cooked/{identity}.cooked.asset",
            $"font-{identity}.bin", selectedCharacters, layoutGlyphs, loadedFont.LayoutEmSize, sourceHash,
            noticePath, Convert.ToHexStringLower(SHA256.HashData(noticeBytes)), $"font-{identity}-NOTICE.txt");
    }

    private static void RequireBrowserRegularFile(string path, string root, long limit, string kind)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullPath = Path.GetFullPath(path);
        if (!AssetReferencePath.TryCreate(fullRoot, fullPath, AssetReferencePath.GamePrefix, out _))
            throw new NotSupportedException($"BrowserCook.{kind}OutsideAssets: source must stay under project Assets.");
        for (string? current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException($"BrowserCook.{kind}Linked: linked source files and directories are unsupported.");
            if (string.Equals(current, fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;
        }
        FileInfo source = new(fullPath);
        if (!source.Exists || source.Length is < 1 || source.Length > limit)
            throw new InvalidDataException($"BrowserCook.{kind}BudgetExceeded: source is missing, empty, or exceeds its size budget.");
    }

    private static string CookBrowserUiFont(BrowserUiFontCookRequest request, string assetRoot, string sourceDirectory,
        CancellationToken cancellationToken)
    {
        RequireBrowserRegularFile(request.SourcePath, assetRoot, 16 * 1024 * 1024, "FontSource");
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(request.SourcePath))) != request.SourceHash)
            throw new InvalidDataException("BrowserCook.FontSourceChanged: source changed during publication.");
        string cooked = CookBitmapFont(request.SourcePath, request.Characters, request.LayoutEmSize,
            Path.Combine(sourceDirectory, request.SourceName), cancellationToken, request.LayoutGlyphs);
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(request.SourcePath))) != request.SourceHash)
            throw new InvalidDataException("BrowserCook.FontSourceChanged: source changed during publication.");
        return cooked;
    }

    private static void VerifyPublishedUiFontReferences(IEnumerable<XRScene> authoredScenes, IEnumerable<XRScene> cookedScenes)
    {
        static List<string?> Collect(IEnumerable<XRScene> scenes)
        {
            List<string?> references = [];
            HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
            foreach (XRScene scene in scenes)
                foreach (SceneNode root in scene.RootNodes)
                    Visit(root);
            return references;

            void Visit(SceneNode node)
            {
                if (!visited.Add(node))
                    throw new InvalidDataException("BrowserCook.UiFontSceneGraphInvalid: shared node.");
                foreach (var component in node.Components)
                    if (component is UITextComponent text)
                        references.Add(text.PublishedFontAssetPath);
                foreach (var child in node.Transform.Children)
                    if (child.SceneNode is SceneNode childNode)
                        Visit(childNode);
            }
        }
        if (!Collect(authoredScenes).SequenceEqual(Collect(cookedScenes), StringComparer.Ordinal))
            throw new InvalidDataException("BrowserCook.UiFontReferenceLost: the cooked world did not retain its authored font identities.");
    }

    /// <summary>Cooks the canonical default bitmap font into one portable, atlas-owning asset.</summary>
    private static string CookBrowserDefaultUiFont(string sourceDirectory, CancellationToken cancellationToken)
    {
        string sourceFont = Engine.Assets.ResolveEngineAssetPath("Fonts", "Roboto", "Roboto-Regular.ttf");
        if (!File.Exists(sourceFont))
            throw new FileNotFoundException("BrowserCook.DefaultUiFontSourceMissing: canonical Roboto source is unavailable.", sourceFont);
        List<string> characters = new FreeTypeFontCharacterEnumerator().GetSupportedCharacters(sourceFont)
            .Where(static codepoint => codepoint is >= 0x20 and <= 0x10FFFF)
            .OrderBy(static codepoint => codepoint)
            .Select(static codepoint => char.ConvertFromUtf32((int)codepoint))
            .ToList();
        return CookBitmapFont(sourceFont, characters, 128.0f,
            Path.Combine(sourceDirectory, "default-ui-font.bin"), cancellationToken);
    }

    private static string CookBitmapFont(string sourceFont, List<string> characters, float layoutEm,
        string payloadPath, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, FontGlyphSet.Glyph>? layoutGlyphs = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IFontBitmapRasterizer? previousRasterizer = FontBitmapRasterizerRegistry.Current;
        IRuntimeImageCodec? previousImageCodec = RuntimeImageCodecs.Current;
        try
        {
            FreeTypeFontBackend.RegisterBitmapRasterizerForCooking(cancellationToken);
            RuntimeImageCodecs.Current ??= new MagickRuntimeImageCodec();
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            FontGlyphSet font = new() { Characters = characters };
            font.GenerateBitmapFontAtlas(sourceFont, characters, payloadPath + ".png", layoutEm);
            if (font.Glyphs?.Count != characters.Count)
                throw new InvalidDataException("BrowserCook.UiFontRepertoireChanged: the source font lacks a selected glyph.");
            if (layoutGlyphs is not null)
            {
                foreach ((string character, FontGlyphSet.Glyph layout) in layoutGlyphs)
                {
                    FontGlyphSet.Glyph glyph = font.Glyphs[character];
                    glyph.Size = layout.Size;
                    glyph.Bearing = layout.Bearing;
                    glyph.AdvanceX = layout.AdvanceX;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            using IDisposable fontCodec = RenderingPublishedCookedAssetRegistration.InstallBrowserBitmapFontCodec();
            WriteCookedAsset(font, payloadPath);
            if (new FileInfo(payloadPath).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("BrowserCook.UiFontPayloadBudgetExceeded: cooked font exceeds the browser read limit.");
            return payloadPath;
        }
        finally
        {
            FontBitmapRasterizerRegistry.Current = previousRasterizer;
            RuntimeImageCodecs.Current = previousImageCodec;
        }
    }
}
