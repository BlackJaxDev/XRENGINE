using XREngine.Core.Files;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Authored sampled-image settings absent from the general texture payload. The bounded
/// material carrier preserves these without changing desktop or standalone texture formats.
/// </summary>
public readonly record struct PublishedStandardLitTextureSettings(
    float MaxAnisotropy,
    bool EnableComparison,
    ETextureCompareFunc CompareFunc,
    ETextureColorSpace ImportedColorSpace,
    ETextureImportUsage ImportedUsage,
    bool ImportedNormalMapFlipGreen,
    bool RequiresStorageUsage)
{
    public const int SerializedSize = sizeof(float) + 3 * sizeof(int) + 3 * sizeof(bool);

    /// <summary>Captures only static, CPU-backed authored images; live GPU producers cannot be cooked as pixels.</summary>
    public static PublishedStandardLitTextureSettings Capture(XRTexture2D texture)
    {
        if (texture.GetType() != typeof(XRTexture2D) || texture.GrabPass is not null || texture.IsGpuWritable ||
            texture.OpenGlExternalMemoryImportHandle != 0 || texture.OpenGlExternalMemoryImportSize != 0 ||
            texture.OpenGlExternalMemoryImportMipLevels != 1 || texture.OpenGlExternalMemoryLabel is not null ||
            texture.SparseTextureStreamingEnabled || texture.SparseTextureStreamingLogicalWidth != 0 ||
            texture.SparseTextureStreamingLogicalHeight != 0 || texture.SparseTextureStreamingLogicalMipCount != 0 ||
            texture.SparseTextureStreamingResidentBaseMipLevel != int.MaxValue ||
            texture.SparseTextureStreamingCommittedBaseMipLevel != int.MaxValue ||
            texture.SparseTextureStreamingNumSparseLevels != 0 || texture.SparseTextureStreamingCommittedBytes != 0 ||
            texture.SparseTextureStreamingResidentPageSelection != SparseTextureStreamingPageSelection.Full ||
            texture.ShouldLoadDataFromInternalPBO || texture.RuntimeManagedProgressiveUploadActive ||
            texture.RuntimeManagedProgressiveFinalizePending || texture.StreamingLockMipLevel >= 0)
            throw Invalid("only exact static CPU-backed XRTexture2D images without grab, external, sparse or active upload state are supported");
        if (texture.Mipmaps.Length == 0)
            throw Invalid("a static image requires at least one CPU-backed mip");
        foreach (Mipmap2D? mip in texture.Mipmaps)
            if (mip is null || mip.Width == 0 || mip.Height == 0 || mip.Data is null || mip.Data.Length == 0 || mip.StreamingPBO is not null)
                throw Invalid("every mip requires dimensions and resident CPU pixel data without a streaming upload buffer");
        PublishedStandardLitTextureSettings settings = new(texture.MaxAnisotropy, texture.EnableComparison,
            texture.CompareFunc, texture.ImportedColorSpace, texture.ImportedUsage,
            texture.ImportedNormalMapFlipGreen, texture.RequiresStorageUsage);
        settings.Validate();
        return settings;
    }

    public void Write(CookedBinaryWriter writer)
    {
        Validate();
        writer.Write(MaxAnisotropy);
        writer.Write(EnableComparison);
        writer.Write((int)CompareFunc);
        writer.Write((int)ImportedColorSpace);
        writer.Write((int)ImportedUsage);
        writer.Write(ImportedNormalMapFlipGreen);
        writer.Write(RequiresStorageUsage);
    }

    public static PublishedStandardLitTextureSettings Read(CookedBinaryReader reader)
    {
        PublishedStandardLitTextureSettings settings = new(reader.ReadSingle(), reader.ReadBoolean(),
            (ETextureCompareFunc)reader.ReadInt32(), (ETextureColorSpace)reader.ReadInt32(),
            (ETextureImportUsage)reader.ReadInt32(), reader.ReadBoolean(), reader.ReadBoolean());
        settings.Validate();
        return settings;
    }

    /// <summary>Restores settings only onto the carrier's newly decoded image, never a borrowed projected image.</summary>
    public void ApplyTo(XRTexture2D texture)
    {
        Validate();
        texture.MaxAnisotropy = MaxAnisotropy;
        texture.EnableComparison = EnableComparison;
        texture.CompareFunc = CompareFunc;
        texture.ImportedColorSpace = ImportedColorSpace;
        texture.ImportedUsage = ImportedUsage;
        texture.ImportedNormalMapFlipGreen = ImportedNormalMapFlipGreen;
        texture.RequiresStorageUsage = RequiresStorageUsage;
    }

    private void Validate()
    {
        if (!float.IsFinite(MaxAnisotropy) || MaxAnisotropy < 1 || MaxAnisotropy > 16 || MaxAnisotropy != MathF.Truncate(MaxAnisotropy) ||
            !Enum.IsDefined(CompareFunc) || !Enum.IsDefined(ImportedColorSpace) || !Enum.IsDefined(ImportedUsage))
            throw Invalid("anisotropy must be an integer from one through sixteen and sampler/import enums must be defined");
    }

    private static InvalidDataException Invalid(string reason)
        => new($"CookedMaterial.TextureSettingsUnsupported: {reason}.");
}
