using System;
using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Allocation-free measurements used by deterministic stereo render validation.
/// Inputs are tightly packed linear RGBA float pixels, one eye at a time.
/// </summary>
public static class StereoRenderedOutputMetrics
{
    public readonly record struct BloomMetrics(double LuminanceEnergy, Vector2 NormalizedCentroid);
    public readonly record struct VelocityMetrics(Vector2 MeanVector, float MeanMagnitude, float MaxMagnitude, int NonZeroSampleCount);
    public readonly record struct EdgeSharpnessMetrics(float MeanGradient, float MaxGradient);

    public static RenderedOutputCaptureMetrics MeasureCapture(
        ReadOnlySpan<float> rgba,
        int width,
        int height,
        int topBandRows = 111,
        float nonBlackEpsilon = 1.0e-5f)
    {
        ValidateRgbaInput(rgba, width, height);
        if (topBandRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(topBandRows));
        if (nonBlackEpsilon < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(nonBlackEpsilon));

        BloomMetrics bloom = MeasureBloom(rgba, width, height);
        VelocityMetrics velocity = MeasureVelocity(rgba, width, height);
        EdgeSharpnessMetrics sharpness = MeasureEdgeSharpness(rgba, width, height);
        int clampedTopRows = Math.Min(topBandRows, height);
        int nonBlackPixels = 0;
        int topBandNonBlackPixels = 0;
        int topBandMagentaPixelCount = 0;
        double maximumLuminance = 0.0;
        double topBandMaximumLuminance = 0.0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                double luminance = Math.Max(0.0, Luminance(rgba, offset));
                maximumLuminance = Math.Max(maximumLuminance, luminance);
                if (luminance > nonBlackEpsilon)
                {
                    nonBlackPixels++;
                    if (y < clampedTopRows)
                        topBandNonBlackPixels++;
                }
                if (y < clampedTopRows)
                {
                    topBandMaximumLuminance = Math.Max(topBandMaximumLuminance, luminance);
                    float red = rgba[offset];
                    float green = rgba[offset + 1];
                    float blue = rgba[offset + 2];
                    if (red > 0.20f && blue > 0.15f &&
                        red > green * 1.35f && blue > green * 1.20f)
                    {
                        topBandMagentaPixelCount++;
                    }
                }
            }
        }

        int pixelCount = checked(width * height);
        int topBandPixelCount = checked(width * clampedTopRows);
        return new RenderedOutputCaptureMetrics
        {
            Width = width,
            Height = height,
            PixelCount = pixelCount,
            NonBlackPixelCount = nonBlackPixels,
            NonBlackPixelRatio = (double)nonBlackPixels / pixelCount,
            MaximumLuminance = maximumLuminance,
            LuminanceEnergy = bloom.LuminanceEnergy,
            BloomCentroidX = bloom.NormalizedCentroid.X,
            BloomCentroidY = bloom.NormalizedCentroid.Y,
            VelocityMeanX = velocity.MeanVector.X,
            VelocityMeanY = velocity.MeanVector.Y,
            VelocityMeanMagnitude = velocity.MeanMagnitude,
            VelocityMaxMagnitude = velocity.MaxMagnitude,
            VelocityNonZeroSampleCount = velocity.NonZeroSampleCount,
            EdgeMeanGradient = sharpness.MeanGradient,
            EdgeMaxGradient = sharpness.MaxGradient,
            TopBandRows = clampedTopRows,
            TopBandNonBlackPixelCount = topBandNonBlackPixels,
            TopBandNonBlackPixelRatio = topBandPixelCount > 0
                ? (double)topBandNonBlackPixels / topBandPixelCount
                : 0.0,
            TopBandMaximumLuminance = topBandMaximumLuminance,
            TopBandMagentaPixelCount = topBandMagentaPixelCount,
            LuminanceFingerprintWidth = 16,
            LuminanceFingerprintHeight = 16,
            LuminanceFingerprint = MeasureLuminanceFingerprint(rgba, width, height, 16, 16),
            VelocityMagnitudeFingerprintWidth = 16,
            VelocityMagnitudeFingerprintHeight = 16,
            VelocityMagnitudeFingerprint = MeasureVelocityMagnitudeFingerprint(rgba, width, height, 16, 16),
        };
    }

    private static double[] MeasureLuminanceFingerprint(
        ReadOnlySpan<float> rgba,
        int width,
        int height,
        int fingerprintWidth,
        int fingerprintHeight)
    {
        var fingerprint = new double[checked(fingerprintWidth * fingerprintHeight)];
        for (int fingerprintY = 0; fingerprintY < fingerprintHeight; fingerprintY++)
        {
            int startY = (fingerprintY * height) / fingerprintHeight;
            int endY = Math.Max(startY + 1, ((fingerprintY + 1) * height) / fingerprintHeight);
            for (int fingerprintX = 0; fingerprintX < fingerprintWidth; fingerprintX++)
            {
                int startX = (fingerprintX * width) / fingerprintWidth;
                int endX = Math.Max(startX + 1, ((fingerprintX + 1) * width) / fingerprintWidth);
                double sum = 0.0;
                int count = 0;
                for (int y = startY; y < Math.Min(endY, height); y++)
                {
                    for (int x = startX; x < Math.Min(endX, width); x++)
                    {
                        sum += Math.Max(0.0, Luminance(rgba, ((y * width) + x) * 4));
                        count++;
                    }
                }
                fingerprint[(fingerprintY * fingerprintWidth) + fingerprintX] = count > 0
                    ? sum / count
                    : 0.0;
            }
        }
        return fingerprint;
    }

    private static double[] MeasureVelocityMagnitudeFingerprint(
        ReadOnlySpan<float> rgba,
        int width,
        int height,
        int fingerprintWidth,
        int fingerprintHeight)
    {
        var fingerprint = new double[checked(fingerprintWidth * fingerprintHeight)];
        for (int fingerprintY = 0; fingerprintY < fingerprintHeight; fingerprintY++)
        {
            int startY = (fingerprintY * height) / fingerprintHeight;
            int endY = Math.Max(startY + 1, ((fingerprintY + 1) * height) / fingerprintHeight);
            for (int fingerprintX = 0; fingerprintX < fingerprintWidth; fingerprintX++)
            {
                int startX = (fingerprintX * width) / fingerprintWidth;
                int endX = Math.Max(startX + 1, ((fingerprintX + 1) * width) / fingerprintWidth);
                double sum = 0.0;
                int count = 0;
                for (int y = startY; y < Math.Min(endY, height); y++)
                {
                    for (int x = startX; x < Math.Min(endX, width); x++)
                    {
                        int offset = ((y * width) + x) * 4;
                        double velocityX = rgba[offset];
                        double velocityY = rgba[offset + 1];
                        sum += Math.Sqrt((velocityX * velocityX) + (velocityY * velocityY));
                        count++;
                    }
                }
                fingerprint[(fingerprintY * fingerprintWidth) + fingerprintX] = count > 0
                    ? sum / count
                    : 0.0;
            }
        }
        return fingerprint;
    }

    public static BloomMetrics MeasureBloom(ReadOnlySpan<float> rgba, int width, int height)
    {
        ValidateRgbaInput(rgba, width, height);

        double energy = 0.0;
        double weightedX = 0.0;
        double weightedY = 0.0;
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            double normalizedY = (y + 0.5) / height;
            for (int x = 0; x < width; x++, offset += 4)
            {
                double luminance = Math.Max(0.0, Luminance(rgba, offset));
                energy += luminance;
                weightedX += luminance * ((x + 0.5) / width);
                weightedY += luminance * normalizedY;
            }
        }

        Vector2 centroid = energy > double.Epsilon
            ? new Vector2((float)(weightedX / energy), (float)(weightedY / energy))
            : Vector2.Zero;
        return new BloomMetrics(energy, centroid);
    }

    public static VelocityMetrics MeasureVelocity(ReadOnlySpan<float> rgba, int width, int height, float nonZeroEpsilon = 1.0e-6f)
    {
        ValidateRgbaInput(rgba, width, height);
        if (nonZeroEpsilon < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(nonZeroEpsilon));

        double magnitudeSum = 0.0;
        double velocityXSum = 0.0;
        double velocityYSum = 0.0;
        float maxMagnitude = 0.0f;
        int nonZeroSamples = 0;
        for (int offset = 0; offset < rgba.Length; offset += 4)
        {
            float magnitude = MathF.Sqrt(rgba[offset] * rgba[offset] + rgba[offset + 1] * rgba[offset + 1]);
            velocityXSum += rgba[offset];
            velocityYSum += rgba[offset + 1];
            magnitudeSum += magnitude;
            maxMagnitude = Math.Max(maxMagnitude, magnitude);
            if (magnitude > nonZeroEpsilon)
                nonZeroSamples++;
        }

        int pixelCount = checked(width * height);
        return new VelocityMetrics(
            new Vector2((float)(velocityXSum / pixelCount), (float)(velocityYSum / pixelCount)),
            (float)(magnitudeSum / pixelCount),
            maxMagnitude,
            nonZeroSamples);
    }

    public static EdgeSharpnessMetrics MeasureEdgeSharpness(ReadOnlySpan<float> rgba, int width, int height)
    {
        ValidateRgbaInput(rgba, width, height);

        double gradientSum = 0.0;
        float maxGradient = 0.0f;
        int gradientCount = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                double center = Luminance(rgba, offset);
                if (x + 1 < width)
                {
                    float gradient = (float)Math.Abs(center - Luminance(rgba, offset + 4));
                    gradientSum += gradient;
                    maxGradient = Math.Max(maxGradient, gradient);
                    gradientCount++;
                }
                if (y + 1 < height)
                {
                    float gradient = (float)Math.Abs(center - Luminance(rgba, offset + width * 4));
                    gradientSum += gradient;
                    maxGradient = Math.Max(maxGradient, gradient);
                    gradientCount++;
                }
            }
        }

        return new EdgeSharpnessMetrics(
            gradientCount > 0 ? (float)(gradientSum / gradientCount) : 0.0f,
            maxGradient);
    }

    public static double RootMeanSquareError(ReadOnlySpan<float> actualRgba, ReadOnlySpan<float> expectedRgba)
    {
        if (actualRgba.Length == 0 || actualRgba.Length != expectedRgba.Length || (actualRgba.Length & 3) != 0)
            throw new ArgumentException("RGBA inputs must be non-empty and have identical four-channel lengths.");

        double squaredError = 0.0;
        for (int i = 0; i < actualRgba.Length; i++)
        {
            double delta = actualRgba[i] - expectedRgba[i];
            squaredError += delta * delta;
        }
        return Math.Sqrt(squaredError / actualRgba.Length);
    }

    public static double RootMeanSquareError(ReadOnlySpan<double> actual, ReadOnlySpan<double> expected)
    {
        if (actual.Length == 0 || actual.Length != expected.Length)
            throw new ArgumentException("Inputs must be non-empty and have identical lengths.");

        double squaredError = 0.0;
        for (int i = 0; i < actual.Length; i++)
        {
            double delta = actual[i] - expected[i];
            squaredError += delta * delta;
        }
        return Math.Sqrt(squaredError / actual.Length);
    }

    public static double RelativeDifference(double first, double second)
    {
        double denominator = Math.Max(Math.Max(Math.Abs(first), Math.Abs(second)), double.Epsilon);
        return Math.Abs(first - second) / denominator;
    }

    public static double MeasureOutsideRegionEnergyFraction(
        ReadOnlySpan<float> rgba,
        int width,
        int height,
        int regionX,
        int regionY,
        int regionWidth,
        int regionHeight)
    {
        ValidateRgbaInput(rgba, width, height);
        if (regionX < 0 || regionY < 0 || regionWidth <= 0 || regionHeight <= 0 ||
            regionX + regionWidth > width || regionY + regionHeight > height)
        {
            throw new ArgumentOutOfRangeException(nameof(regionWidth), "Expected-energy region must be inside the image.");
        }

        double totalEnergy = 0.0;
        double outsideEnergy = 0.0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                double luminance = Math.Max(0.0, Luminance(rgba, offset));
                totalEnergy += luminance;
                if (x < regionX || x >= regionX + regionWidth || y < regionY || y >= regionY + regionHeight)
                    outsideEnergy += luminance;
            }
        }

        return totalEnergy > double.Epsilon ? outsideEnergy / totalEnergy : 0.0;
    }

    private static double Luminance(ReadOnlySpan<float> rgba, int offset)
        => rgba[offset] * 0.2126 + rgba[offset + 1] * 0.7152 + rgba[offset + 2] * 0.0722;

    private static void ValidateRgbaInput(ReadOnlySpan<float> rgba, int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        int expectedLength = checked(width * height * 4);
        if (rgba.Length != expectedLength)
            throw new ArgumentException($"Expected {expectedLength} RGBA values for {width}x{height}, got {rgba.Length}.", nameof(rgba));
    }
}

public sealed class RenderedOutputCaptureMetrics
{
    public string CapturePath { get; set; } = string.Empty;
    public string CaptureSha256 { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int PixelCount { get; set; }
    public int NonBlackPixelCount { get; set; }
    public double NonBlackPixelRatio { get; set; }
    public double MaximumLuminance { get; set; }
    public double LuminanceEnergy { get; set; }
    public float BloomCentroidX { get; set; }
    public float BloomCentroidY { get; set; }
    public float VelocityMeanX { get; set; }
    public float VelocityMeanY { get; set; }
    public float VelocityMeanMagnitude { get; set; }
    public float VelocityMaxMagnitude { get; set; }
    public int VelocityNonZeroSampleCount { get; set; }
    public float EdgeMeanGradient { get; set; }
    public float EdgeMaxGradient { get; set; }
    public int TopBandRows { get; set; }
    public int TopBandNonBlackPixelCount { get; set; }
    public double TopBandNonBlackPixelRatio { get; set; }
    public double TopBandMaximumLuminance { get; set; }
    public int TopBandMagentaPixelCount { get; set; }
    public int LuminanceFingerprintWidth { get; set; }
    public int LuminanceFingerprintHeight { get; set; }
    public double[] LuminanceFingerprint { get; set; } = [];
    public int VelocityMagnitudeFingerprintWidth { get; set; }
    public int VelocityMagnitudeFingerprintHeight { get; set; }
    public double[] VelocityMagnitudeFingerprint { get; set; } = [];
    public string TemporalScenario { get; set; } = string.Empty;
    public string TemporalSample { get; set; } = string.Empty;
    public string VelocityOracle { get; set; } = string.Empty;
    public int TemporalSequenceFrame { get; set; } = -1;
    public ulong RenderFrameId { get; set; }
}

/// <summary>
/// Fixed pass/fail limits for controlled stereo-vs-mono render cohorts. They are
/// intentionally defined in code before capture so a run cannot move its goalposts.
/// </summary>
public static class StereoRenderedOutputThresholds
{
    public const double MaxBloomRelativeEnergyDelta = 0.10;
    // A near-field emissive validation target has legitimate binocular disparity.
    // Keep a bounded stereo envelope large enough for that disparity while still
    // rejecting an eye sampling the wrong layer or bloom leaving its expected area.
    public const float MaxBloomCentroidDistance = 0.075f;
    public const float MinBloomCentroidX = 0.30f;
    public const float MaxBloomCentroidX = 0.65f;
    // The deterministic near-field motion and top-edge sentinels intentionally
    // occupy the upper third of the SPS view. The centered canonical OpenXR
    // validation pose reaches ~0.24 during object motion while both emitters
    // remain fully inside the image; clipping is guarded independently by the
    // full-frame coverage and top-band marker checks.
    public const float MinBloomCentroidY = 0.22f;
    // The corrected dynamic-UBO path exposes the full upper emissive marker;
    // its settled centroid reaches ~0.67 while remaining well clear of the
    // clipped top band checked independently below.
    public const float MaxBloomCentroidY = 0.70f;
    public const float BloomCentroidBoundsTolerance = 0.001f;
    public const double MaxCrossEyeLeakageFraction = 0.001;
    public const float MaxStaticVelocityMagnitude = 1.0e-4f;
    public const float MinMovingVelocityMagnitude = 1.0e-3f;
    public const float MinStaticEdgeSharpnessRatioToMono = 0.90f;
    public const float MinMovingEdgeSharpnessRatioToMono = 0.80f;
    public const double MaxTemporalConvergenceRmse = 0.02;
    public const double MinDisocclusionFingerprintRmse = 0.0025;
    public const double MinStereoEyeSpecificVelocityRmse = 1.0e-7;
    public const float MinDirectionalVelocityComponent = 1.0e-7f;
}
