using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Svg.Skia;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Data.Core;

namespace XREngine.Scene.Components.UI;

/// <summary>
/// Renders an SVG file into a cached texture and displays it like a regular textured UI component.
/// </summary>
public class UISvgComponent : UIMaterialComponent
{
    private record struct SvgCacheKey(string Path, int Width, int Height, bool MaintainAspectRatio);

    private sealed class SvgCacheKeyComparer : IEqualityComparer<SvgCacheKey>
    {
        public bool Equals(SvgCacheKey x, SvgCacheKey y)
            => x.Width == y.Width
                && x.Height == y.Height
                && x.MaintainAspectRatio == y.MaintainAspectRatio
                && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(SvgCacheKey obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path ?? string.Empty),
                obj.Width,
                obj.Height,
                obj.MaintainAspectRatio);
    }

    private sealed class SvgCacheEntry
    {
        public SvgCacheEntry(XRTexture2D texture)
            => Texture = texture;

        public XRTexture2D Texture { get; }
        public int RefCount { get; set; } = 1;
    }

    private static readonly object _cacheLock = new();
    private static readonly Dictionary<SvgCacheKey, SvgCacheEntry> _textureCache = new(new SvgCacheKeyComparer());

    private string? _svgPath;
    /// <summary>
    /// Path to the SVG file to rasterize.
    /// </summary>
    public string? SvgPath
    {
        get => _svgPath;
        set => SetField(ref _svgPath, value);
    }

    private Vector2? _rasterSizeOverride;
    /// <summary>
    /// Optional override for the rasterized size of the SVG, in pixels.
    /// When unset, the component's layout size is used and falls back to the SVG's intrinsic size.
    /// </summary>
    public Vector2? RasterSizeOverride
    {
        get => _rasterSizeOverride;
        set => SetField(ref _rasterSizeOverride, value);
    }

    private bool _maintainAspectRatio = true;
    /// <summary>
    /// When true, scales the SVG uniformly to preserve its aspect ratio.
    /// </summary>
    public bool MaintainAspectRatio
    {
        get => _maintainAspectRatio;
        set => SetField(ref _maintainAspectRatio, value);
    }

    private bool _autoSizeToContent = true;
    /// <summary>
    /// When true, sets the component's width/height to the rasterized texture size if none are provided.
    /// </summary>
    public bool AutoSizeToContent
    {
        get => _autoSizeToContent;
        set => SetField(ref _autoSizeToContent, value);
    }

    private SvgCacheKey? _activeCacheKey;
    private bool _needsRasterize;

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(SvgPath):
            case nameof(RasterSizeOverride):
            case nameof(MaintainAspectRatio):
                RequestRasterization();
                break;
        }
    }

    protected override void UITransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
    {
        base.UITransformPropertyChanged(sender, e);

        if (e.PropertyName == nameof(UIBoundableTransform.AxisAlignedRegion))
            RequestRasterization();
    }

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();

        if (_needsRasterize || _activeCacheKey is null)
            RequestRasterization();
    }

    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        ReleaseActiveTexture();
    }

    private void RequestRasterization()
    {
        if (!IsActive)
        {
            _needsRasterize = true;
            return;
        }

        _needsRasterize = false;

        if (!Engine.InvokeOnMainThread(RasterizeAndApply))
            RasterizeAndApply();
    }

    private void RasterizeAndApply()
    {
        if (!IsActive)
            return;

        string? path = ResolvePath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ReleaseActiveTexture();
            return;
        }

        try
        {
            using SKSvg svg = new();
            svg.Load(path);
            SKPicture? picture = svg.Picture;
            if (picture is null)
            {
                Debug.LogWarning($"Failed to load SVG '{path}'. No drawable picture was produced.");
                return;
            }

            SKSize pictureSize = picture.CullRect.Size;
            SKSizeI targetSize = ResolveTargetSize(pictureSize);
            SvgCacheKey cacheKey = new(path, targetSize.Width, targetSize.Height, MaintainAspectRatio);

            XRTexture2D texture = AcquireTexture(cacheKey, picture, targetSize);
            ApplyTexture(texture, targetSize);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to rasterize SVG '{path}': {ex.Message}");
        }
    }

    private XRTexture2D AcquireTexture(SvgCacheKey key, SKPicture picture, SKSizeI targetSize)
    {
        lock (_cacheLock)
        {
            if (_activeCacheKey is not null && _activeCacheKey.Value.Equals(key) &&
                _textureCache.TryGetValue(key, out SvgCacheEntry? current))
            {
                return current.Texture;
            }

            if (_textureCache.TryGetValue(key, out SvgCacheEntry? cached))
            {
                cached.RefCount++;
                _textureCache[key] = cached;
                _activeCacheKey = key;
                return cached.Texture;
            }
        }

        ReleaseActiveTexture();
        XRTexture2D texture = RasterizeSvg(picture, targetSize, key.Path);

        lock (_cacheLock)
        {
            _textureCache[key] = new SvgCacheEntry(texture);
            _activeCacheKey = key;
        }

        return texture;
    }

    private XRTexture2D RasterizeSvg(SKPicture picture, SKSizeI targetSize, string path)
    {
        using SKBitmap bitmap = new(targetSize.Width, targetSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.Transparent);

        (float scaleX, float scaleY) = ComputeScale(picture.CullRect.Size, targetSize);
        float translatedX = (targetSize.Width - (picture.CullRect.Width * scaleX)) * 0.5f;
        float translatedY = (targetSize.Height - (picture.CullRect.Height * scaleY)) * 0.5f;

        SKMatrix matrix = SKMatrix.CreateScale(scaleX, scaleY);
        matrix = matrix.PostConcat(SKMatrix.CreateTranslation(translatedX, translatedY));

        canvas.DrawPicture(picture, ref matrix);
        canvas.Flush();

        var pixelSpan = bitmap.GetPixelSpan();
        using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(MemoryMarshal.AsBytes(pixelSpan), targetSize.Width, targetSize.Height);

        XRTexture2D texture = new(image)
        {
            Name = Path.GetFileNameWithoutExtension(path),
            FilePath = path,
            AutoGenerateMipmaps = true,
            Resizable = false,
            MinFilter = ETexMinFilter.LinearMipmapLinear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge
        };

        texture.Generate();
        texture.PushData();

        return texture;
    }

    private (float scaleX, float scaleY) ComputeScale(SKSize pictureSize, SKSizeI targetSize)
    {
        float scaleX = pictureSize.Width <= 0 ? 1.0f : targetSize.Width / pictureSize.Width;
        float scaleY = pictureSize.Height <= 0 ? 1.0f : targetSize.Height / pictureSize.Height;

        if (MaintainAspectRatio)
        {
            float uniformScale = MathF.Min(scaleX, scaleY);
            scaleX = uniformScale;
            scaleY = uniformScale;
        }

        return (scaleX, scaleY);
    }

    private void ApplyTexture(XRTexture2D texture, SKSizeI targetSize)
    {
        XRMaterial? material = Material;
        if (material is null || material.Textures.Count == 0)
        {
            material = XRMaterial.CreateUnlitTextureMaterialForward(texture);
            material.RenderOptions = SvgRenderParameters;
        }
        else if (material.Textures.Count > 0)
            material.Textures[0] = texture;
        else
            material.Textures.Add(texture);

        material.RenderOptions ??= SvgRenderParameters;

        Material = material;

        if (AutoSizeToContent)
            ApplyAutoSize(targetSize);
    }

    private void ApplyAutoSize(SKSizeI targetSize)
    {
        var tfm = BoundableTransform;

        if (tfm.Width is null || tfm.Width <= 0.0f)
            tfm.Width = targetSize.Width;

        if (tfm.Height is null || tfm.Height <= 0.0f)
            tfm.Height = targetSize.Height;
    }

    private SKSizeI ResolveTargetSize(SKSize pictureSize)
    {
        Vector2 size = RasterSizeOverride ?? new Vector2(BoundableTransform.ActualWidth, BoundableTransform.ActualHeight);

        float width = size.X;
        float height = size.Y;

        if (width <= 0.0f && pictureSize.Width > 0.0f)
            width = pictureSize.Width;

        if (height <= 0.0f && pictureSize.Height > 0.0f)
            height = pictureSize.Height;

        if (width <= 0.0f)
            width = height > 0.0f ? height : 256.0f;

        if (height <= 0.0f)
            height = width > 0.0f ? width : 256.0f;

        return new SKSizeI((int)MathF.Max(1.0f, MathF.Round(width)), (int)MathF.Max(1.0f, MathF.Round(height)));
    }

    private string? ResolvePath()
    {
        if (string.IsNullOrWhiteSpace(SvgPath))
            return null;

        try
        {
            return Path.GetFullPath(SvgPath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to resolve SVG path '{SvgPath}': {ex.Message}");
            return null;
        }
    }

    private void ReleaseActiveTexture()
    {
        if (_activeCacheKey is null)
            return;

        lock (_cacheLock)
        {
            if (_textureCache.TryGetValue(_activeCacheKey.Value, out SvgCacheEntry? entry))
            {
                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    entry.Texture.Destroy();
                    _textureCache.Remove(_activeCacheKey.Value);
                }
                else
                    _textureCache[_activeCacheKey.Value] = entry;
            }
        }

        _activeCacheKey = null;
    }

    private static RenderingParameters SvgRenderParameters { get; } = new()
    {
        CullMode = ECullMode.None,
        DepthTest = new()
        {
            Enabled = ERenderParamUsage.Disabled,
            Function = EComparison.Always
        },
        BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
    };
}
