using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XREngine.Diagnostics;

public static class AssetDiagnostics
{
    public readonly struct MissingAssetInfo
    {
        public string AssetPath { get; init; }
        public string Category { get; init; }
        public string? LastContext { get; init; }
        public IReadOnlyList<string> Contexts { get; init; }
        public int Count { get; init; }
        public DateTime FirstSeenUtc { get; init; }
        public DateTime LastSeenUtc { get; init; }
    }

    private sealed class MissingAssetAggregate
    {
        public string AssetPath = string.Empty;
        public string Category = string.Empty;
        public HashSet<string>? Contexts;
        public string? LastContext;
        public int Count;
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
    }

    private static readonly object _missingAssetLock = new();
    private static readonly Dictionary<string, MissingAssetAggregate> _missingAssets = new(StringComparer.OrdinalIgnoreCase);

    public static void RecordMissingAsset(string? assetPath, string? category, string? context = null)
    {
        string normalizedPath = NormalizePath(assetPath);
        string normalizedCategory = string.IsNullOrWhiteSpace(category) ? "Unknown" : category.Trim();
        string key = BuildKey(normalizedCategory, normalizedPath);
        DateTime nowUtc = DateTime.UtcNow;

        lock (_missingAssetLock)
        {
            if (!_missingAssets.TryGetValue(key, out var aggregate))
            {
                aggregate = new MissingAssetAggregate
                {
                    AssetPath = normalizedPath,
                    Category = normalizedCategory,
                    FirstSeenUtc = nowUtc
                };
                _missingAssets[key] = aggregate;
            }

            aggregate.Count++;
            aggregate.LastSeenUtc = nowUtc;
            if (!string.IsNullOrWhiteSpace(context))
            {
                aggregate.LastContext = context;
                aggregate.Contexts ??= new HashSet<string>(StringComparer.Ordinal);
                aggregate.Contexts.Add(context);
            }
        }
    }

    public static IReadOnlyList<MissingAssetInfo> GetTrackedMissingAssets()
    {
        lock (_missingAssetLock)
        {
            if (_missingAssets.Count == 0)
                return Array.Empty<MissingAssetInfo>();

            var snapshot = new MissingAssetInfo[_missingAssets.Count];
            int index = 0;
            foreach (var aggregate in _missingAssets.Values)
            {
                string[] contexts = aggregate.Contexts is { Count: > 0 } set
                    ? set.ToArray()
                    : Array.Empty<string>();

                snapshot[index++] = new MissingAssetInfo
                {
                    AssetPath = aggregate.AssetPath,
                    Category = aggregate.Category,
                    LastContext = aggregate.LastContext,
                    Contexts = contexts,
                    Count = aggregate.Count,
                    FirstSeenUtc = aggregate.FirstSeenUtc,
                    LastSeenUtc = aggregate.LastSeenUtc
                };
            }

            return snapshot;
        }
    }

    public static bool RemoveTrackedMissingAsset(string assetPath, string category)
    {
        string normalizedCategory = string.IsNullOrWhiteSpace(category) ? "Unknown" : category.Trim();
        string normalizedPath = NormalizePath(assetPath);
        string key = BuildKey(normalizedCategory, normalizedPath);

        lock (_missingAssetLock)
        {
            return _missingAssets.Remove(key);
        }
    }

    public static void ClearTrackedMissingAssets()
    {
        lock (_missingAssetLock)
        {
            _missingAssets.Clear();
        }
    }

    private static string BuildKey(string category, string assetPath)
        => string.Concat(category, "::", assetPath);

    private static string NormalizePath(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return "<unknown>";

        string trimmed = assetPath.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed.Replace('\u005c', '/');
        }
    }
}
