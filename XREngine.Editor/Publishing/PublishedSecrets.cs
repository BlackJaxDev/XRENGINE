using System.Collections.Concurrent;
using System.Reflection;
using YamlDotNet.Serialization;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Clears the secrets of a settings asset before a publish writes it. A secret is a property marked with
/// <see cref="SecretAttribute"/>. Its environment variable companion (<c>&lt;Name&gt;EnvVar</c>), when the type has one, is
/// cleared as well. Every publish route that writes a settings asset uses this one step.
/// </summary>
/// <remarks>
/// The step clears the top-level secrets of the asset that a publish writes. It does not clear a secret of a nested
/// object, so a publish of an asset whose type reaches a secret member through its serialized members fails
/// (<see cref="ThrowIfNestedSecret"/>). The check follows the declared types of the members. A secret on a derived type
/// that a member of a base type holds at run time is not found by this check; the reflective cooked serializer writes such
/// a member as its default value at write time
/// (<see cref="XREngine.Core.Files.CookedSourcePathProjection.ProjectSecretValue"/>).
/// </remarks>
public static class PublishedSecrets
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ConcurrentDictionary<Type, SecretMember[]> Cache = new();
    private static readonly ConcurrentDictionary<Type, string?> NestedSecretCache = new();

    /// <summary>The secret properties of a type, each with its environment variable companion or null.</summary>
    /// <param name="type">The settings type.</param>
    /// <returns>The secret members.</returns>
    public static IReadOnlyList<(PropertyInfo Secret, PropertyInfo? EnvironmentVariable)> GetSecretMembers(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        SecretMember[] members = Cache.GetOrAdd(type, FindSecretMembers);
        (PropertyInfo, PropertyInfo?)[] result = new (PropertyInfo, PropertyInfo?)[members.Length];
        for (int i = 0; i < members.Length; i++)
            result[i] = (members[i].Secret, members[i].EnvironmentVariable);
        return result;
    }

    /// <summary>
    /// Clears every secret and its environment variable companion on a copy that a publish writes. Do not call it on a live
    /// settings asset.
    /// </summary>
    /// <param name="copy">The copy to write.</param>
    /// <exception cref="InvalidOperationException">
    /// A secret member has no setter, so the publish cannot clear it, or the type reaches a nested secret member.
    /// </exception>
    public static void Strip(object copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ThrowIfNestedSecret(copy.GetType());
        foreach (SecretMember member in Cache.GetOrAdd(copy.GetType(), FindSecretMembers))
        {
            // Clear the companion first: a secret getter can read the environment variable that the companion names.
            member.EnvironmentVariable?.SetValue(copy, null);
            member.Secret.SetValue(copy, member.Secret.PropertyType.IsValueType ? Activator.CreateInstance(member.Secret.PropertyType) : null);
        }
    }

    /// <summary>True if a type has a top-level secret member, which a publish clears on a copy.</summary>
    /// <param name="type">The asset type.</param>
    /// <returns>True if the type has a secret member.</returns>
    public static bool HasSecretMembers(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, FindSecretMembers).Length > 0;
    }

    /// <summary>
    /// Fails the publish if a type reaches a secret member of a nested type through its serialized members. The strip
    /// clears only top-level secrets, so a nested secret would ship.
    /// </summary>
    /// <param name="type">The type of the asset that a publish writes.</param>
    /// <exception cref="InvalidOperationException">The type reaches a nested secret member.</exception>
    public static void ThrowIfNestedSecret(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        string? path = NestedSecretCache.GetOrAdd(type, FindNestedSecret);
        if (path is not null)
            throw new InvalidOperationException(
                $"Type '{type.FullName}' reaches the secret member {path} of a nested object. A publish clears only the " +
                "top-level secrets of the asset that it writes, so it cannot write this asset. Move the secret to a top-level " +
                "member of a settings asset, or remove the [Secret] member from the nested type.");
    }

    private static SecretMember[] FindSecretMembers(Type type)
    {
        List<SecretMember> members = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            foreach (PropertyInfo property in current.GetProperties(InstanceMembers | BindingFlags.DeclaredOnly))
            {
                if (property.GetCustomAttribute<SecretAttribute>(inherit: true) is null || !names.Add(property.Name))
                    continue;
                if (property.SetMethod is null)
                    throw new InvalidOperationException(
                        $"Secret member '{type.Name}.{property.Name}' has no setter, so a publish cannot clear it.");

                PropertyInfo? companion = type.GetProperty(property.Name + "EnvVar", InstanceMembers);
                if (companion is not null && (companion.PropertyType != typeof(string) || companion.SetMethod is null))
                    companion = null;
                members.Add(new SecretMember(property, companion));
            }
        }
        return [.. members];
    }

    /// <summary>
    /// Walks the declared types of the serialized members of a type, breadth first, and gives the path to the first
    /// serialized secret member of a nested type, or null. The top-level members of the root type are not nested.
    /// </summary>
    private static string? FindNestedSecret(Type rootType)
    {
        HashSet<Type> visited = [];
        Queue<(Type Type, string Path)> pending = new();
        EnqueueMemberTypes(rootType, rootType.Name, visited, pending);
        while (pending.Count > 0)
        {
            (Type type, string path) = pending.Dequeue();
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length == 0 &&
                    property.GetCustomAttribute<SecretAttribute>(inherit: true) is not null &&
                    IsSerialized(property))
                    return $"'{type.Name}.{property.Name}' through '{path}'";
            }

            EnqueueMemberTypes(type, path, visited, pending);
        }

        return null;
    }

    private static void EnqueueMemberTypes(Type type, string path, HashSet<Type> visited, Queue<(Type, string)> pending)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is not { IsPublic: true } || !IsSerialized(property))
                continue;

            EnqueueType(property.PropertyType, path + "." + property.Name, visited, pending);
        }
    }

    /// <summary>
    /// Adds a member type to the walk. A type of the base class library or of a framework package is a leaf. The element
    /// type of an array and the generic arguments of every generic type are walked.
    /// </summary>
    private static void EnqueueType(Type type, string path, HashSet<Type> visited, Queue<(Type, string)> pending)
    {
        if (type.IsByRef || type.IsPointer)
            type = type.GetElementType()!;
        if (type.IsArray)
        {
            EnqueueType(type.GetElementType()!, path + "[]", visited, pending);
            return;
        }
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            typeof(Type).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) || type.IsGenericParameter)
            return;

        // The generic arguments of any generic type are walked, also for an engine collection such as EventList<T> that
        // does not expose its item type as a property.
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
                EnqueueType(argument, path + "[]", visited, pending);
        }

        if (IsFrameworkType(type))
            return;

        if (visited.Add(type))
            pending.Enqueue((type, path));
    }

    private static bool IsFrameworkType(Type type)
    {
        string? name = type.Assembly.GetName().Name;
        return name is not null &&
            (name.StartsWith("System", StringComparison.Ordinal) ||
             name.StartsWith("Microsoft", StringComparison.Ordinal) ||
             name.Equals("mscorlib", StringComparison.Ordinal) ||
             name.Equals("netstandard", StringComparison.Ordinal));
    }

    /// <summary>
    /// True if a publish can write the property: YAML writes it unless it has <see cref="YamlIgnoreAttribute"/>, and
    /// MemoryPack writes it on a MemoryPack type unless it has <c>MemoryPackIgnore</c>.
    /// </summary>
    private static bool IsSerialized(PropertyInfo property)
    {
        if (property.GetCustomAttribute<YamlIgnoreAttribute>(inherit: true) is null)
            return true;

        return HasAttributeNamed(property.DeclaringType!, "MemoryPackableAttribute") &&
            !HasAttributeNamed(property, "MemoryPackIgnoreAttribute");
    }

    private static bool HasAttributeNamed(MemberInfo member, string name)
    {
        foreach (object attribute in member.GetCustomAttributes(inherit: false))
            if (string.Equals(attribute.GetType().Name, name, StringComparison.Ordinal))
                return true;
        return false;
    }

    private readonly record struct SecretMember(PropertyInfo Secret, PropertyInfo? EnvironmentVariable);
}
