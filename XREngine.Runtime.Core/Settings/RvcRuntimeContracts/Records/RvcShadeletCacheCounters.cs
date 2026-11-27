namespace XREngine;

/// <summary>
/// GPU-measured shadelet cache counters for one RVC frame. Decoded from the
/// 128-byte indirect dispatch and counter buffer through delayed diagnostic readback.
/// </summary>
public readonly record struct RvcShadeletCacheCounters(
    ulong FrameId,
    uint TotalAllocated,
    uint TotalStandard,
    uint TotalUber,
    uint TotalCoveredPixels,
    uint IntraViewReusedPixels,
    uint StereoReusedPixels,
    uint DirectFallbackPixels,
    uint OverflowCount,
    uint StereoCandidates,
    uint StereoRejected)
{
    /// <summary>Number of 32-bit words in the GPU dispatch-and-counter buffer.</summary>
    public const int GpuWordCount = 32;

    /// <summary>Byte size of the GPU dispatch-and-counter buffer.</summary>
    public const uint GpuByteCount = GpuWordCount * sizeof(uint);

    /// <summary>Word offset of <see cref="TotalAllocated"/> (after 3 uvec4 indirect dispatch blocks and 2 slice counts).</summary>
    public const int TotalAllocatedWord = 14;
    public const int TotalStandardWord = 15;
    public const int TotalUberWord = 16;
    public const int TotalCoveredPixelsWord = 17;
    public const int IntraViewReusedPixelsWord = 18;
    public const int StereoReusedPixelsWord = 19;
    public const int DirectFallbackPixelsWord = 20;
    public const int OverflowCountWord = 21;
    public const int StereoCandidatesWord = 22;
    public const int StereoRejectedWord = 23;

    /// <summary>Fraction of covered pixels satisfied without allocating a unique shadelet.</summary>
    public double ShadeletReductionRatio
        => TotalCoveredPixels == 0u ? 0.0 : 1.0 - (double)TotalAllocated / TotalCoveredPixels;

    /// <summary>Fraction of covered pixels satisfied by intra-view block deduplication.</summary>
    public double IntraViewReuseRatio
        => TotalCoveredPixels == 0u ? 0.0 : (double)IntraViewReusedPixels / TotalCoveredPixels;

    /// <summary>Fraction of stereo reprojection candidates validated for cross-eye reuse.</summary>
    public double StereoReuseRatio
        => StereoCandidates == 0u ? 0.0 : (double)StereoReusedPixels / StereoCandidates;

    /// <summary>Decodes the 32-word GPU state buffer into <see cref="RvcShadeletCacheCounters"/>.</summary>
    public static bool TryDecode(ReadOnlySpan<uint> words, ulong frameId, out RvcShadeletCacheCounters counters)
    {
        if (words.Length < GpuWordCount)
        {
            counters = default;
            return false;
        }

        counters = new RvcShadeletCacheCounters(
            frameId,
            words[TotalAllocatedWord],
            words[TotalStandardWord],
            words[TotalUberWord],
            words[TotalCoveredPixelsWord],
            words[IntraViewReusedPixelsWord],
            words[StereoReusedPixelsWord],
            words[DirectFallbackPixelsWord],
            words[OverflowCountWord],
            words[StereoCandidatesWord],
            words[StereoRejectedWord]);
        return true;
    }
}
