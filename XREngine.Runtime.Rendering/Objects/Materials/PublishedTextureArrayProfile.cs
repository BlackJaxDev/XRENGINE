using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Target-only settings that the cooked XRTexture2DArray payload omits. The profile
/// records each distinct layer image by first-use role, because the array's nested
/// asset payload has its own reference scope: a second serialized reference to a
/// layer image would decode as another image. The decoder validates the complete
/// role graph against the decoded array before it restores any setting.
/// </summary>
public sealed record PublishedTextureArrayProfile
{
    public const int MaximumLayers = 64;
    public const int MaximumMips = 64;

    public Guid ArrayId { get; init; }
    public ESizedInternalFormat ArrayFormat { get; init; }
    /// <summary>One entry for each distinct layer image, in first-use order. No entry has a direct image.</summary>
    public UnlitPublishedImageEntry[] Images { get; init; } = [];
    /// <summary>The image role of each layer, as an index into <see cref="Images"/>.</summary>
    public byte[] LayerRoles { get; init; } = [];
    public int LargestMipmapLevel { get; init; }
    public int SmallestAllowedMipmapLevel { get; init; }
    public int MinLOD { get; init; }
    public int MaxLOD { get; init; }
    public bool AutoGenerateMipmaps { get; init; }

    /// <summary>Reads the layer topology, the image settings, and the array mip state of a static CPU-backed array.</summary>
    public static PublishedTextureArrayProfile Capture(XRTexture2DArray array)
    {
        ArgumentNullException.ThrowIfNull(array);
        ValidateArrayShape(array);
        Dictionary<XRTexture2D, byte> unique = new(ReferenceEqualityComparer.Instance);
        List<UnlitPublishedImageEntry> images = [];
        byte[] roles = new byte[array.Textures.Length];
        for (int layer = 0; layer < array.Textures.Length; layer++)
        {
            XRTexture2D image = array.Textures[layer];
            if (!unique.TryGetValue(image, out byte role))
            {
                role = checked((byte)images.Count);
                unique.Add(image, role);
                images.Add(CaptureImage(image, layer, direct: false));
            }
            roles[layer] = role;
        }
        return new()
        {
            ArrayId = array.ID,
            ArrayFormat = array.SizedInternalFormat,
            Images = [.. images],
            LayerRoles = roles,
            LargestMipmapLevel = array.LargestMipmapLevel,
            SmallestAllowedMipmapLevel = array.SmallestAllowedMipmapLevel,
            MinLOD = array.MinLOD,
            MaxLOD = array.MaxLOD,
            AutoGenerateMipmaps = array.AutoGenerateMipmaps,
        };
    }

    /// <summary>
    /// Checks the array identity, format, layer roles, and every image entry against
    /// the array, and returns the distinct layer image of each role. Changes nothing.
    /// </summary>
    public XRTexture2D[] Validate(XRTexture2DArray array)
    {
        ArgumentNullException.ThrowIfNull(array);
        ValidateArrayShape(array);
        if (Images is null || LayerRoles is null || array.ID != ArrayId || array.SizedInternalFormat != ArrayFormat ||
            LayerRoles.Length != array.Textures.Length || Images.Length is < 1 or > MaximumLayers ||
            Images.Length > LayerRoles.Length)
            throw Invalid("the array identity, format, or layer roles differ from the profile");
        XRTexture2D?[] unique = new XRTexture2D[Images.Length];
        Dictionary<XRTexture2D, int> rolesByImage = new(ReferenceEqualityComparer.Instance);
        for (int layer = 0; layer < LayerRoles.Length; layer++)
        {
            int role = LayerRoles[layer];
            if ((uint)role >= (uint)Images.Length) throw Invalid("the array has an invalid layer role");
            XRTexture2D image = array.Textures[layer];
            if (unique[role] is null)
            {
                if (Images[role] is null || role != rolesByImage.Count ||
                    Images[role].FirstLayer != layer || rolesByImage.ContainsKey(image) ||
                    Images[role].DirectImage is not null)
                    throw Invalid("the array first-use role or distinct-image topology changed");
                unique[role] = image;
                rolesByImage.Add(image, role);
            }
            else if (!ReferenceEquals(unique[role], image) ||
                !rolesByImage.TryGetValue(image, out int previousRole) || previousRole != role)
                throw Invalid("the array repeated-layer alias changed");
        }
        XRTexture2D[] images = new XRTexture2D[Images.Length];
        for (int role = 0; role < Images.Length; role++)
        {
            images[role] = unique[role] ?? throw Invalid("the array has an unused image role");
            ValidateImage(Images[role], images[role]);
        }
        return images;
    }

    /// <summary>
    /// Validates the complete profile first, then writes the omitted image settings and
    /// the array mip state onto the newly decoded array. Never call it on a borrowed array.
    /// </summary>
    public void Restore(XRTexture2DArray array)
    {
        XRTexture2D[] images = Validate(array);
        // Array proxy setters are deliberately avoided; each detached layer image takes its own settings.
        for (int role = 0; role < Images.Length; role++)
            RestoreImage(Images[role], images[role]);
        array.LargestMipmapLevel = LargestMipmapLevel;
        array.SmallestAllowedMipmapLevel = SmallestAllowedMipmapLevel;
        array.MinLOD = MinLOD;
        array.MaxLOD = MaxLOD;
        array.AutoGenerateMipmaps = AutoGenerateMipmaps;
    }

    /// <summary>Reads one static image's mip metadata, sampler state, and import settings.</summary>
    internal static UnlitPublishedImageEntry CaptureImage(XRTexture2D image, int firstLayer, bool direct)
    {
        PublishedStandardLitTextureSettings settings = PublishedStandardLitTextureSettings.Capture(image);
        if (image.Mipmaps.Length > MaximumMips) throw Invalid("an image has more than 64 static mips");
        UnlitPublishedMipEntry[] mips = new UnlitPublishedMipEntry[image.Mipmaps.Length];
        for (int index = 0; index < mips.Length; index++)
        {
            Mipmap2D mip = image.Mipmaps[index];
            mips[index] = new()
            {
                Width = mip.Width,
                Height = mip.Height,
                InternalFormat = mip.InternalFormat,
                PixelFormat = mip.PixelFormat,
                PixelType = mip.PixelType,
                DataLength = mip.Data!.Length,
            };
        }
        UnlitPublishedImageEntry entry = new()
        {
            FirstLayer = firstLayer,
            ImageId = image.ID,
            DirectImage = direct ? image : null,
            SizedInternalFormat = image.SizedInternalFormat,
            Width = image.Width,
            Height = image.Height,
            Mips = mips,
            Settings = settings,
            MinFilter = image.MinFilter,
            MagFilter = image.MagFilter,
            UWrap = image.UWrap,
            VWrap = image.VWrap,
            LodBias = image.LodBias,
        };
        ValidateImage(entry, image);
        return entry;
    }

    /// <summary>Checks that an image entry still describes the image's identity, dimensions, mips, and settings.</summary>
    internal static void ValidateImage(UnlitPublishedImageEntry entry, XRTexture2D image)
    {
        if (entry is null || image.GetType() != typeof(XRTexture2D) || entry.ImageId != image.ID ||
            entry.SizedInternalFormat != image.SizedInternalFormat || entry.Width != image.Width ||
            entry.Height != image.Height || entry.Mips is null || entry.Mips.Length is < 1 or > MaximumMips ||
            entry.Mips.Length != image.Mipmaps.Length ||
            !Enum.IsDefined(entry.MinFilter) || !Enum.IsDefined(entry.MagFilter) ||
            !Enum.IsDefined(entry.UWrap) || !Enum.IsDefined(entry.VWrap) || !float.IsFinite(entry.LodBias) ||
            !float.IsFinite(entry.Settings.MaxAnisotropy) || entry.Settings.MaxAnisotropy is < 1 or > 16 ||
            entry.Settings.MaxAnisotropy != MathF.Truncate(entry.Settings.MaxAnisotropy) ||
            !Enum.IsDefined(entry.Settings.CompareFunc) || !Enum.IsDefined(entry.Settings.ImportedColorSpace) ||
            !Enum.IsDefined(entry.Settings.ImportedUsage))
            throw Invalid("an image identity, dimensions, or sampler/import settings are invalid");
        _ = PublishedStandardLitTextureSettings.Capture(image);
        for (int index = 0; index < entry.Mips.Length; index++)
        {
            UnlitPublishedMipEntry expected = entry.Mips[index];
            Mipmap2D actual = image.Mipmaps[index];
            if (expected is null || actual is null || expected.Width != actual.Width ||
                expected.Height != actual.Height || expected.InternalFormat != actual.InternalFormat ||
                expected.PixelFormat != actual.PixelFormat || expected.PixelType != actual.PixelType ||
                expected.DataLength != actual.Data?.Length)
                throw Invalid("an image mip metadata differs from the profiled source");
        }
    }

    /// <summary>Writes the entry's sampler state and import settings onto a decoded image.</summary>
    internal static void RestoreImage(UnlitPublishedImageEntry entry, XRTexture2D image)
    {
        entry.Settings.ApplyTo(image);
        image.MinFilter = entry.MinFilter;
        image.MagFilter = entry.MagFilter;
        image.UWrap = entry.UWrap;
        image.VWrap = entry.VWrap;
        image.LodBias = entry.LodBias;
    }

    /// <summary>Requires 1 to 64 static single-sample CPU layers with one format, size, and mip chain.</summary>
    internal static void ValidateArrayShape(XRTexture2DArray array)
    {
        if (array.GetType() != typeof(XRTexture2DArray) || array.MultiSample || array.Resizable ||
            array.CopyGpuLayerSources || array.OVRMultiViewParameters is not null ||
            array.Textures is null || array.Textures.Length is < 1 or > MaximumLayers)
            throw Invalid("the array must contain 1 to 64 static single-sample CPU layers");
        XRTexture2D first = array.Textures[0] ?? throw Invalid("the array has a missing first layer");
        if (array.SizedInternalFormat != first.SizedInternalFormat || first.Width == 0 || first.Height == 0)
            throw Invalid("the array requires a matching nonzero image format and dimensions");
        int mipCount = first.Mipmaps.Length;
        for (int layer = 0; layer < array.Textures.Length; layer++)
        {
            XRTexture2D image = array.Textures[layer] ?? throw Invalid("the array contains a missing image");
            if (image.GetType() != typeof(XRTexture2D) || image.Width != first.Width || image.Height != first.Height ||
                image.SizedInternalFormat != first.SizedInternalFormat || image.Mipmaps.Length != mipCount)
                throw Invalid("the array layers require identical dimensions, format, and mip count");
            _ = PublishedStandardLitTextureSettings.Capture(image);
            for (int mip = 0; mip < mipCount; mip++)
            {
                Mipmap2D expected = first.Mipmaps[mip], actual = image.Mipmaps[mip];
                if (actual.Width != expected.Width || actual.Height != expected.Height ||
                    actual.InternalFormat != expected.InternalFormat || actual.PixelFormat != expected.PixelFormat ||
                    actual.PixelType != expected.PixelType)
                    throw Invalid("the array layers require matching mip dimensions and pixel metadata");
            }
        }
    }

    private static InvalidDataException Invalid(string reason) => new($"CookedMaterial.TextureArrayProfileInvalid: {reason}.");
}
