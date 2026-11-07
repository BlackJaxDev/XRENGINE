using System.Collections.Concurrent;
using System.Reflection;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Clears the secrets of a settings asset before a publish writes it. A secret is a property marked with
/// <see cref="SecretAttribute"/>. Its environment variable companion (<c>&lt;Name&gt;EnvVar</c>), when the type has one, is
/// cleared as well. Every publish route that writes a settings asset uses this one step.
/// </summary>
public static class PublishedSecrets
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ConcurrentDictionary<Type, SecretMember[]> Cache = new();

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
    /// <exception cref="InvalidOperationException">A secret member has no setter, so the publish cannot clear it.</exception>
    public static void Strip(object copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        foreach (SecretMember member in Cache.GetOrAdd(copy.GetType(), FindSecretMembers))
        {
            // Clear the companion first: a secret getter can read the environment variable that the companion names.
            member.EnvironmentVariable?.SetValue(copy, null);
            member.Secret.SetValue(copy, member.Secret.PropertyType.IsValueType ? Activator.CreateInstance(member.Secret.PropertyType) : null);
        }
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

    private readonly record struct SecretMember(PropertyInfo Secret, PropertyInfo? EnvironmentVariable);
}
