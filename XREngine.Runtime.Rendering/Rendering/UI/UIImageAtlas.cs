using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

/// <summary>Owns an immutable atlas page. Repacking creates a different page.</summary>
public sealed class UIImageAtlas
{
    private readonly byte[] _pixels;
    private readonly Dictionary<string, Vector4> _regions = new(StringComparer.Ordinal);

    public UIImageAtlas(IReadOnlyList<UIImageAtlasSource> images, int pageSize = 256, bool srgb = true)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(images), "UI.AtlasImageCount: one page supports 1..256 images.");
        if (pageSize is < 4 or > 2048 || (pageSize & (pageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "UI.AtlasPageSize: use a power of two from 4 through 2048.");
        PageSize = pageSize;
        Srgb = srgb;
        _pixels = new byte[checked(pageSize * pageSize * 4)];
        int x = 0, y = 0, shelfHeight = 0;
        for (int index = 0; index < images.Count; index++)
        {
            UIImageAtlasSource image = images[index] ?? throw new ArgumentException("UI.AtlasNullImage", nameof(images));
            if (image.Srgb != srgb)
                throw new ArgumentException("UI.AtlasColorSpace: all images must use the page color space.", nameof(images));
            int width = image.Width + 2, height = image.Height + 2;
            if (x + width > pageSize)
            {
                x = 0;
                y += shelfHeight;
                shelfHeight = 0;
            }
            if (width > pageSize || y + height > pageSize)
                throw new InvalidOperationException("UI.AtlasPageFull: use another bounded page or a larger page size.");
            Vector4 uv = new((float)(x + 1) / pageSize, (float)(y + 1) / pageSize,
                (float)(x + 1 + image.Width) / pageSize, (float)(y + 1 + image.Height) / pageSize);
            if (!_regions.TryAdd(image.Key, uv))
                throw new ArgumentException("UI.AtlasDuplicateKey: image keys must be unique.", nameof(images));
            // A one-pixel edge gutter keeps linear samples inside this image.
            for (int row = -1; row <= image.Height; row++)
            {
                int sourceRow = Math.Clamp(row, 0, image.Height - 1);
                for (int column = -1; column <= image.Width; column++)
                {
                    int sourceColumn = Math.Clamp(column, 0, image.Width - 1);
                    image.Pixels.Slice((sourceRow * image.Width + sourceColumn) * 4, 4)
                        .CopyTo(_pixels.AsSpan(((y + row + 1) * pageSize + x + column + 1) * 4, 4));
                }
            }
            x += width;
            shelfHeight = Math.Max(shelfHeight, height);
        }
    }

    public int PageSize { get; }
    public bool Srgb { get; }
    public int Count => _regions.Count;
    public Vector4 GetRegion(string key) => _regions.TryGetValue(key, out Vector4 uv)
        ? uv : throw new KeyNotFoundException($"UI.AtlasImageMissing: {key}");
    public byte[] CopyPixels() => (byte[])_pixels.Clone();

    /// <summary>Create an independent source texture. The caller owns its destruction.</summary>
    public XRTexture2D CreateTexture()
    {
        XRTexture2D texture = new((uint)PageSize, (uint)PageSize,
            Srgb ? EPixelInternalFormat.Srgb8Alpha8 : EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba, EPixelType.UnsignedByte)
        {
            SizedInternalFormat = Srgb ? ESizedInternalFormat.Srgb8Alpha8 : ESizedInternalFormat.Rgba8,
            ImportedColorSpace = Srgb ? ETextureColorSpace.Srgb : ETextureColorSpace.Linear,
            AutoGenerateMipmaps = false,
            AllowAutomaticImportedTextureStreaming = false,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };
        texture.Mipmaps[0].DataBytes = CopyPixels();
        return texture;
    }
}
