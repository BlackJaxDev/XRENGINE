using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XREngine.Diagnostics;

public static class AssetDiagnostics
{
    public enum AssetReferenceRepairKind
    {
        Unknown = 0,
        PathMadePortable,
        FoundCurrentWorkspacePath,
    }

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

    public readonly struct RebasedAssetInfo
    {
        public string OriginalPath { get; init; }
        public string ResolvedPath { get; init; }
        public string Category { get; init; }
        public AssetReferenceRepairKind RepairKind { get; init; }
        public string? LastContext { get; init; }
        public string? LastSourceAssetPath { get; init; }
        public IReadOnlyList<string> SourceAssetPaths { get; init; }
        public int Count { get; init; }
        public DateTime FirstSeenUtc { get; init; }
        public DateTime LastSeenUtc { get; init; }
    }

    private sealed class RebasedAssetAggregate
    {
        public string OriginalPath = string.Empty;
        public string ResolvedPath = string.Empty;
        public string Category = string.Empty;
        public AssetReferenceRepairKind RepairKind;
        public string? LastContext;
        public string? LastSourceAssetPath;
        public HashSet<string>? SourceAssetPaths;
        public int Count;
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
    }

    private static readonly object _rebasedAssetLock = new();
    private static readonly Dictionary<string, RebasedAssetAggregate> _rebasedAssets = new(StringComparer.OrdinalIgnoreCase);

    private static int _pendingDisplayFlag;

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

        System.Threading.Interlocked.Exchange(ref _pendingDisplayFlag, 1);
    }

    /// <summary>
    /// Records that an asset reference was found in non-portable form (for example an absolute
    /// path baked from a different machine layout, or a stale path that had to be rebased onto a
    /// known asset root). Used by the editor to surface workspace-portability issues without
    /// failing the load.
    /// </summary>
    public static void RecordRebasedAsset(
        string? originalPath,
        string? resolvedPath,
        string? category,
        string? context = null,
        AssetReferenceRepairKind repairKind = AssetReferenceRepairKind.Unknown,
        string? sourceAssetPath = null)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
            return;

        string normalizedOriginal = NormalizePath(originalPath);
        string normalizedResolved = NormalizePath(resolvedPath);
        string normalizedCategory = string.IsNullOrWhiteSpace(category) ? "Unknown" : category.Trim();
        string? normalizedSourceAssetPath = NormalizeOptionalPath(sourceAssetPath);
        string key = BuildRebasedKey(normalizedCategory, normalizedOriginal, normalizedResolved, repairKind);
        DateTime nowUtc = DateTime.UtcNow;

        lock (_rebasedAssetLock)
        {
            if (!_rebasedAssets.TryGetValue(key, out var aggregate))
            {
                aggregate = new RebasedAssetAggregate
                {
                    OriginalPath = normalizedOriginal,
                    ResolvedPath = normalizedResolved,
                    Category = normalizedCategory,
                    RepairKind = repairKind,
                    FirstSeenUtc = nowUtc
                };
                _rebasedAssets[key] = aggregate;
            }

            aggregate.Count++;
            aggregate.LastSeenUtc = nowUtc;
            if (!string.IsNullOrWhiteSpace(context))
                aggregate.LastContext = context;
            if (!string.IsNullOrWhiteSpace(normalizedSourceAssetPath))
            {
                aggregate.LastSourceAssetPath = normalizedSourceAssetPath;
                aggregate.SourceAssetPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                aggregate.SourceAssetPaths.Add(normalizedSourceAssetPath);
            }
        }

        System.Threading.Interlocked.Exchange(ref _pendingDisplayFlag, 1);
    }

    public static IReadOnlyList<RebasedAssetInfo> GetTrackedRebasedAssets()
    {
        lock (_rebasedAssetLock)
        {
            if (_rebasedAssets.Count == 0)
                return Array.Empty<RebasedAssetInfo>();

            var snapshot = new RebasedAssetInfo[_rebasedAssets.Count];
            int index = 0;
            foreach (var aggregate in _rebasedAssets.Values)
            {
                string[] sourceAssetPaths = aggregate.SourceAssetPaths is { Count: > 0 } set
                    ? set.ToArray()
                    : Array.Empty<string>();

                snapshot[index++] = new RebasedAssetInfo
                {
                    OriginalPath = aggregate.OriginalPath,
                    ResolvedPath = aggregate.ResolvedPath,
                    Category = aggregate.Category,
                    RepairKind = aggregate.RepairKind,
                    LastContext = aggregate.LastContext,
                    LastSourceAssetPath = aggregate.LastSourceAssetPath,
                    SourceAssetPaths = sourceAssetPaths,
                    Count = aggregate.Count,
                    FirstSeenUtc = aggregate.FirstSeenUtc,
                    LastSeenUtc = aggregate.LastSeenUtc
                };
            }

            return snapshot;
        }
    }

    public static void ClearTrackedRebasedAssets()
    {
        lock (_rebasedAssetLock)
        {
            _rebasedAssets.Clear();
        }
    }

    /// <summary>
    /// Returns true once when new missing/rebased asset diagnostics have been recorded since the
    /// last call. The editor uses this to auto-open the Missing Assets panel after a load.
    /// </summary>
    public static bool ConsumePendingDisplayFlag()
        => System.Threading.Interlocked.Exchange(ref _pendingDisplayFlag, 0) != 0;

    private static string BuildRebasedKey(
        string category,
        string originalPath,
        string resolvedPath,
        AssetReferenceRepairKind repairKind)
        => string.Concat(category, "::", repairKind.ToString(), "::", originalPath, "->", resolvedPath);

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
        if (string.Equals(trimmed, "<unknown>", StringComparison.Ordinal))
            return trimmed;

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed.Replace('\u005c', '/');
        }
    }

    private static string? NormalizeOptionalPath(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return null;

        string trimmed = assetPath.Trim();
        if (string.Equals(trimmed, "<unknown>", StringComparison.Ordinal))
            return null;

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
