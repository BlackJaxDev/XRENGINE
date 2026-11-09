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

    // Minimum median luminance of a height field. Measured on Sponza: the brick height field has a
    // median of 0.71; its embossed and detail bump maps have 0.47 to 0.65. The margin is small, so the
    // explicit Height and Bump import modes remain the override.
    private const float HeightMinMedian = 0.68f;

    // File-name words that mark a height (displacement) field regardless of content.
    private static readonly string[] HeightNameHints = ["height", "disp", "parallax"];

    private const int MaxSamplesPerAxis = 128;

    private static readonly ConcurrentDictionary<string, ESurfaceDetailMapMode?> FileCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies the image file at <paramref name="filePath"/>. A grayscale map whose file name contains "height", "disp" or "parallax" is a height map. Returns false when the file is
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
                ESurfaceDetailMapMode classified = Classify(image);
                return classified == ESurfaceDetailMapMode.Bump && HasHeightNameHint(path)
                    ? ESurfaceDetailMapMode.Height
                    : classified;
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

    private static bool HasHeightNameHint(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        foreach (string hint in HeightNameHints)
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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

        // Bump and detail maps are authored around a mid-gray base. A height field with recessed
        // detail (for example bricks with mortar grooves) keeps most texels near the top.
        Span<float> values = luminance.AsSpan(0, count);
        values.Sort();
        float median = values[count / 2];
        return median >= HeightMinMedian ? ESurfaceDetailMapMode.Height : ESurfaceDetailMapMode.Bump;
    }
}
