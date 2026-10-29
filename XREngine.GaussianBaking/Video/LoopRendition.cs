namespace XREngine.Rendering.Models.Gaussian.Baking.Video;

/// <summary>
/// Describes one output file of a preview loop: a video or a poster.
/// </summary>
/// <param name="Height">The height of the output, in pixels. A height below the source frames scales the frames down.</param>
/// <param name="Codec">The codec: <see cref="Vp9Codec"/>, <see cref="H264Codec"/>, or <see cref="WebpCodec"/>.</param>
/// <param name="FileName">The name of the file in the output folder.</param>
public readonly record struct LoopRendition(int Height, string Codec, string FileName)
{
    /// <summary>The VP9 video codec, in a WebM file.</summary>
    public const string Vp9Codec = "vp9";

    /// <summary>The H.264 video codec, in an MP4 file.</summary>
    public const string H264Codec = "h264";

    /// <summary>The WebP image codec, for a poster.</summary>
    public const string WebpCodec = "webp";

    /// <summary>The 1080p VP9 loop.</summary>
    public static readonly LoopRendition Loop1080Vp9 = new(1080, Vp9Codec, "loop-1080.webm");

    /// <summary>The 1080p H.264 loop.</summary>
    public static readonly LoopRendition Loop1080H264 = new(1080, H264Codec, "loop-1080.mp4");

    /// <summary>The 480p VP9 loop.</summary>
    public static readonly LoopRendition Loop480Vp9 = new(480, Vp9Codec, "loop-480.webm");

    /// <summary>The 480p H.264 loop.</summary>
    public static readonly LoopRendition Loop480H264 = new(480, H264Codec, "loop-480.mp4");

    /// <summary>The 1080p poster. It shows frame 0 of the loop.</summary>
    public static readonly LoopRendition Poster1080 = new(1080, WebpCodec, "poster-1080.webp");

    /// <summary>The 480p poster. It shows frame 0 of the loop.</summary>
    public static readonly LoopRendition Poster480 = new(480, WebpCodec, "poster-480.webp");

    /// <summary>All outputs of a preview loop: the four videos, then the two posters.</summary>
    public static IReadOnlyList<LoopRendition> All { get; } =
        [Loop1080Vp9, Loop1080H264, Loop480Vp9, Loop480H264, Poster1080, Poster480];

    /// <summary>True for a poster, which holds only one frame.</summary>
    public bool IsPoster => string.Equals(Codec, WebpCodec, StringComparison.Ordinal);
}
