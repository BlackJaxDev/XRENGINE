using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Target-only settings omitted by the cooked XRTexture2D payload. Array layers are
/// addressed by first-use role because an array's nested asset payload has its own
/// reference scope; a second serialized child reference would create another image.
/// </summary>
public sealed record UnlitPublishedTextureProfile
{
    public int Version { get; init; } = 1;
    public XRTexture? SourceTexture { get; init; }
    public Guid SourceTextureId { get; init; }
    public UnlitPublishedImageEntry[] Images { get; init; } = [];
    public byte[] LayerRoles { get; init; } = [];
    public ESizedInternalFormat ArrayFormat { get; init; }
    public int LargestMipmapLevel { get; init; }
    public int SmallestAllowedMipmapLevel { get; init; }
    public int MinLOD { get; init; }
    public int MaxLOD { get; init; }
    public bool AutoGenerateMipmaps { get; init; }

    public static UnlitPublishedTextureProfile Capture(XRTexture? texture)
    {
        if (texture is null) return new();
        if (texture.GetType() == typeof(XRTexture2D))
        {
            XRTexture2D image = (XRTexture2D)texture;
            return new()
            {
                SourceTexture = image,
                SourceTextureId = image.ID,
                Images = [CaptureImage(image, 0, direct: true)],
            };
        }
        if (texture.GetType() != typeof(XRTexture2DArray))
            throw Invalid("Texture0 must be an exact XRTexture2D or XRTexture2DArray");

        XRTexture2DArray array = (XRTexture2DArray)texture;
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
            SourceTexture = array,
            SourceTextureId = array.ID,
            Images = [.. images],
            LayerRoles = roles,
            ArrayFormat = array.SizedInternalFormat,
            LargestMipmapLevel = array.LargestMipmapLevel,
            SmallestAllowedMipmapLevel = array.SmallestAllowedMipmapLevel,
            MinLOD = array.MinLOD,
            MaxLOD = array.MaxLOD,
            AutoGenerateMipmaps = array.AutoGenerateMipmaps,
        };
    }

    /// <summary>Validates the complete decoded role graph before restoring any omitted settings.</summary>
    public void RestoreDecodedTextures(XRMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (Version != 1 || Images is null || LayerRoles is null)
            throw Invalid("texture profile version or tables are invalid");
        if (SourceTexture is null)
        {
            if (material.Textures.Count != 0 || material.SurfaceTextureBindings.Length != 0 || Images.Length != 0 ||
                LayerRoles.Length != 0 || SourceTextureId != Guid.Empty || !HasNoArrayState())
                throw Invalid("untextured role contains a sampled image");
            return;
        }
        if (material.Textures.Count != 1 || !ReferenceEquals(material.Textures[0], SourceTexture) ||
            material.SurfaceTextureBindings.Length != 1 ||
            material.SurfaceTextureBindings[0] is not { Semantic: EMaterialTextureSemantic.BaseColor } binding ||
            !ReferenceEquals(binding.Texture, SourceTexture) || SourceTexture.ID != SourceTextureId)
            throw Invalid("Texture0 and its base-color role do not reference the profiled image");

        if (SourceTexture.GetType() == typeof(XRTexture2D))
        {
            XRTexture2D image = (XRTexture2D)SourceTexture;
            if (Images.Length != 1 || LayerRoles.Length != 0 || Images[0] is null || Images[0].FirstLayer != 0 ||
                !ReferenceEquals(Images[0].DirectImage, image) || !HasNoArrayState())
                throw Invalid("plain Texture0 profile has an invalid direct image role");
            ValidateImage(Images[0], image);
            RestoreImage(Images[0], image);
            return;
        }
        if (SourceTexture.GetType() != typeof(XRTexture2DArray))
            throw Invalid("Texture0 has an unsupported image type");

        XRTexture2DArray array = (XRTexture2DArray)SourceTexture;
        ValidateArrayShape(array);
        if (array.SizedInternalFormat != ArrayFormat || LayerRoles.Length != array.Textures.Length ||
            Images.Length is < 1 or > 64 || Images.Length > LayerRoles.Length)
            throw Invalid("Texture0 array format or layer roles differ from the profile");
        XRTexture2D?[] unique = new XRTexture2D[Images.Length];
        Dictionary<XRTexture2D, int> rolesByImage = new(ReferenceEqualityComparer.Instance);
        for (int layer = 0; layer < LayerRoles.Length; layer++)
        {
            int role = LayerRoles[layer];
            if ((uint)role >= (uint)Images.Length) throw Invalid("Texture0 array has an invalid layer role");
            XRTexture2D image = array.Textures[layer];
            if (unique[role] is null)
            {
                if (Images[role] is null || role != rolesByImage.Count ||
                    Images[role].FirstLayer != layer || rolesByImage.ContainsKey(image) ||
                    Images[role].DirectImage is not null)
                    throw Invalid("Texture0 array first-use role or distinct-image topology changed");
                unique[role] = image;
                rolesByImage.Add(image, role);
            }
            else if (!ReferenceEquals(unique[role], image) ||
                !rolesByImage.TryGetValue(image, out int previousRole) || previousRole != role)
                throw Invalid("Texture0 array repeated-layer alias changed");
        }
        for (int role = 0; role < Images.Length; role++)
        {
            XRTexture2D image = unique[role] ?? throw Invalid("Texture0 array has an unused image role");
            ValidateImage(Images[role], image);
        }

        // All role, identity, mip, and settings checks have completed. These are
        // detached decoded images; array proxy setters are deliberately avoided.
        for (int role = 0; role < Images.Length; role++)
            RestoreImage(Images[role], unique[role]!);
        array.LargestMipmapLevel = LargestMipmapLevel;
        array.SmallestAllowedMipmapLevel = SmallestAllowedMipmapLevel;
        array.MinLOD = MinLOD;
        array.MaxLOD = MaxLOD;
        array.AutoGenerateMipmaps = AutoGenerateMipmaps;
    }

    private static UnlitPublishedImageEntry CaptureImage(XRTexture2D image, int firstLayer, bool direct)
    {
        PublishedStandardLitTextureSettings settings = PublishedStandardLitTextureSettings.Capture(image);
        if (image.Mipmaps.Length > 64) throw Invalid("an image has more than 64 static mips");
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

    private static void ValidateImage(UnlitPublishedImageEntry entry, XRTexture2D image)
    {
        if (entry is null || image.GetType() != typeof(XRTexture2D) || entry.ImageId != image.ID ||
            entry.SizedInternalFormat != image.SizedInternalFormat || entry.Width != image.Width ||
            entry.Height != image.Height || entry.Mips is null || entry.Mips.Length is < 1 or > 64 ||
            entry.Mips.Length != image.Mipmaps.Length ||
            !Enum.IsDefined(entry.MinFilter) || !Enum.IsDefined(entry.MagFilter) ||
            !Enum.IsDefined(entry.UWrap) || !Enum.IsDefined(entry.VWrap) || !float.IsFinite(entry.LodBias) ||
            !float.IsFinite(entry.Settings.MaxAnisotropy) || entry.Settings.MaxAnisotropy is < 1 or > 16 ||
            entry.Settings.MaxAnisotropy != MathF.Truncate(entry.Settings.MaxAnisotropy) ||
            !Enum.IsDefined(entry.Settings.CompareFunc) || !Enum.IsDefined(entry.Settings.ImportedColorSpace) ||
            !Enum.IsDefined(entry.Settings.ImportedUsage))
            throw Invalid("Texture0 image identity, dimensions, or sampler/import settings are invalid");
        _ = PublishedStandardLitTextureSettings.Capture(image);
        for (int index = 0; index < entry.Mips.Length; index++)
        {
            UnlitPublishedMipEntry expected = entry.Mips[index];
            Mipmap2D actual = image.Mipmaps[index];
            if (expected is null || actual is null || expected.Width != actual.Width ||
                expected.Height != actual.Height || expected.InternalFormat != actual.InternalFormat ||
                expected.PixelFormat != actual.PixelFormat || expected.PixelType != actual.PixelType ||
                expected.DataLength != actual.Data?.Length)
                throw Invalid("Texture0 image mip metadata differs from the profiled source");
        }
    }

    private static void RestoreImage(UnlitPublishedImageEntry entry, XRTexture2D image)
    {
        entry.Settings.ApplyTo(image);
        image.MinFilter = entry.MinFilter;
        image.MagFilter = entry.MagFilter;
        image.UWrap = entry.UWrap;
        image.VWrap = entry.VWrap;
        image.LodBias = entry.LodBias;
    }

    private static void ValidateArrayShape(XRTexture2DArray array)
    {
        if (array.GetType() != typeof(XRTexture2DArray) || array.MultiSample || array.Resizable ||
            array.CopyGpuLayerSources || array.OVRMultiViewParameters is not null ||
            array.Textures is null || array.Textures.Length is < 1 or > 64)
            throw Invalid("Texture0 array must contain 1–64 static single-sample CPU layers");
        XRTexture2D first = array.Textures[0] ?? throw Invalid("Texture0 array has a missing first layer");
        if (array.SizedInternalFormat != first.SizedInternalFormat || first.Width == 0 || first.Height == 0)
            throw Invalid("Texture0 array requires a matching nonzero image format and dimensions");
        int mipCount = first.Mipmaps.Length;
        for (int layer = 0; layer < array.Textures.Length; layer++)
        {
            XRTexture2D image = array.Textures[layer] ?? throw Invalid("Texture0 array contains a missing image");
            if (image.GetType() != typeof(XRTexture2D) || image.Width != first.Width || image.Height != first.Height ||
                image.SizedInternalFormat != first.SizedInternalFormat || image.Mipmaps.Length != mipCount)
                throw Invalid("Texture0 array layers require identical dimensions, format, and mip count");
            _ = PublishedStandardLitTextureSettings.Capture(image);
            for (int mip = 0; mip < mipCount; mip++)
            {
                Mipmap2D expected = first.Mipmaps[mip], actual = image.Mipmaps[mip];
                if (actual.Width != expected.Width || actual.Height != expected.Height ||
                    actual.InternalFormat != expected.InternalFormat || actual.PixelFormat != expected.PixelFormat ||
                    actual.PixelType != expected.PixelType)
                    throw Invalid("Texture0 array layers require matching mip dimensions and pixel metadata");
            }
        }
    }

    private bool HasNoArrayState()
        => ArrayFormat == default && LargestMipmapLevel == 0 && SmallestAllowedMipmapLevel == 0 &&
            MinLOD == 0 && MaxLOD == 0 && !AutoGenerateMipmaps;

    private static InvalidDataException Invalid(string reason) => new($"CookedMaterial.UnlitTextureProfileInvalid: {reason}.");
}
