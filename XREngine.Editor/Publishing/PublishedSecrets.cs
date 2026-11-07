using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using XREngine.Core.Files;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Removes the secrets of a settings asset before a publish writes it. A secret is a property marked with
/// <see cref="SecretAttribute"/>. When the type has an environment variable companion (<c>&lt;Name&gt;EnvVar</c>), the
/// publish clears it too. Every publish route that writes a settings asset uses this one step.
/// </summary>
/// <remarks>
/// The step runs once per publish, not per frame, so reflection and allocation here are acceptable.
/// </remarks>
public static class PublishedSecrets
{
    private const string ReflectionWarning = "Copying a settings asset reflects over its concrete type to build a cooked payload.";
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ConcurrentDictionary<Type, SecretMember[]> Cache = new();

    /// <summary>One secret property and its environment variable companion, or null when the type has none.</summary>
    /// <param name="Secret">The marked property.</param>
    /// <param name="EnvironmentVariable">The <c>&lt;Name&gt;EnvVar</c> string property, or null.</param>
    public readonly record struct SecretMember(PropertyInfo Secret, PropertyInfo? EnvironmentVariable);

    /// <summary>Gets the secret members of a type, including inherited members.</summary>
    /// <param name="type">The settings type.</param>
    /// <returns>The secret members. The list is empty when the type has no secret.</returns>
    public static IReadOnlyList<SecretMember> GetSecretMembers(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, FindSecretMembers);
    }

    /// <summary>
    /// Makes the copy of a settings asset that a publish writes. When the type has a secret, the copy is a cooked-binary
    /// round trip of the live asset with every secret cleared. When the type has no secret, the live asset is returned,
    /// because the write does not change it.
    /// </summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="live">The live settings asset. This method does not change it.</param>
    /// <returns>The asset to write.</returns>
    [RequiresUnreferencedCode(ReflectionWarning)]
    [RequiresDynamicCode(ReflectionWarning)]
    public static T CreatePublishedCopy<T>(T live) where T : class
    {
        ArgumentNullException.ThrowIfNull(live);
        Type runtimeType = live.GetType();
        if (GetSecretMembers(runtimeType).Count == 0)
            return live;

        byte[] payload = CookedBinarySerializer.Serialize(live);
        T copy = CookedBinarySerializer.Deserialize(runtimeType, payload) as T
            ?? throw new InvalidOperationException($"The published copy of '{runtimeType.Name}' could not be made.");
        if (ReferenceEquals(copy, live))
            throw new InvalidOperationException($"The published copy of '{runtimeType.Name}' is the live asset.");

        Strip(copy);
        return copy;
    }

    /// <summary>
    /// Clears every secret and its environment variable companion on a copy that a publish writes. Do not call it on a
    /// live settings asset.
    /// </summary>
    /// <param name="copy">The copy to write.</param>
    public static void Strip(object copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        foreach (SecretMember member in Cache.GetOrAdd(copy.GetType(), FindSecretMembers))
        {
            // Clear the companion first: a secret getter can read the environment variable that the companion names.
            member.EnvironmentVariable?.SetValue(copy, null);
            Type type = member.Secret.PropertyType;
            member.Secret.SetValue(copy, type.IsValueType ? Activator.CreateInstance(type) : null);
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
}
