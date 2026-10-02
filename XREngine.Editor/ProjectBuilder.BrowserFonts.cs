using XREngine.Data.Core;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
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

    /// <summary>Declares the font for authored UI text and removes editor-only default imports.</summary>
    private static bool RequiresBrowserDefaultUiFont(XRWorld world)
    {
        bool requiresFont = false;
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string configuredDefault = Path.GetFullPath(Engine.Assets.ResolveEngineAssetPath("Fonts",
            RuntimeEngine.Rendering.Settings.DefaultFontFolder,
            RuntimeEngine.Rendering.Settings.DefaultFontFileName));
        string canonicalDefault = Path.GetFullPath(Engine.Assets.ResolveEngineAssetPath("Fonts", "Roboto", "Roboto-Regular.ttf"));
        foreach (XRScene scene in world.Scenes)
            foreach (SceneNode root in scene.RootNodes)
                Visit(root, 0);
        return requiresFont;

        void Visit(SceneNode node, int depth)
        {
            if (depth >= 128 || !visited.Add(node) || visited.Count > 8192)
                throw new NotSupportedException("BrowserCook.SceneGraphUnsupported: font discovery exceeds the hierarchy budget or shares a scene node.");
            foreach (var component in node.Components)
            {
                if (component is not UITextComponent text)
                    continue;
                if (!string.Equals(configuredDefault, canonicalDefault, comparison))
                    throw new NotSupportedException($"BrowserCook.DefaultFontUnsupported: '{node.GetPath()}' selects a noncanonical default font; the bitmap profile currently cooks Roboto-Regular.ttf only.");
                requiresFont = true;
                if (text.Font is not { } loadedFont)
                    continue;
                if (loadedFont.AtlasType != EFontAtlasType.Bitmap || loadedFont.LayoutEmSize != 128.0f ||
                    loadedFont.Atlas is not { SizedInternalFormat: ESizedInternalFormat.R8,
                        AutoGenerateMipmaps: false, MinFilter: ETexMinFilter.LinearMipmapLinear,
                        MagFilter: ETexMagFilter.Linear, UWrap: ETexWrapMode.ClampToEdge,
                        VWrap: ETexWrapMode.ClampToEdge })
                    throw new NotSupportedException($"BrowserCook.FontProfileUnsupported: '{node.GetPath()}' requires a different authored font profile; this output cooks the default 128-pixel bitmap atlas only.");
                string? fontPath = loadedFont.OriginalPath ?? loadedFont.FilePath;
                if (fontPath is null ||
                    !string.Equals(Path.GetFullPath(fontPath), canonicalDefault, comparison))
                    throw new NotSupportedException($"BrowserCook.FontUnsupported: '{node.GetPath()}' requires a custom authored font; only the cooked default bitmap font is available.");
                // UITextComponent can import a desktop font while YAML is hydrated.
                // The browser must resolve that reference through its preloaded cooked atlas.
                using (XRBase.SuppressPropertyNotifications())
                    text.Font = null;
            }
            foreach (var child in node.Transform.Children)
                if (child.SceneNode is SceneNode childNode)
                    Visit(childNode, depth + 1);
        }
    }

    /// <summary>Cooks the canonical default bitmap font into one portable, atlas-owning asset.</summary>
    private static string CookBrowserDefaultUiFont(string sourceDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourceFont = Engine.Assets.ResolveEngineAssetPath("Fonts", "Roboto", "Roboto-Regular.ttf");
        if (!File.Exists(sourceFont))
            throw new FileNotFoundException("BrowserCook.DefaultUiFontSourceMissing: canonical Roboto source is unavailable.", sourceFont);

        IFontBitmapRasterizer? previousRasterizer = FontBitmapRasterizerRegistry.Current;
        IRuntimeImageCodec? previousImageCodec = RuntimeImageCodecs.Current;
        try
        {
            FreeTypeFontBackend.RegisterBitmapRasterizerForCooking();
            RuntimeImageCodecs.Current ??= new MagickRuntimeImageCodec();
            List<string> characters = new FreeTypeFontCharacterEnumerator().GetSupportedCharacters(sourceFont)
                .Where(static codepoint => codepoint is >= 0x20 and <= 0x10FFFF)
                .OrderBy(static codepoint => codepoint)
                .Select(static codepoint => char.ConvertFromUtf32((int)codepoint))
                .ToList();
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            FontGlyphSet font = new() { Characters = characters };
            font.GenerateBitmapFontAtlas(sourceFont, characters,
                Path.Combine(sourceDirectory, "default-ui-font-atlas.png"), 128.0f);
            cancellationToken.ThrowIfCancellationRequested();
            string payloadPath = Path.Combine(sourceDirectory, "default-ui-font.bin");
            using IDisposable fontCodec = RenderingPublishedCookedAssetRegistration.InstallBrowserBitmapFontCodec();
            WriteCookedAsset(font, payloadPath);
            if (new FileInfo(payloadPath).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("BrowserCook.DefaultUiFontPayloadBudgetExceeded: cooked font exceeds the browser read limit.");
            return payloadPath;
        }
        finally
        {
            FontBitmapRasterizerRegistry.Current = previousRasterizer;
            RuntimeImageCodecs.Current = previousImageCodec;
        }
    }
}
