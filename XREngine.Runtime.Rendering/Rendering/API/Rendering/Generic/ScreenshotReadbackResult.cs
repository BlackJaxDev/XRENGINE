using ImageMagick;

namespace XREngine.Rendering;

/// <summary>
/// Completion payload for an asynchronous renderer screenshot readback.
/// </summary>
/// <remarks>
/// A successful callback transfers ownership of <see cref="Image"/> to the callback.
/// Consumers must dispose it after encoding or inspection.
/// </remarks>
public sealed class ScreenshotReadbackResult
{
    private ScreenshotReadbackResult(
        MagickImage? image,
        int pixelCount,
        int width,
        int height,
        string backend,
        string? sourceFormat,
        long rawByteCount,
        bool usedMultisampleResolve,
        int? queueSlot,
        DateTimeOffset submittedAtUtc,
        DateTimeOffset completedAtUtc,
        double? gpuCompletionSeconds,
        double? cpuProcessingSeconds,
        string? error)
    {
        Image = image;
        PixelCount = pixelCount;
        Width = width;
        Height = height;
        Backend = backend;
        SourceFormat = sourceFormat;
        RawByteCount = rawByteCount;
        UsedMultisampleResolve = usedMultisampleResolve;
        QueueSlot = queueSlot;
        SubmittedAtUtc = submittedAtUtc;
        CompletedAtUtc = completedAtUtc;
        GpuCompletionSeconds = gpuCompletionSeconds;
        CpuProcessingSeconds = cpuProcessingSeconds;
        Error = error;
    }

    public MagickImage? Image { get; }
    public int PixelCount { get; }
    public int Width { get; }
    public int Height { get; }
    public string Backend { get; }
    public string? SourceFormat { get; }
    public long RawByteCount { get; }
    public bool UsedMultisampleResolve { get; }
    public int? QueueSlot { get; }
    public DateTimeOffset SubmittedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public double? GpuCompletionSeconds { get; }
    public double? CpuProcessingSeconds { get; }
    public string? Error { get; }
    public bool Succeeded => Image is not null && string.IsNullOrWhiteSpace(Error);

    public static ScreenshotReadbackResult Success(
        MagickImage image,
        int pixelCount,
        int width,
        int height,
        string backend,
        string? sourceFormat = null,
        long rawByteCount = 0,
        bool usedMultisampleResolve = false,
        int? queueSlot = null,
        DateTimeOffset? submittedAtUtc = null,
        DateTimeOffset? completedAtUtc = null,
        double? gpuCompletionSeconds = null,
        double? cpuProcessingSeconds = null)
        => new(
            image,
            pixelCount,
            width,
            height,
            backend,
            sourceFormat,
            rawByteCount,
            usedMultisampleResolve,
            queueSlot,
            submittedAtUtc ?? DateTimeOffset.UtcNow,
            completedAtUtc ?? DateTimeOffset.UtcNow,
            gpuCompletionSeconds,
            cpuProcessingSeconds,
            error: null);

    public static ScreenshotReadbackResult Failure(
        string error,
        string backend,
        int width = 0,
        int height = 0,
        string? sourceFormat = null,
        long rawByteCount = 0,
        bool usedMultisampleResolve = false,
        int? queueSlot = null,
        DateTimeOffset? submittedAtUtc = null,
        DateTimeOffset? completedAtUtc = null,
        double? gpuCompletionSeconds = null)
        => new(
            image: null,
            pixelCount: 0,
            width,
            height,
            backend,
            sourceFormat,
            rawByteCount,
            usedMultisampleResolve,
            queueSlot,
            submittedAtUtc ?? DateTimeOffset.UtcNow,
            completedAtUtc ?? DateTimeOffset.UtcNow,
            gpuCompletionSeconds,
            cpuProcessingSeconds: null,
            error);
}
