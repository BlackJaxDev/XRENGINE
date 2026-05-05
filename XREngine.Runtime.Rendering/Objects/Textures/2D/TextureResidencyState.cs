namespace XREngine.Rendering;

/// <summary>
/// Mutable runtime residency state for an <see cref="XRTexture2D"/>.
/// Thread-safety: owned by the texture/backend transition path; callers should mutate
/// through XRTexture2D properties so XRBase change notifications are preserved.
/// </summary>
internal sealed class TextureResidencyState
{
    internal bool SparseEnabled;
    internal uint LogicalWidth;
    internal uint LogicalHeight;
    internal int LogicalMipCount;
    internal int ResidentBaseMipLevel = int.MaxValue;
    internal int CommittedBaseMipLevel = int.MaxValue;
    internal int SparseLevelCount;
    internal long CommittedBytes;
    internal SparseTextureStreamingPageSelection ResidentPageSelection = SparseTextureStreamingPageSelection.Full;
}
