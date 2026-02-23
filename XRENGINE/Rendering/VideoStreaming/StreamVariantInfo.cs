using System;
using System.Collections.Generic;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Describes a single selectable quality variant from an HLS master playlist.
/// </summary>
public sealed class StreamVariantInfo
{
    /// <summary>
    /// The absolute URL of this variant's media playlist.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Declared bandwidth in bits/s from <c>#EXT-X-STREAM-INF BANDWIDTH=</c>.
    /// -1 if not specified.
    /// </summary>
    public int Bandwidth { get; init; } = -1;

    /// <summary>
    /// Resolution width in pixels (e.g. 1920). 0 if not specified.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Resolution height in pixels (e.g. 1080). 0 if not specified.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Human-readable name from <c>NAME="..."</c> or <c>VIDEO="..."</c>
    /// attribute on the stream-inf or matching <c>#EXT-X-MEDIA</c> tag.
    /// May be empty.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Frame rate from <c>FRAME-RATE=</c> attribute. 0 if not specified.
    /// </summary>
    public double FrameRate { get; init; }

    /// <summary>
    /// Codec string from <c>CODECS="..."</c> attribute. May be empty.
    /// </summary>
    public string Codecs { get; init; } = string.Empty;

    /// <summary>
    /// Returns a concise display label like "1080p60 (6.2 Mbps)" or "720p (2.5 Mbps)".
    /// Falls back to <see cref="Name"/> or the bandwidth value if resolution is unknown.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            string resolution = Height > 0 ? $"{Height}p" : string.Empty;
            string fps = FrameRate >= 1.0 ? $"{FrameRate:0}" : string.Empty;
            string bps = Bandwidth > 0 ? FormatBitrate(Bandwidth) : string.Empty;

            string label = resolution;
            if (!string.IsNullOrEmpty(fps) && !string.IsNullOrEmpty(resolution))
                label += fps;

            if (!string.IsNullOrEmpty(bps))
                label = string.IsNullOrEmpty(label) ? bps : $"{label} ({bps})";

            if (string.IsNullOrEmpty(label))
                label = !string.IsNullOrEmpty(Name) ? Name : Url;

            return label;
        }
    }

    private static string FormatBitrate(int bitsPerSecond)
    {
        double mbps = bitsPerSecond / 1_000_000.0;
        return mbps >= 1.0
            ? $"{mbps:F1} Mbps"
            : $"{bitsPerSecond / 1_000.0:F0} kbps";
    }

    /// <summary>
    /// Parses all <c>#EXT-X-STREAM-INF</c> entries from an HLS master playlist.
    /// Returns an empty list if the playlist is a media playlist (no variants).
    /// Results are ordered from highest to lowest bandwidth.
    /// </summary>
    public static IReadOnlyList<StreamVariantInfo> ParseFromMasterPlaylist(string baseUrl, string playlistText)
    {
        if (string.IsNullOrWhiteSpace(playlistText))
            return [];

        string[] lines = playlistText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var variants = new List<StreamVariantInfo>();

        for (int i = 0; i < lines.Length - 1; i++)
        {
            string line = lines[i];
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                continue;

            string next = lines[i + 1];
            if (string.IsNullOrWhiteSpace(next) || next.StartsWith('#'))
                continue;

            string url = MakeAbsolute(baseUrl, next);
            int bandwidth = ParseIntAttribute(line, "BANDWIDTH");
            (int w, int h) = ParseResolution(line);
            double frameRate = ParseDoubleAttribute(line, "FRAME-RATE");
            string codecs = ParseQuotedAttribute(line, "CODECS");
            string name = ParseQuotedAttribute(line, "NAME");
            if (string.IsNullOrEmpty(name))
                name = ParseQuotedAttribute(line, "VIDEO");

            variants.Add(new StreamVariantInfo
            {
                Url = url,
                Bandwidth = bandwidth,
                Width = w,
                Height = h,
                Name = name,
                FrameRate = frameRate,
                Codecs = codecs
            });
        }

        // Sort highest bandwidth first
        variants.Sort((a, b) => b.Bandwidth.CompareTo(a.Bandwidth));
        return variants;
    }

    private static int ParseIntAttribute(string line, string key)
    {
        string fullKey = key + "=";
        int start = line.IndexOf(fullKey, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return -1;

        start += fullKey.Length;
        int end = line.IndexOfAny([',', ' ', '\t'], start);
        string value = end >= 0 ? line[start..end] : line[start..];
        return int.TryParse(value, out int result) ? result : -1;
    }

    private static double ParseDoubleAttribute(string line, string key)
    {
        string fullKey = key + "=";
        int start = line.IndexOf(fullKey, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return 0;

        start += fullKey.Length;
        int end = line.IndexOfAny([',', ' ', '\t'], start);
        string value = end >= 0 ? line[start..end] : line[start..];
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double result) ? result : 0;
    }

    private static string ParseQuotedAttribute(string line, string key)
    {
        string fullKey = key + "=\"";
        int start = line.IndexOf(fullKey, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        start += fullKey.Length;
        int end = line.IndexOf('"', start);
        return end > start ? line[start..end] : string.Empty;
    }

    private static (int width, int height) ParseResolution(string line)
    {
        const string key = "RESOLUTION=";
        int start = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return (0, 0);

        start += key.Length;
        int end = line.IndexOfAny([',', ' ', '\t'], start);
        string value = end >= 0 ? line[start..end] : line[start..];

        int xIndex = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (xIndex < 0)
            return (0, 0);

        if (int.TryParse(value[..xIndex], out int w) &&
            int.TryParse(value[(xIndex + 1)..], out int h))
            return (w, h);

        return (0, 0);
    }

    private static string MakeAbsolute(string baseUrl, string maybeRelative)
    {
        if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out Uri? absolute))
            return absolute.ToString();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
            return maybeRelative;

        return new Uri(baseUri, maybeRelative).ToString();
    }
}
