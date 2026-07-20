using System.Security.Cryptography;
using ImageMagick;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Produces bounded, LLM-friendly image statistics for captured viewport frames.
/// </summary>
internal static class ViewportSequenceCaptureImageAnalyzer
{
    private const uint AnalysisWidth = 64;
    private const uint AnalysisHeight = 64;
    private const byte BlackThreshold = 5;
    private const byte ChangedChannelThreshold = 4;

    public static void Analyze(IReadOnlyList<ViewportSequenceCaptureFrame> frames)
    {
        byte[]? previousPixels = null;

        foreach (ViewportSequenceCaptureFrame frame in frames)
        {
            if (!frame.Succeeded || !File.Exists(frame.Path))
                continue;

            frame.ContentSha256 = ComputeSha256(frame.Path);

            using MagickImage image = new(frame.Path);
            image.Depth = 8;
            image.Resize(AnalysisWidth, AnalysisHeight);
            byte[] pixels = image.ToByteArray(MagickFormat.Rgba);

            PopulateImageStatistics(frame, pixels);
            if (previousPixels is not null && previousPixels.Length == pixels.Length)
                frame.DifferenceFromPrevious = CalculateDifference(previousPixels, pixels);

            previousPixels = pixels;
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void PopulateImageStatistics(ViewportSequenceCaptureFrame frame, byte[] pixels)
    {
        if (pixels.Length < 4)
        {
            frame.MeanLuminance = 0.0;
            frame.BlackPixelRatio = 1.0;
            return;
        }

        int pixelCount = pixels.Length / 4;
        double luminanceSum = 0.0;
        int blackPixels = 0;

        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            byte r = pixels[i];
            byte g = pixels[i + 1];
            byte b = pixels[i + 2];
            luminanceSum += (0.2126 * r + 0.7152 * g + 0.0722 * b) / byte.MaxValue;
            if (r <= BlackThreshold && g <= BlackThreshold && b <= BlackThreshold)
                blackPixels++;
        }

        frame.MeanLuminance = luminanceSum / pixelCount;
        frame.BlackPixelRatio = blackPixels / (double)pixelCount;
    }

    private static ViewportSequenceCaptureDifference CalculateDifference(byte[] previous, byte[] current)
    {
        long absoluteDeltaSum = 0L;
        long squaredDeltaSum = 0L;
        int maximumDelta = 0;
        int changedPixels = 0;
        int pixelCount = current.Length / 4;

        for (int i = 0; i + 3 < current.Length; i += 4)
        {
            bool pixelChanged = false;
            for (int channel = 0; channel < 3; channel++)
            {
                int delta = Math.Abs(current[i + channel] - previous[i + channel]);
                absoluteDeltaSum += delta;
                squaredDeltaSum += (long)delta * delta;
                maximumDelta = Math.Max(maximumDelta, delta);
                pixelChanged |= delta >= ChangedChannelThreshold;
            }

            if (pixelChanged)
                changedPixels++;
        }

        int channelCount = Math.Max(1, pixelCount * 3);
        double normalizedMeanAbsoluteError = absoluteDeltaSum / (channelCount * (double)byte.MaxValue);
        double normalizedRootMeanSquareError = Math.Sqrt(squaredDeltaSum / (double)channelCount) / byte.MaxValue;

        return new ViewportSequenceCaptureDifference
        {
            MeanAbsoluteError = normalizedMeanAbsoluteError,
            RootMeanSquareError = normalizedRootMeanSquareError,
            ChangedPixelRatio = changedPixels / (double)Math.Max(1, pixelCount),
            MaximumChannelDelta = maximumDelta / (double)byte.MaxValue,
            Identical = absoluteDeltaSum == 0L,
        };
    }
}
