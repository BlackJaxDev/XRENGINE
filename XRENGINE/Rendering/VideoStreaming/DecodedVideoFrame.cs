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
    {
        Width = width;
        Height = height;
        PresentationTimestampTicks = presentationTimestampTicks;
        PixelFormat = pixelFormat;
        PackedData = packedData;
    }

    public int Width { get; }
    public int Height { get; }
    public long PresentationTimestampTicks { get; }
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
