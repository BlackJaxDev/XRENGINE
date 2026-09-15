using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using XREngine.Networking;
using XREngine.Scene;

namespace XREngine;

public static class WorldAssetIdentityProvider
{
    private static readonly ConditionalWeakTable<XRWorld, WorldAssetIdentity> VerifiedIdentities = new();
    private static readonly ConditionalWeakTable<XRWorld, HashSet<string>> VerifiedAssets = new();

    /// <summary>Registers the manifest paths only after every package file has passed integrity verification.</summary>
    public static void RegisterVerifiedAssetPaths(XRWorld world, IEnumerable<string> paths)
    {
        var verified = new HashSet<string>(paths.Select(static path => path.Replace('\\', '/')), StringComparer.Ordinal);
        VerifiedAssets.Remove(world);
        VerifiedAssets.Add(world, verified);
    }

    public static bool IsVerifiedAssetPath(XRWorld? world, string? path)
        => world is not null && !string.IsNullOrWhiteSpace(path) && path.Length <= 512
            && VerifiedAssets.TryGetValue(world, out HashSet<string>? paths) && paths.Contains(path);

    public static WorldAssetIdentity Create(XRWorld? world, string fallbackBuildVersion)
    {
        if (world is not null && VerifiedIdentities.TryGetValue(world, out WorldAssetIdentity? verified))
            return Clone(verified);

        string worldId = GetOverride(XREngineEnvironmentVariables.WorldId)
            ?? (world?.ID.ToString("D") ?? "local-world");
        string revisionId = GetOverride(XREngineEnvironmentVariables.WorldRevision)
            ?? ResolveRevisionId(world);
        string contentHash = GetOverride(XREngineEnvironmentVariables.WorldContentHash)
            ?? ComputeWorldHash(world);
        string requiredBuildVersion = GetOverride(XREngineEnvironmentVariables.WorldRequiredBuildVersion)
            ?? fallbackBuildVersion;

        return new WorldAssetIdentity
        {
            WorldId = worldId,
            RevisionId = revisionId,
            ContentHash = contentHash,
            AssetSchemaVersion = TryParsePositiveInt(GetOverride(XREngineEnvironmentVariables.WorldAssetSchemaVersion), 1),
            RequiredBuildVersion = requiredBuildVersion,
            Metadata =
            {
                ["source"] = string.IsNullOrWhiteSpace(world?.FilePath) ? "generated" : "file",
                ["worldName"] = world?.Name ?? string.Empty,
            }
        };
    }

    /// <summary>Binds an immutable package identity to a world after the package bytes were verified.</summary>
    public static void RegisterVerifiedIdentity(XRWorld world, WorldAssetIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(identity);
        VerifiedIdentities.Remove(world);
        VerifiedIdentities.Add(world, Clone(identity));
    }

    private static WorldAssetIdentity Clone(WorldAssetIdentity source)
        => new()
        {
            WorldId = source.WorldId,
            RevisionId = source.RevisionId,
            ContentHash = source.ContentHash,
            AssetSchemaVersion = source.AssetSchemaVersion,
            RequiredBuildVersion = source.RequiredBuildVersion,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
        };

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
