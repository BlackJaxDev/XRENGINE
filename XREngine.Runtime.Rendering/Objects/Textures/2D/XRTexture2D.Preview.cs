namespace XREngine.Rendering;

public partial class XRTexture2D
{
    /// <summary>Publishes one cooked preview mip and its sampling values without decoding the source image.</summary>
    internal static bool TryApplyTexturePreviewPayload(ReadOnlySpan<byte> payload, XRTexture2D target, uint maxPreviewSize)
    {
        if (!TryReadResidentDataFromTextureAssetFileBytes(payload, maxPreviewSize, includeMipChain: false,
                out TextureStreamingResidentData residentData, out TextureSamplingSettings sampling))
            return false;

        if (residentData.Mipmaps is not { Length: 1 }
            || residentData.Mipmaps[0] is not { Width: > 0, Height: > 0 } mip
            || !mip.HasData())
        {
            foreach (Mipmap2D? invalidMip in residentData.Mipmaps ?? [])
                invalidMip?.Data?.Dispose();
            return false;
        }

        ApplyResidentData(target, residentData, includeMipChain: false, previewSampling: sampling);
        return true;
    }
}
