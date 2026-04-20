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
    private int _sparseTextureStreamingResidentBaseMipLevel = int.MaxValue;

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

    [YamlIgnore]
    [MemoryPackIgnore]
    private SparseTextureStreamingPageSelection _sparseTextureStreamingResidentPageSelection = SparseTextureStreamingPageSelection.Full;

    [YamlIgnore]
    [MemoryPackIgnore]
    public SparseTextureStreamingPageSelection SparseTextureStreamingResidentPageSelection
    {
        get => _sparseTextureStreamingResidentPageSelection;
        set => SetField(ref _sparseTextureStreamingResidentPageSelection, value.Normalize());
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
        SparseTextureStreamingResidentBaseMipLevel = int.MaxValue;
        SparseTextureStreamingCommittedBaseMipLevel = int.MaxValue;
        SparseTextureStreamingNumSparseLevels = 0;
        SparseTextureStreamingCommittedBytes = 0L;
        SparseTextureStreamingResidentPageSelection = SparseTextureStreamingPageSelection.Full;
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

    internal static int ResolveSparseCommittedBaseMipLevel(int requestedBaseMipLevel, int numSparseLevels, int logicalMipCount)
    {
        if (logicalMipCount <= 0)
            return 0;

        int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), logicalMipCount);
        if (tailFirstMipLevel >= logicalMipCount)
            return Math.Clamp(requestedBaseMipLevel, 0, logicalMipCount - 1);

        return Math.Min(Math.Clamp(requestedBaseMipLevel, 0, logicalMipCount - 1), tailFirstMipLevel);
    }

    internal static bool TryResolveSparsePageRegion(
        SparseTextureStreamingSupport support,
        SparseTextureStreamingPageSelection selection,
        uint logicalWidth,
        uint logicalHeight,
        int mipLevel,
        out SparseTextureStreamingPageRegion region)
    {
        region = default;
        if (!support.IsAvailable)
            return false;

        uint mipWidth = Math.Max(1u, logicalWidth >> mipLevel);
        uint mipHeight = Math.Max(1u, logicalHeight >> mipLevel);
        SparseTextureStreamingPageSelection normalized = selection.Normalize();
        if (!normalized.IsPartial)
        {
            region = new SparseTextureStreamingPageRegion(mipLevel, 0, 0, mipWidth, mipHeight);
            return true;
        }

        int x0 = Math.Clamp((int)Math.Floor(normalized.MinU * mipWidth), 0, Math.Max(0, (int)mipWidth - 1));
        int y0 = Math.Clamp((int)Math.Floor(normalized.MinV * mipHeight), 0, Math.Max(0, (int)mipHeight - 1));
        int x1 = Math.Clamp((int)Math.Ceiling(normalized.MaxU * mipWidth), x0 + 1, (int)mipWidth);
        int y1 = Math.Clamp((int)Math.Ceiling(normalized.MaxV * mipHeight), y0 + 1, (int)mipHeight);

        uint pageWidth = Math.Max(1u, support.VirtualPageSizeX);
        uint pageHeight = Math.Max(1u, support.VirtualPageSizeY);
        int committedX0 = (int)((uint)x0 / pageWidth * pageWidth);
        int committedY0 = (int)((uint)y0 / pageHeight * pageHeight);
        int committedX1 = (int)Math.Min(mipWidth, AlignUp((uint)x1, pageWidth));
        int committedY1 = (int)Math.Min(mipHeight, AlignUp((uint)y1, pageHeight));
        if (committedX1 <= committedX0 || committedY1 <= committedY0)
            return false;

        region = new SparseTextureStreamingPageRegion(
            mipLevel,
            committedX0,
            committedY0,
            (uint)(committedX1 - committedX0),
            (uint)(committedY1 - committedY0));
        return true;
    }

    internal static long EstimateSparsePageSelectionBytes(
        uint sourceWidth,
        uint sourceHeight,
        int requestedBaseMipLevel,
        int logicalMipCount,
        int numSparseLevels,
        SparseTextureStreamingSupport support,
        SparseTextureStreamingPageSelection selection,
        ESizedInternalFormat format = ESizedInternalFormat.Rgba8)
    {
        if (logicalMipCount <= 0)
            return 0L;

        int committedBaseMipLevel = ResolveSparseCommittedBaseMipLevel(requestedBaseMipLevel, numSparseLevels, logicalMipCount);
        int tailFirstMipLevel = Math.Min(Math.Max(0, numSparseLevels), logicalMipCount);
        int bytesPerPixel = Math.Max(1, RuntimeRenderingHostServices.Current.GetBytesPerPixel(format));
        SparseTextureStreamingPageSelection normalizedSelection = selection.Normalize();
        bool usePartialPages = normalizedSelection.IsPartial && support.IsAvailable && committedBaseMipLevel < tailFirstMipLevel;

        long totalBytes = 0L;
        int individualMipEndExclusive = usePartialPages ? tailFirstMipLevel : logicalMipCount;
        for (int mipLevel = committedBaseMipLevel; mipLevel < individualMipEndExclusive; mipLevel++)
        {
            if (usePartialPages
                && TryResolveSparsePageRegion(support, normalizedSelection, sourceWidth, sourceHeight, mipLevel, out SparseTextureStreamingPageRegion region)
                && region.HasArea)
            {
                totalBytes += (long)region.Width * region.Height * bytesPerPixel;
                continue;
            }

            uint mipWidth = Math.Max(1u, sourceWidth >> mipLevel);
            uint mipHeight = Math.Max(1u, sourceHeight >> mipLevel);
            totalBytes += (long)mipWidth * mipHeight * bytesPerPixel;
        }

        if (!usePartialPages)
            return totalBytes;

        for (int mipLevel = Math.Max(committedBaseMipLevel, tailFirstMipLevel); mipLevel < logicalMipCount; mipLevel++)
        {
            uint mipWidth = Math.Max(1u, sourceWidth >> mipLevel);
            uint mipHeight = Math.Max(1u, sourceHeight >> mipLevel);
            totalBytes += (long)mipWidth * mipHeight * bytesPerPixel;
        }

        return totalBytes;
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

    private static uint AlignUp(uint value, uint alignment)
    {
        if (alignment <= 1u)
            return value;

        uint remainder = value % alignment;
        return remainder == 0u
            ? value
            : value + alignment - remainder;
    }
}
