using System.Collections.Concurrent;
using XREngine.Imaging;

namespace XREngine.Rendering.Models;

/// <summary>
/// Classifies a surface-detail texture as a normal, albedo, bump or height map from its pixels.
/// Results are cached by file path for the process lifetime.
/// </summary>
public static class SurfaceDetailMapClassifier
{
    // A normal map stores a mostly +Z tangent normal: blue high, red and green near mid.
    private const float NormalMinMeanBlue = 0.6f;
    private const float NormalMaxRedGreenOffset = 0.2f;
    private const float NormalMinMeanChroma = 0.08f;

    // Height and bump maps are grayscale. A colored map that is not a normal map is a color texture.
    private const float AlbedoMinMeanChroma = 0.08f;

    // Detail deviation from the base (median) value, and the minimum detail coverage.
    private const float DetailThreshold = 0.08f;
    private const float MinDetailFraction = 0.02f;

    // Embossed bump maps have bright and dark detail in similar amounts around a flat base.
    // Height fields have detail mostly on one side (for example dark mortar between bricks).
    private const float BumpMinBalance = 0.35f;

    private const int MaxSamplesPerAxis = 128;

    private static readonly ConcurrentDictionary<string, ESurfaceDetailMapMode?> FileCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies the image file at <paramref name="filePath"/>. Returns false when the file is
    /// missing or cannot be decoded.
    /// </summary>
    public static bool TryClassifyFile(string? filePath, out ESurfaceDetailMapMode mode)
    {
        mode = ESurfaceDetailMapMode.Auto;
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        ESurfaceDetailMapMode? cached = FileCache.GetOrAdd(filePath, static path =>
        {
            try
            {
                if (!File.Exists(path) || RuntimeImageCodecs.Current is not { } codec)
                    return null;

                using RuntimeImage image = codec.Decode(File.ReadAllBytes(path));
                return Classify(image);
            }
            catch (Exception)
            {
                // An unreadable texture is reported to the caller as unclassified.
                return null;
            }
        });

        if (cached is not { } result)
            return false;

        mode = result;
        return true;
    }

    /// <summary>
    /// Classifies decoded pixels. Never returns <see cref="ESurfaceDetailMapMode.Auto"/>.
    /// </summary>
    public static ESurfaceDetailMapMode Classify(RuntimeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Classify(image.CopyRgba8Pixels(), checked((int)image.Width), checked((int)image.Height));
    }

    /// <summary>
    /// Classifies tightly packed RGBA8 pixels. Never returns <see cref="ESurfaceDetailMapMode.Auto"/>.
    /// </summary>
    public static ESurfaceDetailMapMode Classify(ReadOnlySpan<byte> rgba8, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba8.Length < width * height * 4)
            return ESurfaceDetailMapMode.Bump;

        int stepX = Math.Max(1, width / MaxSamplesPerAxis);
        int stepY = Math.Max(1, height / MaxSamplesPerAxis);
        int samplesX = (width + stepX - 1) / stepX;
        int samplesY = (height + stepY - 1) / stepY;
        float[] luminance = new float[samplesX * samplesY];

        double sumRed = 0.0, sumGreen = 0.0, sumBlue = 0.0, sumChroma = 0.0;
        int count = 0;
        for (int y = 0; y < height; y += stepY)
        {
            for (int x = 0; x < width; x += stepX)
            {
                int offset = (y * width + x) * 4;
                float red = rgba8[offset] / 255.0f;
                float green = rgba8[offset + 1] / 255.0f;
                float blue = rgba8[offset + 2] / 255.0f;
                sumRed += red;
                sumGreen += green;
                sumBlue += blue;
                sumChroma += Math.Max(Math.Abs(red - green), Math.Max(Math.Abs(green - blue), Math.Abs(red - blue)));
                luminance[count++] = (red + green + blue) / 3.0f;
            }
        }

        float meanRed = (float)(sumRed / count);
        float meanGreen = (float)(sumGreen / count);
        float meanBlue = (float)(sumBlue / count);
        float meanChroma = (float)(sumChroma / count);
        if (meanBlue >= NormalMinMeanBlue &&
            meanChroma >= NormalMinMeanChroma &&
            Math.Abs(meanRed - 0.5f) <= NormalMaxRedGreenOffset &&
            Math.Abs(meanGreen - 0.5f) <= NormalMaxRedGreenOffset)
            return ESurfaceDetailMapMode.Normal;

        // Strong color that is not a normal map is a color texture filed in a detail slot.
        if (meanChroma >= AlbedoMinMeanChroma)
            return ESurfaceDetailMapMode.Albedo;

        Span<float> values = luminance.AsSpan(0, count);
        values.Sort();
        float median = values[count / 2];
        int brighter = 0, darker = 0;
        foreach (float value in values)
        {
            if (value > median + DetailThreshold)
                brighter++;
            else if (value < median - DetailThreshold)
                darker++;
        }

        float brighterFraction = brighter / (float)count;
        float darkerFraction = darker / (float)count;
        float largest = Math.Max(brighterFraction, darkerFraction);
        if (largest < MinDetailFraction)
            return ESurfaceDetailMapMode.Bump;

        float balance = Math.Min(brighterFraction, darkerFraction) / largest;
        return balance >= BumpMinBalance ? ESurfaceDetailMapMode.Bump : ESurfaceDetailMapMode.Height;
    }
}
