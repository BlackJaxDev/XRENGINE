using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public readonly record struct SparseTextureStreamingSupport(
    bool SupportsSparseTextures,
    bool SupportsSparseTexture2,
    uint VirtualPageSizeX,
    uint VirtualPageSizeY,
    int VirtualPageSizeIndex,
    string? FailureReason = null)
{
    public bool IsAvailable
        => SupportsSparseTextures
        && VirtualPageSizeX > 0
        && VirtualPageSizeY > 0;

    public bool IsPageAligned(uint width, uint height)
    {
        if (!IsAvailable)
            return false;

        return width > 0
            && height > 0
            && width % VirtualPageSizeX == 0
            && height % VirtualPageSizeY == 0;
    }

    public static SparseTextureStreamingSupport Unsupported(string? reason = null)
        => new(
            SupportsSparseTextures: false,
            SupportsSparseTexture2: false,
            VirtualPageSizeX: 0,
            VirtualPageSizeY: 0,
            VirtualPageSizeIndex: 0,
            FailureReason: reason);
}

public readonly record struct SparseTextureStreamingTransitionRequest(
    uint LogicalWidth,
    uint LogicalHeight,
    ESizedInternalFormat SizedInternalFormat,
    int LogicalMipCount,
    int RequestedBaseMipLevel,
    Mipmap2D[] ResidentMipmaps);

public readonly record struct SparseTextureStreamingTransitionResult(
    bool Applied,
    bool UsedSparseResidency,
    int RequestedBaseMipLevel,
    int CommittedBaseMipLevel,
    int NumSparseLevels,
    long CommittedBytes,
    string? FailureReason = null)
{
    public static SparseTextureStreamingTransitionResult Unsupported(string? reason = null)
        => new(
            Applied: false,
            UsedSparseResidency: false,
            RequestedBaseMipLevel: 0,
            CommittedBaseMipLevel: 0,
            NumSparseLevels: 0,
            CommittedBytes: 0L,
            FailureReason: reason);
}
