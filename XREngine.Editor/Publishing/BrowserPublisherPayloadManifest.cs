using System.Security.Cryptography;
using System.Text.Json;

namespace XREngine.Editor.Publishing;

/// <summary>Checks that an installed browser publishing graph matches the Editor package.</summary>
internal static class BrowserPublisherPayloadManifest
{
    private const string ManifestName = "browser-publisher-manifest.json";

    internal static void Validate(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        string manifestPath = Path.Combine(fullRoot, ManifestName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("BrowserPublisher.ManifestMissing: the packaged browser publishing manifest is required.", manifestPath);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement manifest = document.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("schema", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1 ||
            !manifest.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("BrowserPublisher.ManifestInvalid: unrecognized payload manifest.");

        Dictionary<string, string> expected = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object ||
                !file.TryGetProperty("path", out JsonElement pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                !file.TryGetProperty("sha256", out JsonElement hashValue) || hashValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("BrowserPublisher.ManifestInvalid: an entry has invalid fields.");
            string relativePath = pathValue.GetString()!;
            string digest = hashValue.GetString()!;
            if (!IsSafeRelativePath(relativePath) || digest.Length != 64 ||
                !digest.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                !expected.TryAdd(relativePath, digest))
                throw new InvalidDataException($"BrowserPublisher.ManifestInvalid: invalid or duplicate entry '{relativePath}'.");
        }
        if (expected.Count == 0)
            throw new InvalidDataException("BrowserPublisher.ManifestInvalid: no payload files are declared.");

        HashSet<string> expectedDirectories = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in expected.Keys)
        {
            int separator = path.IndexOf('/');
            while (separator >= 0)
            {
                expectedDirectories.Add(path[..separator]);
                separator = path.IndexOf('/', separator + 1);
            }
        }

        Stack<string> directories = new();
        directories.Push(fullRoot);
        int seen = 0;
        while (directories.Count > 0)
        {
            string directory = directories.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"BrowserPublisher.LinkedPayload: '{directory}' is linked.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"BrowserPublisher.LinkedPayload: '{entry}' is linked.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    string relativeDirectory = Path.GetRelativePath(fullRoot, entry).Replace('\\', '/');
                    if (!expectedDirectories.Contains(relativeDirectory))
                        throw new InvalidDataException($"BrowserPublisher.PayloadModified: unexpected directory '{relativeDirectory}'.");
                    directories.Push(entry);
                    continue;
                }

                string relativePath = Path.GetRelativePath(fullRoot, entry).Replace('\\', '/');
                if (string.Equals(relativePath, ManifestName, StringComparison.Ordinal))
                    continue;
                if (!expected.TryGetValue(relativePath, out string? digest))
                    throw new InvalidDataException($"BrowserPublisher.PayloadModified: unexpected file '{relativePath}'.");
                using FileStream stream = File.OpenRead(entry);
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), digest, StringComparison.Ordinal))
                    throw new InvalidDataException($"BrowserPublisher.PayloadModified: '{relativePath}' differs from its manifest.");
                seen++;
            }
        }
        if (seen != expected.Count)
            throw new InvalidDataException("BrowserPublisher.PayloadModified: one or more declared files are missing.");
    }

    private static bool IsSafeRelativePath(string path)
        => !string.IsNullOrWhiteSpace(path) &&
           !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') &&
           !string.Equals(path, ManifestName, StringComparison.OrdinalIgnoreCase) &&
           path.Split('/').All(static part => part.Length != 0 && part != "." && part != "..");
}
