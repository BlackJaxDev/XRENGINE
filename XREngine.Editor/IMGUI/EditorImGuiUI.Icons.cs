using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Svg.Skia;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const int DefaultIconSize = 24;
    private const int ToolbarIconWarmupFrames = 180;
    private const int ToolbarIconCpuRasterizationsPerFrame = 1;
    private const int ToolbarIconGpuUploadsPerFrame = 1;

    private static readonly Dictionary<string, XRTexture2D> _iconCache = new();
    private static readonly HashSet<string> _uploadedOpenGLIconCache = [];
    private static readonly object _iconCacheLock = new();

    private static int _toolbarIconFrameIndex;
    private static int _toolbarIconCpuRasterizationsThisFrame;
    private static int _toolbarIconGpuUploadsThisFrame;
    private static bool _loggedIconPath = false;

    private static void BeginToolbarIconFrame()
    {
        _toolbarIconFrameIndex++;
        _toolbarIconCpuRasterizationsThisFrame = 0;
        _toolbarIconGpuUploadsThisFrame = 0;
    }
    
    /// <summary>
    /// Gets or creates an icon texture from an SVG file.
    /// </summary>
    private static XRTexture2D? GetIcon(string iconName, int size = DefaultIconSize)
    {
        string cacheKey = BuildIconCacheKey(iconName, size);
        
        lock (_iconCacheLock)
        {
            if (_iconCache.TryGetValue(cacheKey, out var cachedTexture))
                return cachedTexture;
        }

        if (_toolbarIconCpuRasterizationsThisFrame >= ToolbarIconCpuRasterizationsPerFrame)
            return null;
        _toolbarIconCpuRasterizationsThisFrame++;

        string relativePath = SvgEditorIcons.GetIconPath(iconName);
        string? fullPath = null;
        
        // Try engine assets path first
        if (Engine.Assets?.EngineAssetsPath is not null)
        {
            string candidate = Path.Combine(Engine.Assets.EngineAssetsPath, relativePath);
            if (File.Exists(candidate))
                fullPath = candidate;
        }
        
        // Try relative to editor executable
        if (fullPath is null)
        {
            string? exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (exeDir is not null)
            {
                string candidate = Path.Combine(exeDir, relativePath);
                if (File.Exists(candidate))
                    fullPath = candidate;
            }
        }
        
        // Try current directory
        if (fullPath is null && File.Exists(relativePath))
        {
            fullPath = Path.GetFullPath(relativePath);
        }

        if (fullPath is null)
        {
            if (!_loggedIconPath)
            {
                _loggedIconPath = true;
                XREngine.Debug.LogWarning($"SVG icon not found. Searched: EngineAssetsPath='{Engine.Assets?.EngineAssetsPath}', RelativePath='{relativePath}'");
            }
            return null;
        }

        XRTexture2D? texture = RasterizeSvgIcon(fullPath, size);
        if (texture is not null)
        {
            lock (_iconCacheLock)
            {
                _iconCache[cacheKey] = texture;
            }
        }

        return texture;
    }

    /// <summary>
    /// Rasterizes an SVG file to a texture using SkiaSharp.
    /// </summary>
    private static XRTexture2D? RasterizeSvgIcon(string svgPath, int size)
    {
        try
        {
            using SKSvg svg = new();
            svg.Load(svgPath);
            
            SKPicture? picture = svg.Picture;
            if (picture is null)
            {
                XREngine.Debug.LogWarning($"Failed to load SVG '{svgPath}'. No drawable picture was produced.");
                return null;
            }

            SKRect cullRect = picture.CullRect;
            float scaleX = cullRect.Width > 0 ? size / cullRect.Width : 1.0f;
            float scaleY = cullRect.Height > 0 ? size / cullRect.Height : 1.0f;
            float scale = MathF.Min(scaleX, scaleY);

            using SKBitmap bitmap = new(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.Transparent);

            float translatedX = (size - (cullRect.Width * scale)) * 0.5f;
            float translatedY = (size - (cullRect.Height * scale)) * 0.5f;

            SKMatrix matrix = SKMatrix.CreateScale(scale, scale);
            matrix = matrix.PostConcat(SKMatrix.CreateTranslation(translatedX, translatedY));

            canvas.DrawPicture(picture, in matrix);
            canvas.Flush();

            var pixelSpan = bitmap.GetPixelSpan();
            using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(MemoryMarshal.AsBytes(pixelSpan), size, size);

            XRTexture2D texture = new(image)
            {
                Name = Path.GetFileNameWithoutExtension(svgPath),
                FilePath = svgPath,
                AutoGenerateMipmaps = false,
                Resizable = false,
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge
            };

            return texture;
        }
        catch (Exception ex)
        {
            XREngine.Debug.LogError($"Failed to rasterize SVG icon '{svgPath}': {ex.Message}");
            return null;
        }
    }
    
    private static bool TryGetIconHandle(string iconName, out nint handle)
    {
        handle = nint.Zero;
        if (_toolbarIconFrameIndex < ToolbarIconWarmupFrames)
            return false;

        string cacheKey = BuildIconCacheKey(iconName, DefaultIconSize);
        var texture = GetIcon(iconName);
        if (texture is null) return false;

        if (AbstractRenderer.Current is OpenGLRenderer glRenderer)
        {
            var apiTexture = glRenderer.GenericToAPI<GLTexture2D>(texture);
            if (apiTexture is null)
                return false;

            bool uploadNow = false;
            lock (_iconCacheLock)
            {
                if (!_uploadedOpenGLIconCache.Contains(cacheKey))
                {
                    if (_toolbarIconGpuUploadsThisFrame >= ToolbarIconGpuUploadsPerFrame)
                        return false;

                    _toolbarIconGpuUploadsThisFrame++;
                    uploadNow = true;
                }
            }

            if (uploadNow)
            {
                apiTexture.Generate();
                apiTexture.PushData();
                lock (_iconCacheLock)
                    _uploadedOpenGLIconCache.Add(cacheKey);
            }

            if (!apiTexture.TryGetBindingId(out uint bindingId) || bindingId == OpenGLRenderer.GLObjectBase.InvalidBindingId)
                return false;

            handle = (nint)bindingId;
            return true;
        }

        if (AbstractRenderer.Current is VulkanRenderer vkRenderer)
        {
            IntPtr textureId = vkRenderer.RegisterImGuiTexture(texture);
            if (textureId == IntPtr.Zero)
                return false;

            handle = (nint)textureId;
            return true;
        }

        return false;
    }

    private static string BuildIconCacheKey(string iconName, int size)
        => $"{iconName}_{size}";
}
