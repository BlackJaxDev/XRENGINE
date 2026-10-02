using MemoryPack;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using XREngine.Core;
using XREngine.Core.Files;
using XREngine.Data.Runtime.AotParity;

namespace XREngine;

public static class AotRuntimeMetadataStore
{
    public const string MetadataFileName = "AotRuntimeMetadata.bin";

    // Asset registration is an optional feature of the desktop asset pipeline.
    // The registry installs this resolver when initialized; dynamic runtime type
    // discovery continues to work without an asset registry.
    internal static Func<string, bool, Type?>? PublishedAssetTypeResolver { get; set; }

    private static readonly object Sync = new();
    private static volatile bool _loaded;
    private static AotRuntimeMetadata? _metadata;
    private static readonly ConcurrentDictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Type?> IgnoreCaseTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Type?> PublishedTypeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Type?> PublishedIgnoreCaseTypeCache = new(StringComparer.OrdinalIgnoreCase);

    // Names whose development resolution succeeded only through Type.GetType or an assembly scan.
    // Cache hits on these names re-report inside the player path so an editor-time resolution does
    // not hide a later play-mode gap.
    private static readonly ConcurrentDictionary<string, byte> ReflectiveResolutions = new(StringComparer.OrdinalIgnoreCase);

    public static AotRuntimeMetadata? Metadata
    {
        get
        {
            EnsureLoaded();
            return _metadata;
        }
    }

    public static AotRuntimeMetadata RequireMetadata()
        => Metadata ?? throw new InvalidOperationException(
            $"Published runtime metadata is missing. Ensure '{MetadataFileName}' is present in the published config archive.");

    public static void ResetForTestsOrReconfiguration()
    {
        lock (Sync)
        {
            _loaded = false;
            _metadata = null;
            TypeCache.Clear();
            IgnoreCaseTypeCache.Clear();
            PublishedTypeCache.Clear();
            PublishedIgnoreCaseTypeCache.Clear();
        }
    }

    public static Type? ResolveType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        if (XRRuntimeEnvironment.IsPublishedBuild)
            return PublishedTypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key));

        Type? resolved = TypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key));
        ReportReflectiveResolution(typeName, resolved);
        return resolved;
    }

    public static Type? ResolveTypeIgnoreCase(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        if (XRRuntimeEnvironment.IsPublishedBuild)
            return PublishedIgnoreCaseTypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key, ignoreCase: true));

        Type? resolved = IgnoreCaseTypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key, ignoreCase: true));
        ReportReflectiveResolution(typeName, resolved);
        return resolved;
    }

    private static void ReportReflectiveResolution(string typeName, Type? resolved)
    {
        if (resolved is null || !ReflectiveResolutions.ContainsKey(typeName))
            return;

        AotParityDiagnostics.Report(
            resolved,
            EAotParityCategory.TypeResolutionScan,
            $"{nameof(AotRuntimeMetadataStore)}.{nameof(ResolveType)}",
            "Add the type to the published runtime metadata known-type table or install a published registration that resolves it, so lookup succeeds without Type.GetType or an assembly scan.");
    }

    public static Type? ResolveType(int typeIndex)
    {
        AotRuntimeMetadata? metadata = Metadata;
        if (metadata is null || typeIndex < 0 || typeIndex >= metadata.KnownTypeAssemblyQualifiedNames.Length)
            return null;

        string assemblyQualifiedName = metadata.KnownTypeAssemblyQualifiedNames[typeIndex];
        return ResolveTypeCore(assemblyQualifiedName);
    }

    public static bool TryGetKnownTypeIndex(Type type, out int typeIndex)
    {
        ArgumentNullException.ThrowIfNull(type);

        string? assemblyQualifiedName = type.AssemblyQualifiedName;
        if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
        {
            typeIndex = -1;
            return false;
        }

        return TryGetKnownTypeIndex(assemblyQualifiedName, out typeIndex);
    }

    public static bool TryGetKnownTypeIndex(string assemblyQualifiedName, out int typeIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyQualifiedName);

        AotRuntimeMetadata? metadata = Metadata;
        if (metadata is null)
        {
            typeIndex = -1;
            return false;
        }

        string[] knownTypes = metadata.KnownTypeAssemblyQualifiedNames;
        for (int i = 0; i < knownTypes.Length; i++)
        {
            if (string.Equals(knownTypes[i], assemblyQualifiedName, StringComparison.Ordinal))
            {
                typeIndex = i;
                return true;
            }
        }

        typeIndex = -1;
        return false;
    }

    public static bool IsPublishedRuntimeAssetType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        string? assemblyQualifiedName = type.AssemblyQualifiedName;
        return !string.IsNullOrWhiteSpace(assemblyQualifiedName)
            && IsPublishedRuntimeAssetType(assemblyQualifiedName);
    }

    public static bool IsPublishedRuntimeAssetType(string assemblyQualifiedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyQualifiedName);

        AotRuntimeMetadata metadata = XRRuntimeEnvironment.IsPublishedBuild
            ? RequireMetadata()
            : Metadata ?? new AotRuntimeMetadata();
        string requestedTypeName = TypeNameOnly(assemblyQualifiedName);

        foreach (string candidate in metadata.PublishedRuntimeAssetTypeNames)
        {
            if (string.Equals(candidate, assemblyQualifiedName, StringComparison.Ordinal)
                || string.Equals(TypeNameOnly(candidate), requestedTypeName, StringComparison.Ordinal))
            {
                // NativeAOT may expose a different assembly-qualified identity for a type
                // than the editor observed while cooking. The full type name is stable;
                // PublishedCookedAssetRegistry still requires an exact Type registration
                // before any payload can be deserialized.
                return true;
            }
        }

        return false;
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        lock (Sync)
        {
            if (_loaded)
                return;

            _metadata = LoadMetadata();
            _loaded = true;
        }
    }

    private static AotRuntimeMetadata? LoadMetadata()
    {
        string? configArchivePath = XRRuntimeEnvironment.PublishedConfigArchivePath;
        if (string.IsNullOrWhiteSpace(configArchivePath) || !File.Exists(configArchivePath))
            return null;

        // The config archive stays open for the life of the config root; the metadata entry is
        // read through a lease so no transient copy of the table is made.
        PublishedArchiveHandle handle = PublishedArchiveRegistry.GetOrOpen(configArchivePath);
        if (!handle.TryReadAsset(MetadataFileName, out CookedPayloadLease lease))
            return null;

        try
        {
            return MemoryPackSerializer.Deserialize<AotRuntimeMetadata>(lease.Span);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static Type? ResolveTypeCore(string typeName, bool ignoreCase = false)
    {
        string fullTypeName = SerializedTypeIdentity.GetUnqualifiedTypeName(typeName);
        if (!ignoreCase && RuntimeTypeContractRegistry.TryResolve(fullTypeName, out Type? generatedType))
            return generatedType;
        if (!ignoreCase && XREngine.Core.Files.CookedBinaryFormatterRegistry.TryResolve(fullTypeName, out Type? formatterType))
            return formatterType;

        if (!XRRuntimeEnvironment.IsPublishedBuild)
        {
            Type? direct = Type.GetType(typeName, throwOnError: false, ignoreCase: ignoreCase);
            if (direct is not null)
            {
                // Development keeps the fast direct lookup, but records whether the published order
                // (metadata table, then published registrations) would also have resolved the name.
                if (!IsResolvableThroughPublishedPaths(typeName, ignoreCase))
                    ReflectiveResolutions.TryAdd(typeName, 0);
                return direct;
            }
        }

        AotRuntimeMetadata? metadata = XRRuntimeEnvironment.IsPublishedBuild ? RequireMetadata() : Metadata;
        if (metadata is not null)
        {
            string? assemblyQualifiedName = metadata.KnownTypeAssemblyQualifiedNames
                .FirstOrDefault(x => string.Equals(x, typeName, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || string.Equals(
                        SerializedTypeIdentity.GetUnqualifiedTypeName(x),
                        fullTypeName,
                        ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

            if (!string.IsNullOrWhiteSpace(assemblyQualifiedName))
            {
                var fromMetadata = Type.GetType(assemblyQualifiedName, throwOnError: false, ignoreCase: ignoreCase);
                if (fromMetadata is not null)
                    return fromMetadata;
            }
        }

        // Published registrations are explicit AOT roots. They provide a trimmed-safe
        // path for repository assets whose persisted outer assembly qualifier changed.
        if (PublishedAssetTypeResolver?.Invoke(fullTypeName, ignoreCase) is { } publishedType)
            return publishedType;

        // Fallback: scan loaded assemblies by FullName.
        // Type.GetType(string) only searches the calling assembly and System.Private.CoreLib
        // when given a namespace-qualified name without assembly qualifier, so types from other
        // engine assemblies (e.g., the main XREngine assembly) won't be found without this scan.
        if (!XRRuntimeEnvironment.IsPublishedBuild)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = assembly.GetType(fullTypeName, throwOnError: false, ignoreCase: ignoreCase);
                if (found is not null)
                {
                    ReflectiveResolutions.TryAdd(typeName, 0);
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Mirrors the published resolution order without any development fallback: the known-type
    /// table, then the published asset registration resolver.
    /// </summary>
    private static bool IsResolvableThroughPublishedPaths(string typeName, bool ignoreCase)
    {
        string fullTypeName = SerializedTypeIdentity.GetUnqualifiedTypeName(typeName);
        if (!ignoreCase && RuntimeTypeContractRegistry.TryResolve(fullTypeName, out _))
            return true;
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        AotRuntimeMetadata? metadata = Metadata;
        if (metadata is not null)
        {
            string[] knownTypes = metadata.KnownTypeAssemblyQualifiedNames;
            for (int i = 0; i < knownTypes.Length; i++)
            {
                if (string.Equals(knownTypes[i], typeName, comparison)
                    || string.Equals(SerializedTypeIdentity.GetUnqualifiedTypeName(knownTypes[i]), fullTypeName, comparison))
                {
                    return true;
                }
            }
        }

        return PublishedAssetTypeResolver?.Invoke(fullTypeName, ignoreCase) is not null;
    }

    private static string TypeNameOnly(string assemblyQualifiedName)
        => SerializedTypeIdentity.GetUnqualifiedTypeName(assemblyQualifiedName);
}
