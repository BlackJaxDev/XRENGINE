using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using MemoryPack;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Core.Files;

#pragma warning disable IL2026 // Trimming not supported for reflection graph inspection
#pragma warning disable IL2055
#pragma warning disable IL2070
#pragma warning disable IL2072
#pragma warning disable IL2075

/// <summary>
/// Keeps <see cref="XRAsset.SourceAsset"/> and <see cref="XRAsset.EmbeddedAssets"/> in sync with the
/// actual object graph that will be serialized to YAML.
/// </summary>
public static class XRAssetGraphUtility
{
    private static readonly ConcurrentDictionary<Type, List<AssetGraphAccessor>> AccessorCache = new();
    private static readonly ConcurrentDictionary<Type, bool> LeafTypeCache = new();
    private static readonly ConcurrentDictionary<Type, bool> InspectMemberTypeCache = new();
    private static readonly ConcurrentDictionary<(Type OwnerType, string MemberName), bool> SerializedMemberRefreshCache = new();

    private static readonly HashSet<string> InfrastructureMembers = new(StringComparer.Ordinal)
    {
        nameof(XRAsset.SourceAsset),
        nameof(XRAsset.EmbeddedAssets),
        nameof(XRAsset.FilePath),
        nameof(XRAsset.OriginalPath),
        nameof(XRAsset.OriginalLastWriteTimeUtc),
        nameof(XRAsset.SerializedAssetType),
        nameof(XRAsset.Reloaded),
        nameof(XRAsset.FileMapStream),
        "FileMap"
    };

    private static readonly Type XRAssetType = typeof(XRAsset);

    internal static bool ShouldRefreshForPropertyChange(Type ownerType, string? propertyName, object? previousValue, object? newValue)
    {
        if (!string.IsNullOrWhiteSpace(propertyName) && !ShouldRefreshSerializedMember(ownerType, propertyName))
            return false;

        return ContainsAssetCandidate(previousValue) || ContainsAssetCandidate(newValue);
    }

    private static bool ShouldRefreshSerializedMember(Type ownerType, string memberName)
        => SerializedMemberRefreshCache.GetOrAdd((ownerType, memberName), static key => IsSerializedMember(key.OwnerType, key.MemberName));

    private static bool IsSerializedMember(Type ownerType, string memberName)
    {
        for (Type? current = ownerType; current is not null; current = current.BaseType)
        {
            PropertyInfo? property = current.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null)
                return !IsSerializationIgnored(property);

            FieldInfo? field = current.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
                return !IsSerializationIgnored(field);
        }

        return true;
    }

    public static void RefreshAssetGraph(XRAsset? root)
    {
        if (root is null)
            return;

        //Trace.WriteLine($"[XRAssetGraphUtility] RefreshAssetGraph START for '{root.FilePath ?? root.GetType().Name}'");
        var sw = Stopwatch.StartNew();

        if (!ReferenceEquals(root.SourceAsset, root))
            root.SourceAsset = root;

        var visitedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var discoveredAssets = new HashSet<XRAsset>(AssetReferenceComparer.Instance);

        _traversalCount = 0;
        TraverseObject(
            root,
            root,
            visitedObjects,
            discoveredAssets,
            0,
            root.GetType().Name);

        root.EmbeddedAssets.Set(discoveredAssets, reportRemoved: false, reportAdded: false, reportModified: false);

        sw.Stop();
        //Trace.WriteLine($"[XRAssetGraphUtility] RefreshAssetGraph END for '{root.FilePath ?? root.GetType().Name}' - visited {visitedObjects.Count} objects, found {discoveredAssets.Count} embedded assets in {sw.ElapsedMilliseconds}ms");
    }

    private const int MaxTraversalDepth = 64;
    private const int MaxTraversalCount = 100_000;
    
    [ThreadStatic]
    private static int _traversalCount;

    private static void TraverseObject(
        object? candidate,
        XRAsset root,
        HashSet<object> visited,
        HashSet<XRAsset> embedded,
        int depth,
        string path)
    {
        if (candidate is null)
            return;

        Type candidateType = candidate.GetType();
        
        // Check leaf type FIRST before anything else
        if (IsLeafType(candidateType))
            return;

        _traversalCount++;
        if (_traversalCount % 10000 == 0)
        {
            Trace.WriteLine($"[XRAssetGraphUtility] TraverseObject count={_traversalCount}, depth={depth}, visited={visited.Count}, type={candidateType.FullName}");
        }

        if (_traversalCount > MaxTraversalCount)
        {
            Trace.WriteLine($"[XRAssetGraphUtility] Hard traversal limit {MaxTraversalCount} reached for asset='{root.FilePath ?? root.GetType().Name}', aborting");
            return;
        }

        if (depth > MaxTraversalDepth)
        {
            Trace.WriteLine($"[XRAssetGraphUtility] Max depth {MaxTraversalDepth} exceeded at type={candidateType.FullName}, asset='{root.FilePath}'");
            return;
        }

        if (!visited.Add(candidate))
            return;

        if (candidate is XRAsset asset)
        {
            if (!ReferenceEquals(asset, root))
            {
                // If this XRAsset is a reference to an external asset file, it should NOT become embedded
                // in the current root's asset graph.
                if (IsExternalAssetReference(asset))
                    return;

                //Trace.WriteLine($"[XRAssetGraphUtility] Found embedded asset: {asset.GetType().Name}, root='{root.FilePath ?? root.GetType().Name}', asset FilePath='{asset.FilePath}'");
                if (!ReferenceEquals(asset.SourceAsset, root))
                    asset.SourceAsset = root;

                embedded.Add(asset);
            }
        }

        if (candidate is IDictionary dictionary)
        {
            int dictCount = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                // Don't increment depth for collection iteration - only for property traversal
                TraverseObject(
                    entry.Key,
                    root,
                    visited,
                    embedded,
                    depth,
                    $"{path}[key:{dictCount}]");
                TraverseObject(
                    entry.Value,
                    root,
                    visited,
                    embedded,
                    depth,
                    $"{path}[value:{dictCount}]");
                if (++dictCount > 1000)
                {
                    throw new InvalidDataException(
                        $"Authored asset graph exceeded the 1,000-entry dictionary safety limit at " +
                        $"'{path}' ({candidateType.FullName}); graph completion was affected.");
                }
            }
            return;
        }

        if (candidate is Array array)
        {
            Type? elementType = array.GetType().GetElementType();
            if (elementType is not null && IsLeafType(elementType))
                return;
            
            // Large leaf arrays were returned above. A remaining array can carry
            // authored assets, so silently skipping it would publish a partial graph.
            if (array.Length > 1000)
            {
                string message =
                    $"Authored asset graph cannot safely inspect large array at '{path}': " +
                    $"type={candidateType.FullName} elements={array.Length}; graph completion was affected.";
                Trace.WriteLine($"[XRAssetGraphUtility] {message}");
                throw new InvalidDataException(message);
            }
        }

        if (candidate is IEnumerable enumerable and not string)
        {
            var elementType = GetEnumerableElementType(candidateType);
            if (elementType is not null && IsLeafType(elementType))
                return;

            int count = 0;
            foreach (var item in enumerable)
            {
                // Don't increment depth for collection iteration
                TraverseObject(
                    item,
                    root,
                    visited,
                    embedded,
                    depth,
                    $"{path}[{count}]");
                
                // Safety limit for very large collections
                if (++count > 1000)
                {
                    throw new InvalidDataException(
                        $"Authored asset graph exceeded the 1,000-entry collection safety limit at " +
                        $"'{path}' ({candidateType.FullName}); graph completion was affected.");
                }
            }
            return;
        }

        foreach (AssetGraphAccessor accessor in GetAccessors(candidateType))
        {
            object? value;
            try
            {
                value = accessor.Getter(candidate);
            }
            catch
            {
                continue;
            }

            // Increment depth only for property/field traversal
            TraverseObject(
                value,
                root,
                visited,
                embedded,
                depth + 1,
                $"{path}.{accessor.Name}");
        }
    }

    private static bool IsExternalAssetReference(XRAsset asset)
    {
        // External assets are self-rooted and have a real file backing them.
        // If they are referenced from another asset, they should serialize as { ID: <guid> } only.
        if (!ReferenceEquals(asset.SourceAsset, asset))
            return false;

        string? path = asset.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return File.Exists(path);
    }

    private static bool IsLeafType(Type type)
        => LeafTypeCache.GetOrAdd(type, IsLeafTypeCore);

    private static bool IsLeafTypeCore(Type type)
    {
        // Primitives, enums, pointers, and value types (structs) can't reference XRAsset
        if (type.IsPrimitive || type.IsEnum || type.IsPointer || type.IsValueType)
            return true;
        if (type == typeof(string) || type == typeof(Type))
            return true;

        // Vertex/VertexData are pure geometry data holders that can never reference XRAsset.
        // They live in XREngine.Runtime.Rendering (cross-assembly, so checked by name).
        if (type.Name is "Vertex" or "VertexData" && type.Namespace == "XREngine.Data.Rendering")
            return true;

        // Skip system/runtime types that can't contain XRAsset references
        string? ns = type.Namespace;
        if (ns is not null && (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal)))
            return true;

        // Skip types from assemblies that aren't part of the XREngine solution
        string? assemblyName = type.Assembly.GetName().Name;
        if (assemblyName is not null && !assemblyName.StartsWith("XREngine", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool ContainsAssetCandidate(object? value)
    {
        if (value is null)
            return false;

        if (value is XRAsset)
            return true;

        Type valueType = value.GetType();

        if (valueType.IsArray)
        {
            Type? element = valueType.GetElementType();
            return element is not null && XRAssetType.IsAssignableFrom(element);
        }

        if (!typeof(IEnumerable).IsAssignableFrom(valueType))
            return false;

        if (value is string)
            return false;

        if (value is IDictionary)
        {
            foreach (DictionaryEntry entry in (IDictionary)value)
            {
                if (ContainsAssetCandidate(entry.Key) || ContainsAssetCandidate(entry.Value))
                    return true;
            }

            return false;
        }

        if (valueType.IsGenericType)
        {
            foreach (Type argument in valueType.GetGenericArguments())
            {
                if (XRAssetType.IsAssignableFrom(argument))
                    return true;
            }
        }

        foreach (var item in (IEnumerable)value)
        {
            if (ContainsAssetCandidate(item))
                return true;
        }

        return false;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Graph inspection requires runtime reflection.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Graph inspection requires runtime reflection.")]
    private static List<AssetGraphAccessor> GetAccessors(Type type)
        => AccessorCache.GetOrAdd(type, BuildAccessors);

    private static List<AssetGraphAccessor> BuildAccessors(Type t)
    {
        var accessors = new List<AssetGraphAccessor>();
        for (Type? current = t; current is not null; current = current.BaseType)
        {
            var typeInfo = current.GetTypeInfo();

            foreach (var property in typeInfo.DeclaredProperties)
            {
                if (!property.CanRead)
                    continue;
                if (property.GetIndexParameters().Length != 0)
                    continue;
                if (property.GetMethod?.IsStatic == true)
                    continue;
                if (ShouldSkipMember(property.DeclaringType, property.Name))
                    continue;
                if (IsSerializationIgnored(property))
                    continue;
                if (!ShouldInspectMemberType(property.PropertyType))
                    continue;

                accessors.Add(new AssetGraphAccessor(property.Name, property.GetValue));
            }

            foreach (var field in typeInfo.DeclaredFields)
            {
                if (field.IsStatic)
                    continue;
                if (ShouldSkipMember(field.DeclaringType, field.Name))
                    continue;
                if (IsSerializationIgnored(field))
                    continue;
                if (!ShouldInspectMemberType(field.FieldType))
                    continue;

                accessors.Add(new AssetGraphAccessor(field.Name, field.GetValue));
            }
        }

        return accessors;
    }

    private readonly record struct AssetGraphAccessor(
        string Name,
        Func<object, object?> Getter);

    private static bool ShouldSkipMember(Type? declaringType, string memberName)
    {
        if (declaringType is null)
            return false;

        if (!XRAssetType.IsAssignableFrom(declaringType))
            return false;

        return InfrastructureMembers.Contains(memberName);
    }

    /// <summary>
    /// Returns whether a reflected member is excluded from persisted asset state.
    /// The graph walker must use the same transient-state boundary as cooked binary
    /// serialization; otherwise runtime publishers, event handlers, and GPU state
    /// are mistaken for embedded assets.
    /// </summary>
    private static bool IsSerializationIgnored(MemberInfo member)
    {
        if (member.GetCustomAttribute<YamlIgnoreAttribute>() is not null ||
            member.GetCustomAttribute<MemoryPackIgnoreAttribute>() is not null ||
            member.GetCustomAttribute<RuntimeOnlyAttribute>() is not null)
        {
            return true;
        }

        // Reflection exposes an auto-property and its compiler-generated backing
        // field as two independent members. Persistence attributes are normally
        // authored on the property, so inherit that boundary when considering the
        // backing field or transient runtime graphs can leak back into traversal.
        if (member is not FieldInfo field ||
            field.GetCustomAttribute<CompilerGeneratedAttribute>() is null ||
            !TryGetAutoPropertyName(field.Name, out string? propertyName))
        {
            return false;
        }

        PropertyInfo? property = field.DeclaringType?.GetProperty(
            propertyName,
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);
        return property is not null && IsSerializationIgnored(property);
    }

    private static bool TryGetAutoPropertyName(
        string fieldName,
        [NotNullWhen(true)] out string? propertyName)
    {
        const string suffix = ">k__BackingField";
        if (fieldName.Length <= suffix.Length + 1 ||
            fieldName[0] != '<' ||
            !fieldName.EndsWith(suffix, StringComparison.Ordinal))
        {
            propertyName = null;
            return false;
        }

        propertyName = fieldName[1..^suffix.Length];
        return propertyName.Length != 0;
    }

    private static bool ShouldInspectMemberType(Type? memberType)
    {
        if (memberType is null)
            return false;

        return InspectMemberTypeCache.GetOrAdd(memberType, static t =>
        {
            if (t.GetCustomAttribute<RuntimeOnlyAttribute>(inherit: true) is not null)
                return false;

            if (XRAssetType.IsAssignableFrom(t))
                return true;

            if (typeof(XRObjectBase).IsAssignableFrom(t))
                return true;

            if (typeof(IDictionary).IsAssignableFrom(t))
            {
                if (t.IsGenericType)
                {
                    foreach (var arg in t.GetGenericArguments())
                    {
                        if (XRAssetType.IsAssignableFrom(arg) || typeof(XRObjectBase).IsAssignableFrom(arg))
                            return true;
                    }
                }
                // Unknown dictionary contents - inspect conservatively
                return true;
            }

            if (typeof(IEnumerable).IsAssignableFrom(t))
            {
                if (t == typeof(string))
                    return false;

                if (t.IsArray)
                {
                    var elementType = t.GetElementType();
                    return elementType is not null && ShouldInspectMemberType(elementType);
                }

                if (t.IsGenericType)
                {
                    foreach (var arg in t.GetGenericArguments())
                    {
                        if (ShouldInspectMemberType(arg))
                            return true;
                    }
                }

                // Non-generic IEnumerable - inspect to be safe
                return true;
            }

            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    if (ShouldInspectMemberType(arg))
                        return true;
                }
            }

            return false;
        });
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    private sealed class AssetReferenceComparer : IEqualityComparer<XRAsset>
    {
        public static readonly AssetReferenceComparer Instance = new();

        public bool Equals(XRAsset? x, XRAsset? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(XRAsset obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
