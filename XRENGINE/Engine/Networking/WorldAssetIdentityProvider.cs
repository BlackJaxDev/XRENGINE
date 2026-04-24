using System.Security.Cryptography;
using System.Text;
using XREngine.Networking;
using XREngine.Scene;

namespace XREngine;

public static class WorldAssetIdentityProvider
{
    public static WorldAssetIdentity Create(XRWorld? world, string fallbackBuildVersion)
    {
        string worldId = GetOverride("XRE_WORLD_ID")
            ?? (world?.ID.ToString("D") ?? "local-world");
        string revisionId = GetOverride("XRE_WORLD_REVISION")
            ?? ResolveRevisionId(world);
        string contentHash = GetOverride("XRE_WORLD_CONTENT_HASH")
            ?? ComputeWorldHash(world);
        string requiredBuildVersion = GetOverride("XRE_WORLD_REQUIRED_BUILD_VERSION")
            ?? fallbackBuildVersion;

        return new WorldAssetIdentity
        {
            WorldId = worldId,
            RevisionId = revisionId,
            ContentHash = contentHash,
            AssetSchemaVersion = TryParsePositiveInt(GetOverride("XRE_WORLD_ASSET_SCHEMA_VERSION"), 1),
            RequiredBuildVersion = requiredBuildVersion,
            Metadata =
            {
                ["source"] = string.IsNullOrWhiteSpace(world?.FilePath) ? "generated" : "file",
                ["worldName"] = world?.Name ?? string.Empty,
            }
        };
    }

    private static string ResolveRevisionId(XRWorld? world)
    {
        if (world?.OriginalLastWriteTimeUtc is DateTime originalWrite)
            return originalWrite.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(world?.FilePath) && File.Exists(world.FilePath))
            return File.GetLastWriteTimeUtc(world.FilePath).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return "local";
    }

    private static string ComputeWorldHash(XRWorld? world)
    {
        if (!string.IsNullOrWhiteSpace(world?.FilePath) && File.Exists(world.FilePath))
            return $"sha256:{ComputeFileSha256(world.FilePath)}";

        IEnumerable<string> sceneNames = world is null
            ? Array.Empty<string>()
            : world.Scenes
                .Select(static scene => scene.Name ?? string.Empty)
                .OrderBy(static name => name, StringComparer.Ordinal);
        string fingerprint = string.Join("|",
        [
            world?.Name ?? "local-world",
            world?.DefaultGameMode?.GetType().FullName ?? string.Empty,
            .. sceneNames,
        ]);
        return $"sha256:{ComputeStringSha256(fingerprint)}";
    }

    private static string ComputeFileSha256(string path)
    {
        using SHA256 sha256 = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string ComputeStringSha256(string value)
    {
        using SHA256 sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string? GetOverride(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int TryParsePositiveInt(string? value, int fallback)
        => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : fallback;
}
