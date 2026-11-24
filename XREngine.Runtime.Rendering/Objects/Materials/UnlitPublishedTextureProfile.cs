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
                Images = [PublishedTextureArrayProfile.CaptureImage(image, 0, direct: true)],
            };
        }
        if (texture.GetType() != typeof(XRTexture2DArray))
            throw Invalid("Texture0 must be an exact XRTexture2D or XRTexture2DArray");

        XRTexture2DArray array = (XRTexture2DArray)texture;
        PublishedTextureArrayProfile layers = PublishedTextureArrayProfile.Capture(array);
        return new()
        {
            SourceTexture = array,
            SourceTextureId = array.ID,
            Images = layers.Images,
            LayerRoles = layers.LayerRoles,
            ArrayFormat = layers.ArrayFormat,
            LargestMipmapLevel = layers.LargestMipmapLevel,
            SmallestAllowedMipmapLevel = layers.SmallestAllowedMipmapLevel,
            MinLOD = layers.MinLOD,
            MaxLOD = layers.MaxLOD,
            AutoGenerateMipmaps = layers.AutoGenerateMipmaps,
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
            PublishedTextureArrayProfile.ValidateImage(Images[0], image);
            PublishedTextureArrayProfile.RestoreImage(Images[0], image);
            return;
        }
        if (SourceTexture.GetType() != typeof(XRTexture2DArray))
            throw Invalid("Texture0 has an unsupported image type");

        // The shared array profile validates the complete role graph before it restores any setting.
        new PublishedTextureArrayProfile
        {
            ArrayId = SourceTextureId,
            ArrayFormat = ArrayFormat,
            Images = Images,
            LayerRoles = LayerRoles,
            LargestMipmapLevel = LargestMipmapLevel,
            SmallestAllowedMipmapLevel = SmallestAllowedMipmapLevel,
            MinLOD = MinLOD,
            MaxLOD = MaxLOD,
            AutoGenerateMipmaps = AutoGenerateMipmaps,
        }.Restore((XRTexture2DArray)SourceTexture);
    }

    private bool HasNoArrayState()
        => ArrayFormat == default && LargestMipmapLevel == 0 && SmallestAllowedMipmapLevel == 0 &&
            MinLOD == 0 && MaxLOD == 0 && !AutoGenerateMipmaps;

    private static InvalidDataException Invalid(string reason) => new($"CookedMaterial.UnlitTextureProfileInvalid: {reason}.");
}
