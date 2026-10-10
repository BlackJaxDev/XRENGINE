using System.ComponentModel;
using System.Reflection;
using XREngine;
using XREngine.Core;
using XREngine.Core.Attributes;
using XREngine.Core.Files;
using XREngine.Networking;
using XREngine.Scene.Transforms;
using XREngine.Serialization;
using YamlDotNet.Serialization;

namespace XREngine.Publishing;

/// <summary>Builds the shared published type contract from an explicitly selected managed assembly set.</summary>
public static class AotRuntimeMetadataBuilder
{
    /// <summary>Reads the exact compiled browser closure without constructing game or engine attribute instances.</summary>
    public static AotRuntimeMetadata BuildBrowser(IEnumerable<Assembly> browserAssemblies,
        Assembly activeGameAssembly, IEnumerable<Type> additionalPublishedAssetTypes)
    {
        ArgumentNullException.ThrowIfNull(browserAssemblies);
        ArgumentNullException.ThrowIfNull(activeGameAssembly);
        ArgumentNullException.ThrowIfNull(additionalPublishedAssetTypes);
        Assembly[] selected = [.. browserAssemblies.DistinctBy(static assembly => assembly.FullName, StringComparer.Ordinal)];
        Type[] types = [.. selected.SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type.AssemblyQualifiedName is not null)
            .DistinctBy(static type => type.AssemblyQualifiedName, StringComparer.Ordinal)];
        HashSet<string> knownNames = [.. types.Select(static type => type.AssemblyQualifiedName!)];
        HashSet<string> selectedAssemblyNames = [.. selected.Select(static assembly => assembly.GetName().Name!)];
        string? gameAssemblyName = activeGameAssembly.GetName().Name;
        string[] assetNames = [.. PublishedCookedAssetRegistry.SnapshotRegisteredTypes()
            .Concat(additionalPublishedAssetTypes)
            .Where(type => type.Assembly == activeGameAssembly
                || type.Assembly.GetName().Name != gameAssemblyName
                    && selectedAssemblyNames.Contains(type.Assembly.GetName().Name ?? string.Empty))
            .Select(static type => type.AssemblyQualifiedName!)
            .Where(knownNames.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)];

        return new AotRuntimeMetadata
        {
            KnownTypeAssemblyQualifiedNames = [.. knownNames.OrderBy(static name => name, StringComparer.Ordinal)],
            PublishedRuntimeAssetTypeNames = assetNames,
            TransformTypes = [.. types
                .Where(static type => !type.IsAbstract && Inherits(type, typeof(TransformBase)))
                .Select(static type => new AotTransformTypeInfo
                {
                    AssemblyQualifiedName = type.AssemblyQualifiedName!,
                    FriendlyName = $"{BrowserDisplayName(type) ?? type.Name} ({type.Assembly.GetName().Name})",
                })
                .OrderBy(static entry => entry.AssemblyQualifiedName, StringComparer.Ordinal)],
            TypeRedirects = [.. types.SelectMany(BrowserRedirectInfos)
                .OrderBy(static entry => entry.LegacyTypeName, StringComparer.Ordinal)
                .ThenBy(static entry => entry.FullName, StringComparer.Ordinal)],
            WorldObjectReplications = [.. types
                .Where(static type => !type.IsAbstract && Inherits(type, typeof(XRWorldObjectBase)))
                .Select(BrowserReplicationInfo)
                .Where(static entry => entry is not null)
                .Cast<AotWorldObjectReplicationInfo>()
                .OrderBy(static entry => entry.AssemblyQualifiedName, StringComparer.Ordinal)],
            YamlTypeConverterTypeNames = [.. types
                .Where(static type => !type.IsAbstract && !type.IsInterface
                    && type.GetInterfaces().Any(candidate => MatchesMarker(candidate, typeof(IYamlTypeConverter)))
                    && CustomAttributeData.GetCustomAttributes(type).Any(attribute =>
                        MatchesMarker(attribute.AttributeType, typeof(YamlTypeConverterAttribute))))
                .Select(static type => type.AssemblyQualifiedName!)
                .OrderBy(static name => name, StringComparer.Ordinal)],
        };
    }

    private static bool Inherits(Type type, Type baseType)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
            if (MatchesMarker(current, baseType))
                return true;
        return false;
    }

    private static bool MatchesMarker(Type candidate, Type marker)
        => candidate.FullName == marker.FullName
            && candidate.Assembly.GetName().FullName == marker.Assembly.GetName().FullName;

    private static CustomAttributeData? InheritedAttribute(Type type, Type attributeType)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            CustomAttributeData? attribute = CustomAttributeData.GetCustomAttributes(current)
                .FirstOrDefault(candidate => MatchesMarker(candidate.AttributeType, attributeType));
            if (attribute is not null)
                return attribute;
        }
        return null;
    }

    private static string? BrowserDisplayName(Type type)
        => InheritedAttribute(type, typeof(DisplayNameAttribute))
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    private static IEnumerable<AotTypeRedirectInfo> BrowserRedirectInfos(Type type)
    {
        foreach (CustomAttributeData attribute in CustomAttributeData.GetCustomAttributes(type))
        {
            if (!MatchesMarker(attribute.AttributeType, typeof(XRTypeRedirectAttribute))
                || attribute.ConstructorArguments.Count == 0
                || attribute.ConstructorArguments[0].Value is not IReadOnlyCollection<CustomAttributeTypedArgument> names)
                continue;
            foreach (CustomAttributeTypedArgument name in names)
            {
                if (name.Value is not string legacy || string.IsNullOrWhiteSpace(legacy))
                    continue;
                yield return new AotTypeRedirectInfo
                {
                    LegacyTypeName = legacy.Trim(), FullName = type.FullName ?? type.Name,
                    AssemblyQualifiedName = type.AssemblyQualifiedName,
                };
            }
        }
    }

    private static AotWorldObjectReplicationInfo? BrowserReplicationInfo(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        List<string> onChange = [];
        List<string> onTick = [];
        HashSet<string> compressed = new(StringComparer.Ordinal);
        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            CustomAttributeData? change = InheritedPropertyAttribute(property, typeof(ReplicateOnChangeAttribute));
            CustomAttributeData? tick = InheritedPropertyAttribute(property, typeof(ReplicateOnTickAttribute));
            if (change is not null) onChange.Add(property.Name);
            if (tick is not null) onTick.Add(property.Name);
            if (IsCompressed(change) || IsCompressed(tick))
                compressed.Add(property.Name);
        }
        if (onChange.Count == 0 && onTick.Count == 0)
            return null;
        return new AotWorldObjectReplicationInfo
        {
            AssemblyQualifiedName = type.AssemblyQualifiedName!,
            ReplicateOnChangeProperties = [.. onChange.Distinct(StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal)],
            ReplicateOnTickProperties = [.. onTick.Distinct(StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal)],
            CompressedPropertyNames = [.. compressed.OrderBy(static name => name, StringComparer.Ordinal)],
        };
    }

    public static AotRuntimeMetadata Build(IEnumerable<Assembly> assemblies,
        IEnumerable<Type>? additionalPublishedAssetTypes = null,
        bool restrictPublishedAssetTypesToSelectedAssemblies = false)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        Assembly[] selected = [.. assemblies.DistinctBy(static assembly => assembly.FullName, StringComparer.Ordinal)];
        HashSet<Assembly> selectedSet = [.. selected];
        Type[] allTypes = [.. selected
            .SelectMany(static assembly => XRLoadableTypeCatalog.GetTypes(assembly))
            .Concat(restrictPublishedAssetTypesToSelectedAssemblies
                ? NetworkingAotContractRegistry.ContractTypes.Where(type => selectedSet.Contains(type.Assembly))
                : NetworkingAotContractRegistry.ContractTypes)
            .Where(static type => type.AssemblyQualifiedName is not null)
            .DistinctBy(static type => type.AssemblyQualifiedName, StringComparer.Ordinal)];
        string[] registeredAssetNames = PublishedCookedAssetRegistry.SnapshotRegisteredTypeNames();

        return new AotRuntimeMetadata
        {
            KnownTypeAssemblyQualifiedNames = [.. allTypes
                .Select(static type => type.AssemblyQualifiedName!)
                .OrderBy(static name => name, StringComparer.Ordinal)],
            PublishedRuntimeAssetTypeNames = restrictPublishedAssetTypesToSelectedAssemblies
                ? [.. PublishedCookedAssetRegistry.SnapshotRegisteredTypes()
                    .Where(type => selectedSet.Contains(type.Assembly))
                    .Concat(additionalPublishedAssetTypes ?? [])
                    .Where(type => selectedSet.Contains(type.Assembly))
                    .Select(static type => type.AssemblyQualifiedName!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static name => name, StringComparer.Ordinal)]
                : registeredAssetNames,
            TransformTypes = [.. allTypes
                .Where(static type => !type.IsAbstract && type.IsSubclassOf(typeof(TransformBase)))
                .Select(static type => new AotTransformTypeInfo
                {
                    AssemblyQualifiedName = type.AssemblyQualifiedName!,
                    FriendlyName = $"{type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? type.Name} ({type.Assembly.GetName().Name})",
                })
                .OrderBy(static entry => entry.AssemblyQualifiedName, StringComparer.Ordinal)],
            TypeRedirects = [.. allTypes.SelectMany(BuildRedirectInfos)
                .OrderBy(static entry => entry.LegacyTypeName, StringComparer.Ordinal)
                .ThenBy(static entry => entry.FullName, StringComparer.Ordinal)],
            WorldObjectReplications = [.. allTypes
                .Where(static type => !type.IsAbstract && typeof(XRWorldObjectBase).IsAssignableFrom(type))
                .Select(BuildReplicationInfo)
                .Where(static entry => entry is not null)
                .Cast<AotWorldObjectReplicationInfo>()
                .OrderBy(static entry => entry.AssemblyQualifiedName, StringComparer.Ordinal)],
            YamlTypeConverterTypeNames = [.. allTypes
                .Where(static type => !type.IsAbstract && !type.IsInterface
                    && typeof(IYamlTypeConverter).IsAssignableFrom(type)
                    && type.GetCustomAttribute<YamlTypeConverterAttribute>() is not null)
                .Select(static type => type.AssemblyQualifiedName!)
                .OrderBy(static name => name, StringComparer.Ordinal)],
        };
    }

    private static IEnumerable<AotTypeRedirectInfo> BuildRedirectInfos(Type type)
    {
        foreach (XRTypeRedirectAttribute attribute in type.GetCustomAttributes<XRTypeRedirectAttribute>(inherit: false))
        foreach (string legacy in attribute.LegacyTypeNames ?? [])
        {
            if (string.IsNullOrWhiteSpace(legacy))
                continue;
            yield return new AotTypeRedirectInfo
            {
                LegacyTypeName = legacy.Trim(),
                FullName = type.FullName ?? type.Name,
                AssemblyQualifiedName = type.AssemblyQualifiedName,
            };
        }
    }

    private static AotWorldObjectReplicationInfo? BuildReplicationInfo(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        List<string> onChange = [];
        List<string> onTick = [];
        HashSet<string> compressed = new(StringComparer.Ordinal);
        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (property.GetCustomAttribute<ReplicateOnChangeAttribute>(true) is { } change)
            {
                onChange.Add(property.Name);
                if (change.Compress) compressed.Add(property.Name);
            }
            if (property.GetCustomAttribute<ReplicateOnTickAttribute>(true) is { } tick)
            {
                onTick.Add(property.Name);
                if (tick.Compress) compressed.Add(property.Name);
            }
        }
        if (onChange.Count == 0 && onTick.Count == 0)
            return null;
        return new AotWorldObjectReplicationInfo
        {
            AssemblyQualifiedName = type.AssemblyQualifiedName!,
            ReplicateOnChangeProperties = [.. onChange.OrderBy(static name => name, StringComparer.Ordinal)],
            ReplicateOnTickProperties = [.. onTick.OrderBy(static name => name, StringComparer.Ordinal)],
            CompressedPropertyNames = [.. compressed.OrderBy(static name => name, StringComparer.Ordinal)],
        };
    }

    private static bool IsCompressed(CustomAttributeData? attribute)
        => attribute is not null && attribute.ConstructorArguments.Count != 0
            && attribute.ConstructorArguments[0].Value is true;

    private static CustomAttributeData? InheritedPropertyAttribute(PropertyInfo property, Type attributeType)
    {
        MethodInfo? accessor = property.GetMethod ?? property.SetMethod;
        MethodInfo? baseDefinition = accessor?.GetBaseDefinition();
        for (Type? owner = property.DeclaringType; owner is not null; owner = owner.BaseType)
        {
            foreach (PropertyInfo current in owner.GetProperties(
                         BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                MethodInfo? currentAccessor = current.GetMethod ?? current.SetMethod;
                if (current.Name != property.Name || currentAccessor is null
                    || currentAccessor.GetBaseDefinition() != baseDefinition)
                    continue;
                CustomAttributeData? attribute = CustomAttributeData.GetCustomAttributes(current)
                    .FirstOrDefault(candidate => MatchesMarker(candidate.AttributeType, attributeType));
                if (attribute is not null)
                    return attribute;
            }
        }
        return null;
    }
}
