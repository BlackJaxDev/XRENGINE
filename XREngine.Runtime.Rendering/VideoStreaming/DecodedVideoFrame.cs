using System;

namespace XREngine.Rendering.VideoStreaming;

public readonly struct DecodedVideoFrame
{
    public DecodedVideoFrame(
        int width,
        int height,
        long presentationTimestampTicks,
        VideoPixelFormat pixelFormat,
        ReadOnlyMemory<byte> packedData)
        : this(width, height, presentationTimestampTicks, 0L, pixelFormat, packedData)
    {
    }

    public DecodedVideoFrame(
        int width,
        int height,
        long presentationTimestampTicks,
        long durationTicks,
        VideoPixelFormat pixelFormat,
        ReadOnlyMemory<byte> packedData)
    {
        Width = width;
        Height = height;
        PresentationTimestampTicks = presentationTimestampTicks;
        DurationTicks = durationTicks;
        PixelFormat = pixelFormat;
        PackedData = packedData;
    }

    public int Width { get; }
    public int Height { get; }
    public long PresentationTimestampTicks { get; }

    /// <summary>The time in 100 ns ticks for which the frame shows, or zero if the decoder does not know it.</summary>
    public long DurationTicks { get; }

    public VideoPixelFormat PixelFormat { get; }
    public ReadOnlyMemory<byte> PackedData { get; }
}

public enum VideoPixelFormat
{
    Unknown = 0,
    Rgb24,
    Rgba32,
    Nv12,
    Yuv420P
}
