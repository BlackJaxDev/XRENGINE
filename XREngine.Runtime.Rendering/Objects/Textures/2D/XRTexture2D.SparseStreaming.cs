using MemoryPack;
using YamlDotNet.Serialization;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    [YamlIgnore]
    [MemoryPackIgnore]
    private bool _sparseTextureStreamingEnabled;

    [YamlIgnore]
    [MemoryPackIgnore]
    public bool SparseTextureStreamingEnabled
    {
        get => _sparseTextureStreamingEnabled;
        set => SetField(ref _sparseTextureStreamingEnabled, value);
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    private uint _sparseTextureStreamingLogicalWidth;

    [YamlIgnore]
    [MemoryPackIgnore]
    public uint SparseTextureStreamingLogicalWidth
    {
        get => _sparseTextureStreamingLogicalWidth;
        set => SetField(ref _sparseTextureStreamingLogicalWidth, value);
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    private uint _sparseTextureStreamingLogicalHeight;

    [YamlIgnore]
    [MemoryPackIgnore]
    public uint SparseTextureStreamingLogicalHeight
    {
        get => _sparseTextureStreamingLogicalHeight;
        set => SetField(ref _sparseTextureStreamingLogicalHeight, value);
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    private int _sparseTextureStreamingLogicalMipCount;

    [YamlIgnore]
    [MemoryPackIgnore]
    public int SparseTextureStreamingLogicalMipCount
    {
        get => _sparseTextureStreamingLogicalMipCount;
        set => SetField(ref _sparseTextureStreamingLogicalMipCount, value);
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    private int _sparseTextureStreamingResidentBaseMipLevel;

    [YamlIgnore]
    [MemoryPackIgnore]
    public int SparseTextureStreamingResidentBaseMipLevel
    {
        get => _sparseTextureStreamingResidentBaseMipLevel;
        set => SetField(ref _sparseTextureStreamingResidentBaseMipLevel, value);
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    private int _sparseTextureStreamingCommittedBaseMipLevel = int.MaxValue;

    [YamlIgnore]
    [MemoryPackIgnore]
    public int SparseTextureStreamingCommittedBaseMipLevel
    {
        get => _sparseTextureStreamingCommittedBaseMipLevel;
        set => SetField(ref _sparseTextureStreamingCommittedBaseMipLevel, value);
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    private int _sparseTextureStreamingNumSparseLevels;

    [YamlIgnore]
    [MemoryPackIgnore]
    public int SparseTextureStreamingNumSparseLevels
    {
        get => _sparseTextureStreamingNumSparseLevels;
        set => SetField(ref _sparseTextureStreamingNumSparseLevels, value);
    }

    [YamlIgnore]
    [MemoryPackIgnore]
    private long _sparseTextureStreamingCommittedBytes;

    [YamlIgnore]
    [MemoryPackIgnore]
    public long SparseTextureStreamingCommittedBytes
    {
        get => _sparseTextureStreamingCommittedBytes;
        set => SetField(ref _sparseTextureStreamingCommittedBytes, value);
    }

    [field: MemoryPackIgnore]
    public event Func<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? SparseTextureStreamingTransitionRequested;

    public SparseTextureStreamingTransitionResult ApplySparseTextureStreamingTransition(SparseTextureStreamingTransitionRequest request)
        => SparseTextureStreamingTransitionRequested?.Invoke(request)
        ?? SparseTextureStreamingTransitionResult.Unsupported("No sparse texture streaming handler is attached to this texture.");

    public void ClearSparseTextureStreamingState()
    {
        SparseTextureStreamingEnabled = false;
        SparseTextureStreamingLogicalWidth = 0;
        SparseTextureStreamingLogicalHeight = 0;
        SparseTextureStreamingLogicalMipCount = 0;
        SparseTextureStreamingResidentBaseMipLevel = 0;
        SparseTextureStreamingCommittedBaseMipLevel = int.MaxValue;
        SparseTextureStreamingNumSparseLevels = 0;
        SparseTextureStreamingCommittedBytes = 0L;
    }

    internal static int GetLogicalMipCount(uint sourceWidth, uint sourceHeight)
        => XRTexture.GetSmallestMipmapLevel(sourceWidth, sourceHeight) + 1;

    internal static int ResolveResidentBaseMipLevel(uint sourceWidth, uint sourceHeight, uint maxResidentDimension)
    {
        uint width = Math.Max(1u, sourceWidth);
        uint height = Math.Max(1u, sourceHeight);
        uint residentMax = Math.Max(1u, maxResidentDimension);
        int mipLevel = 0;

        while (Math.Max(width, height) > residentMax)
        {
            width = Math.Max(1u, width >> 1);
            height = Math.Max(1u, height >> 1);
            mipLevel++;
        }

        return mipLevel;
    }

    internal static long EstimateMipRangeBytes(
        uint sourceWidth,
        uint sourceHeight,
        int baseMipLevel,
        int logicalMipCount,
        ESizedInternalFormat format = ESizedInternalFormat.Rgba8)
    {
        if (logicalMipCount <= 0)
            return 0L;

        int startMip = Math.Clamp(baseMipLevel, 0, logicalMipCount - 1);
        int bytesPerPixel = Math.Max(1, RuntimeRenderingHostServices.Current.GetBytesPerPixel(format));
        long totalBytes = 0L;

        for (int mipLevel = startMip; mipLevel < logicalMipCount; mipLevel++)
        {
            uint mipWidth = Math.Max(1u, sourceWidth >> mipLevel);
            uint mipHeight = Math.Max(1u, sourceHeight >> mipLevel);
            totalBytes += (long)mipWidth * mipHeight * bytesPerPixel;
        }

        return totalBytes;
    }
}
