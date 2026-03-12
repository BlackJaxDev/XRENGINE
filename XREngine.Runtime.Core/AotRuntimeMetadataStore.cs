using MemoryPack;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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
        return Type.GetType(assemblyQualifiedName, throwOnError: false, ignoreCase: false);
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

        AotRuntimeMetadata? metadata = Metadata;
        if (metadata is null)
            return null;

        string? assemblyQualifiedName = metadata.KnownTypeAssemblyQualifiedNames
            .FirstOrDefault(x => string.Equals(x, typeName, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || string.Equals(TypeNameOnly(x), typeName, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

        return string.IsNullOrWhiteSpace(assemblyQualifiedName)
            ? null
            : Type.GetType(assemblyQualifiedName, throwOnError: false, ignoreCase: ignoreCase);
    }

    private static string TypeNameOnly(string assemblyQualifiedName)
    {
        int commaIndex = assemblyQualifiedName.IndexOf(',');
        return commaIndex < 0 ? assemblyQualifiedName : assemblyQualifiedName[..commaIndex];
    }
}
