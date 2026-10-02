using MemoryPack;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using XREngine.Core;
using XREngine.Core.Files;

namespace XREngine;

public static class AotRuntimeMetadataStore
{
    public const string MetadataFileName = "AotRuntimeMetadata.bin";

    // Asset registration is an optional feature of the desktop asset pipeline.
    // The registry installs this resolver when initialized; dynamic runtime type
    // discovery continues to work without an asset registry.
    internal static Func<string, bool, Type?>? PublishedAssetTypeResolver { get; set; }

    // Desktop runtime configuration installs its archive reader before the
    // published path is accessed. Portable scenes have no archive decoder.
    internal static Func<string, string, byte[]>? MetadataArchiveReader { get; set; }

    private static readonly object Sync = new();
    private static volatile bool _loaded;
    private static AotRuntimeMetadata? _metadata;
    private static string? _browserMetadataFingerprint;
    private static string? _prematureBrowserTypeDiscovery;
    private static readonly ConcurrentDictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Type?> IgnoreCaseTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Type?> PublishedTypeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Type?> PublishedIgnoreCaseTypeCache = new(StringComparer.OrdinalIgnoreCase);
    [ThreadStatic]
    private static DevelopmentTypeResolutionFrame? _preferredDevelopmentTypeResolver;

    /// <summary>Prefers one authoring assembly on the current thread during synchronous development deserialization.</summary>
    public static IDisposable PreferDevelopmentTypes(Func<string, bool, Type?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (XRRuntimeEnvironment.IsPublishedBuild)
            throw new InvalidOperationException("Published type resolution cannot use an authoring assembly preference.");
        DevelopmentTypeResolutionFrame frame = new(resolver, _preferredDevelopmentTypeResolver);
        _preferredDevelopmentTypeResolver = frame;
        return frame;
    }

    private sealed class DevelopmentTypeResolutionFrame(
        Func<string, bool, Type?> resolver, DevelopmentTypeResolutionFrame? previous) : IDisposable
    {
        private Func<string, bool, Type?>? _resolver = resolver;
        private DevelopmentTypeResolutionFrame? _previous = previous;
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;
        private bool _disposed;

        public Type? Resolve(string name, bool ignoreCase) => _resolver?.Invoke(name, ignoreCase);

        public void Dispose()
        {
            if (_disposed)
                return;
            if (Environment.CurrentManagedThreadId != _ownerThread
                || !ReferenceEquals(_preferredDevelopmentTypeResolver, this))
                throw new InvalidOperationException("Development type preferences must be retired in LIFO order on their owning thread.");
            _disposed = true;
            _preferredDevelopmentTypeResolver = _previous;
            _previous = null;
            _resolver = null;
        }
    }

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

    /// <summary>Installs hash-verified browser metadata before published runtime types can initialize.</summary>
    public static void InstallVerifiedBrowserMetadata(byte[] bytes, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64 || fingerprint.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("PublishedMetadata.HashMismatch: browser metadata bytes differ from their manifest identity.");

        lock (Sync)
        {
            if (_prematureBrowserTypeDiscovery is { } discovery)
                throw new InvalidOperationException($"PublishedMetadata.StartupOrderInvalid: '{discovery}' used development type discovery before browser metadata installation; reload the page.");
            if (_browserMetadataFingerprint is not null)
            {
                if (!string.Equals(_browserMetadataFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("PublishedMetadata.PageReloadRequired: different published type metadata cannot replace the process-wide type table; reload the page.");
                return;
            }
            if (_loaded || _metadata is not null)
                throw new InvalidOperationException("PublishedMetadata.StartupOrderInvalid: runtime type metadata was accessed before browser metadata installation; reload the page.");

            AotRuntimeMetadata metadata = MemoryPackSerializer.Deserialize<AotRuntimeMetadata>(bytes)
                ?? throw new InvalidDataException("PublishedMetadata.Invalid: browser metadata payload is empty.");
            if (metadata.KnownTypeAssemblyQualifiedNames is null || metadata.TransformTypes is null
                || metadata.TypeRedirects is null || metadata.WorldObjectReplications is null
                || metadata.PublishedRuntimeAssetTypeNames is null || metadata.YamlTypeConverterTypeNames is null)
                throw new InvalidDataException("PublishedMetadata.Invalid: required metadata tables are absent.");
            _metadata = metadata;
            _loaded = true;
            _browserMetadataFingerprint = fingerprint;
        }
    }

    /// <summary>Records a one-shot development discovery that cannot be rebuilt as published in the same page.</summary>
    public static void NoteBrowserDevelopmentTypeDiscovery(string owner)
    {
        if (!OperatingSystem.IsBrowser())
            return;
        lock (Sync)
            _prematureBrowserTypeDiscovery ??= owner;
    }

    public static void ResetForTestsOrReconfiguration()
    {
        lock (Sync)
        {
            if (_browserMetadataFingerprint is not null)
                throw new InvalidOperationException("PublishedMetadata.PageReloadRequired: browser metadata is process-wide and cannot be reset; reload the page.");
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

        if (!XRRuntimeEnvironment.IsPublishedBuild
            && _preferredDevelopmentTypeResolver?.Resolve(typeName, false) is { } preferred)
            return preferred;

        return XRRuntimeEnvironment.IsPublishedBuild
            ? PublishedTypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key))
            : TypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key));
    }

    public static Type? ResolveTypeIgnoreCase(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        if (!XRRuntimeEnvironment.IsPublishedBuild
            && _preferredDevelopmentTypeResolver?.Resolve(typeName, true) is { } preferred)
            return preferred;

        return XRRuntimeEnvironment.IsPublishedBuild
            ? PublishedIgnoreCaseTypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key, ignoreCase: true))
            : IgnoreCaseTypeCache.GetOrAdd(typeName, static key => ResolveTypeCore(key, ignoreCase: true));
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

        try
        {
            Func<string, string, byte[]> reader = MetadataArchiveReader
                ?? throw new NotSupportedException("No published config archive reader is registered for this runtime.");
            byte[] bytes = reader(configArchivePath, MetadataFileName);
            return MemoryPackSerializer.Deserialize<AotRuntimeMetadata>(bytes);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static Type? ResolveTypeCore(string typeName, bool ignoreCase = false)
    {
        if (!XRRuntimeEnvironment.IsPublishedBuild)
        {
            Type? direct = Type.GetType(typeName, throwOnError: false, ignoreCase: ignoreCase);
            if (direct is not null)
                return direct;
        }

        string fullTypeName = SerializedTypeIdentity.GetUnqualifiedTypeName(typeName);

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
            if (XRRuntimeEnvironment.IsPublishedBuild && !XRRuntimeEnvironment.IsAotRuntimeBuild
                && IsBoundedConstructedTypeName(typeName))
            {
                // Cooked collections name closed CLR types; assembly scans only list their
                // open definitions. Resolve the shape only when every application type in
                // the constructed identity belongs to the published type table.
                Type? constructed = Type.GetType(typeName, throwOnError: false, ignoreCase: ignoreCase);
                int componentCount = 0;
                if (constructed is not null && (constructed.IsConstructedGenericType || constructed.IsArray)
                    && !constructed.ContainsGenericParameters
                    && IsPublishedConstructedType(constructed, metadata, 0, ref componentCount))
                    return constructed;
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
                    return found;
            }
        }

        return null;
    }

    private static string TypeNameOnly(string assemblyQualifiedName)
        => SerializedTypeIdentity.GetUnqualifiedTypeName(assemblyQualifiedName);

    private static bool IsBoundedConstructedTypeName(string typeName)
    {
        if (typeName.Length > 4096 || !typeName.Contains('['))
            return false;
        int bracketDepth = 0;
        foreach (char character in typeName)
        {
            if (character == '[' && ++bracketDepth > 32)
                return false;
            if (character == ']' && --bracketDepth < 0)
                return false;
        }
        return bracketDepth == 0;
    }

    private static bool IsPublishedConstructedType(Type type, AotRuntimeMetadata metadata, int depth,
        ref int componentCount)
    {
        if (depth > 16 || ++componentCount > 64 || type.IsPointer || type.IsByRef
            || type.IsGenericParameter || type.ContainsGenericParameters)
            return false;
        if (type.IsArray)
            return type.GetArrayRank() <= 4 && type.GetElementType() is { } element
                && IsPublishedConstructedType(element, metadata, depth + 1, ref componentCount);
        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (!IsPublishedNominalType(definition, metadata))
                return false;
            foreach (Type argument in type.GetGenericArguments())
                if (!IsPublishedConstructedType(argument, metadata, depth + 1, ref componentCount))
                    return false;
            return true;
        }
        return IsPublishedNominalType(type, metadata);
    }

    private static bool IsPublishedNominalType(Type type, AotRuntimeMetadata metadata)
    {
        string? assemblyName = type.Assembly.GetName().Name;
        if (assemblyName is "System.Private.CoreLib" or "System.Runtime" or "System.Collections"
            or "System.Collections.Concurrent" or "System.Collections.Immutable" or "System.ObjectModel"
            or "System.Numerics.Vectors" or "System.Net.Primitives")
        {
            string? name = type.FullName;
            if (type.IsGenericTypeDefinition)
                return name is not null && (name.StartsWith("System.Collections.Generic.", StringComparison.Ordinal)
                    || name.StartsWith("System.Collections.Concurrent.", StringComparison.Ordinal)
                    || name.StartsWith("System.Collections.Immutable.", StringComparison.Ordinal)
                    || name.StartsWith("System.Collections.ObjectModel.", StringComparison.Ordinal)
                    || name.StartsWith("System.Nullable`", StringComparison.Ordinal)
                    || name.StartsWith("System.Tuple`", StringComparison.Ordinal)
                    || name.StartsWith("System.ValueTuple`", StringComparison.Ordinal));
            return type.IsPrimitive || type.IsEnum || name is "System.Object" or "System.String"
                or "System.Decimal" or "System.Guid" or "System.TimeSpan" or "System.DateTime"
                or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly" or "System.Half"
                or "System.Uri" or "System.Numerics.BigInteger" or "System.Numerics.Vector2"
                or "System.Numerics.Vector3" or "System.Numerics.Vector4" or "System.Numerics.Quaternion"
                or "System.Numerics.Matrix4x4" or "System.Net.IPAddress" or "System.Net.IPEndPoint";
        }
        string? qualifiedName = type.AssemblyQualifiedName;
        return qualifiedName is not null && metadata.KnownTypeAssemblyQualifiedNames.Contains(qualifiedName, StringComparer.Ordinal);
    }
}
