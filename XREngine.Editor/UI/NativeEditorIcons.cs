using System.Numerics;
using SkiaSharp;
using Svg.Skia;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>
/// The editor SVG icons (<see cref="SvgEditorIcons"/>) for the native UI. On first use, this class rasterizes each
/// SVG file in the icon folder at <see cref="PixelSize"/>, packs the images into one atlas page, and creates one
/// texture. A native icon is a batched UI image quad that shows the atlas region of one icon, so all icons draw
/// from one texture.
/// </summary>
/// <remarks>
/// The ImGui editor rasterizes the same files for its toolbar. The atlas and its texture live for the process, so
/// a rebuilt panel reuses them. A missing or invalid SVG file has no region, and its icon quad stays clear, with a
/// warning in the log.
/// </remarks>
public static class NativeEditorIcons
{
    /// <summary>The raster size of one icon. It is twice the usual display size, so icons stay sharp at 2x scale.</summary>
    public const int PixelSize = 48;
    private const int PageSize = 512;

    private static readonly object Gate = new();
    private static UIImageAtlas? _atlas;
    private static XRTexture2D? _texture;
    private static HashSet<string>? _available;
    private static bool _loaded;

    /// <summary>
    /// Creates an icon quad in <paramref name="parent"/>: <paramref name="size"/> pixels square, centered at
    /// <paramref name="anchor"/> and moved by <paramref name="offset"/>. Returns the quad, which stays clear when
    /// the icon is missing.
    /// </summary>
    /// <param name="iconName">An icon file name from <see cref="SvgEditorIcons"/>, such as <see cref="SvgEditorIcons.IconPlay"/>.</param>
    /// <param name="renderPass">The pass of the image. Drop-down menus draw in their own later pass.</param>
    public static UIMaterialComponent CreateIcon(SceneNode parent, string iconName, Vector2 anchor, Vector2 offset,
        float size, ColorF4? tint = null, int renderPass = (int)EDefaultRenderPass.TransparentForward)
    {
        SceneNode node = parent.NewChild<UIMaterialComponent>(out var quad);
        node.Name = $"Icon {Path.GetFileNameWithoutExtension(iconName)}";
        UIBoundableTransform transform = node.GetTransformAs<UIBoundableTransform>(true)!;
        transform.MinAnchor = anchor;
        transform.MaxAnchor = anchor;
        transform.NormalizedPivot = new Vector2(0.5f, 0.5f);
        transform.Translation = offset;
        transform.Width = size;
        transform.Height = size;
        transform.BlocksInputBehind = false;
        quad.SetBatchSolidColor(ColorF4.Transparent);
        SetIcon(quad, iconName, tint, renderPass);
        return quad;
    }

    /// <summary>
    /// Shows icon <paramref name="iconName"/> on an existing quad. Returns false and leaves the quad unchanged when
    /// the icon is missing.
    /// </summary>
    public static bool SetIcon(UIMaterialComponent quad, string iconName, ColorF4? tint = null,
        int renderPass = (int)EDefaultRenderPass.TransparentForward)
    {
        if (!TryGetRegion(iconName, out XRTexture2D? texture, out Vector4 region))
            return false;
        ColorF4 color = tint ?? ColorF4.White;
        quad.SetBatchImage(texture!, new Vector4(color.R, color.G, color.B, color.A), region);
        // The image setter selects the default transparent pass, so another pass is set after it.
        if (quad.RenderPass != renderPass)
            quad.RenderPass = renderPass;
        return true;
    }

    /// <summary>Gets whether the icon folder has <paramref name="iconName"/>.</summary>
    public static bool HasIcon(string iconName)
    {
        EnsureLoaded();
        return _available?.Contains(iconName) == true;
    }

    private static bool TryGetRegion(string iconName, out XRTexture2D? texture, out Vector4 region)
    {
        EnsureLoaded();
        texture = _texture;
        region = default;
        if (texture is null || _atlas is null || _available?.Contains(iconName) != true)
            return false;
        region = _atlas.GetRegion(iconName);
        return true;
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (Gate)
        {
            if (_loaded)
                return;
            _loaded = true;
            try
            {
                Load();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, "Could not build the native editor icon atlas.");
            }
        }
    }

    private static void Load()
    {
        string? folder = ResolveIconFolder();
        if (folder is null)
        {
            Debug.LogWarning($"[NativeEditorIcons] The icon folder '{SvgEditorIcons.ICON_ROOT}' was not found.");
            return;
        }
        List<UIImageAtlasSource> sources = [];
        foreach (string path in Directory.EnumerateFiles(folder, "*.svg").OrderBy(static path => path, StringComparer.Ordinal))
        {
            if (TryRasterize(path, out byte[]? pixels))
                sources.Add(new UIImageAtlasSource(Path.GetFileName(path), PixelSize, PixelSize, pixels));
        }
        if (sources.Count == 0)
            return;
        _atlas = new UIImageAtlas(sources, PageSize, srgb: true);
        _texture = _atlas.CreateTexture();
        _texture.Name = "NativeEditorIcons";
        _available = [.. sources.Select(static source => source.Key)];
    }

    private static string? ResolveIconFolder()
    {
        string relative = SvgEditorIcons.ICON_ROOT;
        string?[] roots =
        [
            Engine.Assets?.EngineAssetsPath,
            Path.GetDirectoryName(typeof(NativeEditorIcons).Assembly.Location),
            Directory.GetCurrentDirectory(),
        ];
        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            string candidate = Path.Combine(root, relative);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Rasterizes one SVG file into tightly packed RGBA8 pixels with straight alpha, centered and scaled to fit
    /// <see cref="PixelSize"/>.
    /// </summary>
    private static bool TryRasterize(string path, out byte[]? pixels)
    {
        pixels = null;
        try
        {
            using SKSvg svg = new();
            svg.Load(path);
            if (svg.Picture is not { } picture)
                throw new InvalidDataException("The SVG parser produced no drawable picture.");
            SKRect cull = picture.CullRect;
            float scale = MathF.Min(cull.Width > 0 ? PixelSize / cull.Width : 1.0f,
                cull.Height > 0 ? PixelSize / cull.Height : 1.0f);
            using SKBitmap bitmap = new(PixelSize, PixelSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using (SKCanvas canvas = new(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                SKMatrix matrix = SKMatrix.CreateScale(scale, scale)
                    .PostConcat(SKMatrix.CreateTranslation(
                        (PixelSize - cull.Width * scale) * 0.5f - cull.Left * scale,
                        (PixelSize - cull.Height * scale) * 0.5f - cull.Top * scale));
                canvas.DrawPicture(picture, in matrix);
                canvas.Flush();
            }
            int rowBytes = PixelSize * 4;
            ReadOnlySpan<byte> source = bitmap.GetPixelSpan();
            pixels = new byte[rowBytes * PixelSize];
            for (int row = 0; row < PixelSize; row++)
                source.Slice(row * bitmap.RowBytes, rowBytes).CopyTo(pixels.AsSpan(row * rowBytes, rowBytes));
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[NativeEditorIcons] Could not rasterize '{Path.GetFileName(path)}': {exception.Message}");
            return false;
        }
    }
}
