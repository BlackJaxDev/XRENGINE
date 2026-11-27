using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace XREngine.Scene.Importers;

/// <summary>
/// Persists validated image dimensions keyed by normalized file path, byte length, and UTC
/// last-write ticks so unmodified Unity prefab textures do not require a full PNG/JPG pixel
/// decode on every editor launch.
/// </summary>
internal static class SerializedTextureValidationCache
{
    private const string HeaderLine = "#xrengine-unity-texture-validation-v1";

    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<string, CachedEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static string? s_loadedCachePath;
    private static bool s_dirty;

    private readonly record struct CachedEntry(long Length, long LastWriteTicks, uint Width, uint Height);

    public static bool TryGetValidated(
        string fullPath,
        long length,
        long lastWriteTicks,
        out uint width,
        out uint height)
    {
        EnsureLoaded();
        if (Entries.TryGetValue(fullPath, out CachedEntry entry) &&
            entry.Length == length &&
            entry.LastWriteTicks == lastWriteTicks &&
            entry.Width > 0 &&
            entry.Height > 0)
        {
            width = entry.Width;
            height = entry.Height;
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }

    public static void RecordValidated(
        string fullPath,
        long length,
        long lastWriteTicks,
        uint width,
        uint height)
    {
        if (width == 0 || height == 0 || IsUnderTempDirectory(fullPath))
            return;

        EnsureLoaded();
        var updated = new CachedEntry(length, lastWriteTicks, width, height);
        if (Entries.TryGetValue(fullPath, out CachedEntry existing) && existing == updated)
            return;

        Entries[fullPath] = updated;
        lock (Sync)
            s_dirty = true;
    }

    public static void FlushIfDirty()
    {
        string? cachePath = ResolveCachePath();
        if (string.IsNullOrWhiteSpace(cachePath))
            return;

        lock (Sync)
        {
            if (!s_dirty)
                return;

            s_dirty = false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            using (var writer = new StreamWriter(tempPath, append: false, Encoding.UTF8))
            {
                writer.WriteLine(HeaderLine);
                foreach ((string path, CachedEntry entry) in Entries)
                {
                    writer.Write(path);
                    writer.Write('\t');
                    writer.Write(entry.Length.ToString(CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.Write(entry.LastWriteTicks.ToString(CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.Write(entry.Width.ToString(CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.WriteLine(entry.Height.ToString(CultureInfo.InvariantCulture));
                }
            }

            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Validation cache persistence is opportunistic and never fails an import.
        }
    }

    private static void EnsureLoaded()
    {
        string? cachePath = ResolveCachePath();
        if (string.IsNullOrWhiteSpace(cachePath))
            return;

        if (string.Equals(Volatile.Read(ref s_loadedCachePath), cachePath, StringComparison.OrdinalIgnoreCase))
            return;

        lock (Sync)
        {
            if (string.Equals(s_loadedCachePath, cachePath, StringComparison.OrdinalIgnoreCase))
                return;

            Entries.Clear();
            s_loadedCachePath = cachePath;
            if (!File.Exists(cachePath))
                return;

            try
            {
                using var reader = new StreamReader(cachePath, Encoding.UTF8);
                string? header = reader.ReadLine();
                if (!string.Equals(header, HeaderLine, StringComparison.Ordinal))
                    return;

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length == 0)
                        continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length != 5 ||
                        !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long length) ||
                        !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) ||
                        !uint.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint width) ||
                        !uint.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint height))
                    {
                        continue;
                    }

                    Entries[parts[0]] = new CachedEntry(length, ticks, width, height);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                Entries.Clear();
            }
        }
    }

    private static string? ResolveCachePath()
    {
        string? cacheRoot = Engine.Assets?.GameCachePath;
        return string.IsNullOrWhiteSpace(cacheRoot)
            ? null
            : Path.Combine(cacheRoot, "UnityTextureValidation", "validated-v1.tsv");
    }

    private static bool IsUnderTempDirectory(string fullPath)
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string relative = Path.GetRelativePath(tempRoot, fullPath);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
