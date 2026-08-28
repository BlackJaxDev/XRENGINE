using MemoryPack;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using XREngine.Core;
using XREngine.Core.Files;

namespace XREngine;

public static class AotRuntimeMetadataStore
{
    public const string MetadataFileName = "AotRuntimeMetadata.bin";

    private static readonly object Sync = new();
    private static volatile bool _loaded;
    private static AotRuntimeMetadata? _metadata;
    private static readonly ConcurrentDictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Type?> IgnoreCaseTypeCache = new(StringComparer.OrdinalIgnoreCase);

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
            $"Published AOT runtime metadata is missing. Ensure '{MetadataFileName}' is present in the published config archive.");

    public static void ResetForTestsOrReconfiguration()
    {
        lock (Sync)
        {
            _loaded = false;
            _metadata = null;
            TypeCache.Clear();
            IgnoreCaseTypeCache.Clear();
        }
    }

    public static Type? ResolveType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        return TypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key));
    }

    public static Type? ResolveTypeIgnoreCase(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        return IgnoreCaseTypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key, ignoreCase: true));
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

        AotRuntimeMetadata metadata = XRRuntimeEnvironment.IsAotRuntimeBuild
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

        try
        {
            byte[] bytes = AssetArchiveReader.GetAsset(configArchivePath, MetadataFileName);
            return MemoryPackSerializer.Deserialize<AotRuntimeMetadata>(bytes);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static Type? ResolveTypeCore(string typeName, bool ignoreCase = false)
    {
        Type? direct = Type.GetType(typeName, throwOnError: false, ignoreCase: ignoreCase);
        if (direct is not null)
            return direct;

        string fullTypeName = SerializedTypeIdentity.GetUnqualifiedTypeName(typeName);

        AotRuntimeMetadata? metadata = Metadata;
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
        if (PublishedCookedAssetRegistry.TryResolveByFullName(fullTypeName, ignoreCase, out Type? publishedType))
            return publishedType;

        // Fallback: scan loaded assemblies by FullName.
        // Type.GetType(string) only searches the calling assembly and System.Private.CoreLib
        // when given a namespace-qualified name without assembly qualifier, so types from other
        // engine assemblies (e.g., the main XREngine assembly) won't be found without this scan.
        if (!XRRuntimeEnvironment.IsAotRuntimeBuild)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = assembly.GetType(fullTypeName, throwOnError: false, ignoreCase: ignoreCase);
                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    private static string TypeNameOnly(string assemblyQualifiedName)
        => SerializedTypeIdentity.GetUnqualifiedTypeName(assemblyQualifiedName);
}
