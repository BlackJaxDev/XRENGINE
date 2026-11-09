namespace XREngine;

/// <summary>
/// GPU-measured shadelet reuse ceilings for one RVC frame. The telemetry kernel reads the
/// Advanced visibility buffer of every view and does not change shading. It counts two key
/// levels. The primitive key (draw handle and primitive) gives loose upper bounds. The
/// surface key adds the world-space cell of the surface point; the cell edge is the
/// footprint of N pixels at the point's distance from the head, so it models a shadelet of
/// N x N pixels.
/// </summary>
/// <param name="FrameId">Render frame that produced the counters.</param>
/// <param name="ViewCount">Number of views that the kernel measured.</param>
/// <param name="ValidPixels">Pixels with a valid visibility identity, summed over all views.</param>
/// <param name="UniqueShadelets2x2">Unique keys in each 2x2 pixel block, summed over all blocks and views.</param>
/// <param name="UniqueShadelets4x4">Unique keys in each 4x4 pixel block, summed over all blocks and views.</param>
/// <param name="UniqueShadelets8x8">Unique keys in each 8x8 pixel block, summed over all blocks and views.</param>
/// <param name="StereoCandidates">First-view pixels that project inside the second view.</param>
/// <param name="StereoOffscreen">First-view pixels that project outside the second view.</param>
/// <param name="StereoVisibleInBothEyes">Candidates whose surface point is also the visible surface in the second view.</param>
/// <param name="StereoKeyMatches">Visible-in-both candidates that also have the same key in both views.</param>
/// <param name="InsetWideKeyMatches">Inset pixels whose key matches the wide view of the same eye. Zero until quad view exists.</param>
/// <param name="UniqueSurfaceShadelets2x2">Unique surface keys (2-pixel cells) in each 2x2 block.</param>
/// <param name="UniqueSurfaceShadelets4x4">Unique surface keys (4-pixel cells) in each 4x4 block.</param>
/// <param name="UniqueSurfaceShadelets8x8">Unique surface keys (8-pixel cells) in each 8x8 block.</param>
/// <param name="StereoSurfaceKeyMatches">Primitive key matches whose second-view sample is in the same one-pixel surface cell.</param>
public readonly record struct RvcShadeletTelemetryCounters(
    ulong FrameId,
    uint ViewCount,
    ulong ValidPixels,
    ulong UniqueShadelets2x2,
    ulong UniqueShadelets4x4,
    ulong UniqueShadelets8x8,
    ulong StereoCandidates,
    ulong StereoOffscreen,
    ulong StereoVisibleInBothEyes,
    ulong StereoKeyMatches,
    ulong InsetWideKeyMatches,
    ulong UniqueSurfaceShadelets2x2,
    ulong UniqueSurfaceShadelets4x4,
    ulong UniqueSurfaceShadelets8x8,
    ulong StereoSurfaceKeyMatches)
{
    /// <summary>Number of 32-bit words in the GPU counter record.</summary>
    public const int GpuWordCount = 16;

    /// <summary>Byte size of the GPU counter record.</summary>
    public const uint GpuByteCount = GpuWordCount * sizeof(uint);

    /// <summary>Word index of the valid-pixel count for the first view.</summary>
    public const int ValidPixelsView0Word = 0;

    /// <summary>Word index of the valid-pixel count for the second view.</summary>
    public const int ValidPixelsView1Word = 1;

    /// <summary>Word index of the 2x2 unique-key count.</summary>
    public const int Unique2x2Word = 2;

    /// <summary>Word index of the 4x4 unique-key count.</summary>
    public const int Unique4x4Word = 3;

    /// <summary>Word index of the 8x8 unique-key count.</summary>
    public const int Unique8x8Word = 4;

    /// <summary>Word index of the stereo candidate count.</summary>
    public const int StereoCandidatesWord = 5;

    /// <summary>Word index of the stereo off-screen count.</summary>
    public const int StereoOffscreenWord = 6;

    /// <summary>Word index of the visible-in-both-eyes count.</summary>
    public const int StereoVisibleWord = 7;

    /// <summary>Word index of the stereo key-match count.</summary>
    public const int StereoKeyMatchesWord = 8;

    /// <summary>Word index of the measured view count.</summary>
    public const int ViewCountWord = 9;

    /// <summary>Word index of the 2x2 unique surface-key count.</summary>
    public const int UniqueSurface2x2Word = 10;

    /// <summary>Word index of the 4x4 unique surface-key count.</summary>
    public const int UniqueSurface4x4Word = 11;

    /// <summary>Word index of the 8x8 unique surface-key count.</summary>
    public const int UniqueSurface8x8Word = 12;

    /// <summary>Word index of the stereo surface-key match count.</summary>
    public const int StereoSurfaceKeyMatchesWord = 13;

    /// <summary>Fraction of shading work that 2x2 shadelets remove inside one view.</summary>
    public double IntraView2x2ReuseRatio => ReuseRatio(UniqueShadelets2x2);

    /// <summary>Fraction of shading work that 4x4 shadelets remove inside one view.</summary>
    public double IntraView4x4ReuseRatio => ReuseRatio(UniqueShadelets4x4);

    /// <summary>Fraction of shading work that 8x8 shadelets remove inside one view.</summary>
    public double IntraView8x8ReuseRatio => ReuseRatio(UniqueShadelets8x8);

    /// <summary>Surface-key form of <see cref="IntraView2x2ReuseRatio"/>.</summary>
    public double IntraViewSurface2x2ReuseRatio => ReuseRatio(UniqueSurfaceShadelets2x2);

    /// <summary>Surface-key form of <see cref="IntraView4x4ReuseRatio"/>.</summary>
    public double IntraViewSurface4x4ReuseRatio => ReuseRatio(UniqueSurfaceShadelets4x4);

    /// <summary>Surface-key form of <see cref="IntraView8x8ReuseRatio"/>.</summary>
    public double IntraViewSurface8x8ReuseRatio => ReuseRatio(UniqueSurfaceShadelets8x8);

    /// <summary>Fraction of on-screen first-view pixels whose primitive key matches in the second eye.</summary>
    public double StereoKeyMatchRatio
        => StereoCandidates == 0u ? 0.0 : (double)StereoKeyMatches / StereoCandidates;

    /// <summary>Fraction of on-screen first-view pixels whose one-pixel surface key matches in the second eye.</summary>
    public double StereoSurfaceKeyMatchRatio
        => StereoCandidates == 0u ? 0.0 : (double)StereoSurfaceKeyMatches / StereoCandidates;

    /// <summary>
    /// Decodes the GPU counter record. Returns false when the record is too short.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<uint> words, ulong frameId, out RvcShadeletTelemetryCounters counters)
    {
        if (words.Length < GpuWordCount)
        {
            counters = default;
            return false;
        }

        counters = new RvcShadeletTelemetryCounters(
            frameId,
            words[ViewCountWord],
            (ulong)words[ValidPixelsView0Word] + words[ValidPixelsView1Word],
            words[Unique2x2Word],
            words[Unique4x4Word],
            words[Unique8x8Word],
            words[StereoCandidatesWord],
            words[StereoOffscreenWord],
            words[StereoVisibleWord],
            words[StereoKeyMatchesWord],
            InsetWideKeyMatches: 0u,
            words[UniqueSurface2x2Word],
            words[UniqueSurface4x4Word],
            words[UniqueSurface8x8Word],
            words[StereoSurfaceKeyMatchesWord]);
        return true;
    }

    private double ReuseRatio(ulong unique)
        => ValidPixels == 0u ? 0.0 : 1.0 - (double)unique / ValidPixels;
}
