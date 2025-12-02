using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private static readonly ConcurrentDictionary<Type, List<Func<object, object?>>> AccessorCache = new();
    private static readonly ConcurrentDictionary<Type, bool> LeafTypeCache = new();
    private static readonly ConcurrentDictionary<Type, bool> InspectMemberTypeCache = new();

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

    internal static bool ShouldRefreshForPropertyChange(object? previousValue, object? newValue)
        => ContainsAssetCandidate(previousValue) || ContainsAssetCandidate(newValue);

    public static void RefreshAssetGraph(XRAsset? root)
    {
        if (root is null)
            return;

        Trace.WriteLine($"[XRAssetGraphUtility] RefreshAssetGraph START for '{root.FilePath ?? root.GetType().Name}'");
        var sw = Stopwatch.StartNew();

        if (!ReferenceEquals(root.SourceAsset, root))
            root.SourceAsset = root;

        var visitedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var discoveredAssets = new HashSet<XRAsset>(AssetReferenceComparer.Instance);

        TraverseObject(root, root, visitedObjects, discoveredAssets, 0);

        root.EmbeddedAssets.Set(discoveredAssets, reportRemoved: false, reportAdded: false, reportModified: false);

        sw.Stop();
        Trace.WriteLine($"[XRAssetGraphUtility] RefreshAssetGraph END for '{root.FilePath ?? root.GetType().Name}' - visited {visitedObjects.Count} objects, found {discoveredAssets.Count} embedded assets in {sw.ElapsedMilliseconds}ms");
    }

    private const int MaxTraversalDepth = 64;
    
    [ThreadStatic]
    private static int _traversalCount;

    private static void TraverseObject(object? candidate, XRAsset root, HashSet<object> visited, HashSet<XRAsset> embedded, int depth)
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
                Trace.WriteLine($"[XRAssetGraphUtility] Found embedded asset: {asset.GetType().Name}, root='{root.FilePath ?? root.GetType().Name}', asset FilePath='{asset.FilePath}'");
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
                TraverseObject(entry.Key, root, visited, embedded, depth);
                TraverseObject(entry.Value, root, visited, embedded, depth);
                if (++dictCount > 1000)
                {
                    Trace.WriteLine($"[XRAssetGraphUtility] Dictionary iteration limit reached for {candidateType.FullName}");
                    break;
                }
            }
            return;
        }

        if (candidate is Array array)
        {
            Type? elementType = array.GetType().GetElementType();
            if (elementType is not null && IsLeafType(elementType))
                return;
            
            // Skip large arrays (likely pixel data or similar)
            if (array.Length > 1000)
            {
                Trace.WriteLine($"[XRAssetGraphUtility] Skipping large array: {candidateType.FullName} with {array.Length} elements");
                return;
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
                TraverseObject(item, root, visited, embedded, depth);
                
                // Safety limit for very large collections
                if (++count > 1000)
                {
                    Trace.WriteLine($"[XRAssetGraphUtility] Collection iteration limit reached for {candidateType.FullName}");
                    break;
                }
            }
            return;
        }

        foreach (var getter in GetAccessors(candidateType))
        {
            object? value;
            try
            {
                value = getter(candidate);
            }
            catch
            {
                continue;
            }

            // Increment depth only for property/field traversal
            TraverseObject(value, root, visited, embedded, depth + 1);
        }
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

        // Skip system/runtime types that can't contain XRAsset references
        string? ns = type.Namespace;
        if (ns is not null && (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal)))
            return true;

        // Skip types from assemblies that aren't part of the XREngine solution
        string? assemblyName = type.Assembly.GetName().Name;
        if (assemblyName is not null && !assemblyName.StartsWith("XREngine", StringComparison.Ordinal))
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
    private static List<Func<object, object?>> GetAccessors(Type type)
        => AccessorCache.GetOrAdd(type, BuildAccessors);

    private static List<Func<object, object?>> BuildAccessors(Type t)
    {
        var accessors = new List<Func<object, object?>>();
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
                if (property.GetCustomAttribute<YamlIgnoreAttribute>() is not null)
                    continue;
                if (!ShouldInspectMemberType(property.PropertyType))
                    continue;

                accessors.Add(property.GetValue);
            }

            foreach (var field in typeInfo.DeclaredFields)
            {
                if (field.IsStatic)
                    continue;
                if (ShouldSkipMember(field.DeclaringType, field.Name))
                    continue;
                if (field.GetCustomAttribute<YamlIgnoreAttribute>() is not null)
                    continue;
                if (!ShouldInspectMemberType(field.FieldType))
                    continue;

                accessors.Add(field.GetValue);
            }
        }

        return accessors;
    }

    private static bool ShouldSkipMember(Type? declaringType, string memberName)
    {
        if (declaringType is null)
            return false;

        if (!XRAssetType.IsAssignableFrom(declaringType))
            return false;

        return InfrastructureMembers.Contains(memberName);
    }

    private static bool ShouldInspectMemberType(Type? memberType)
    {
        if (memberType is null)
            return false;

        return InspectMemberTypeCache.GetOrAdd(memberType, static t =>
        {
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
