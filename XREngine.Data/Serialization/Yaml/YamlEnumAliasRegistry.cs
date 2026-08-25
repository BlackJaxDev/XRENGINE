using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Serialization;

/// <summary>Lease-based compatibility aliases for feature-owned enum values.</summary>
public static class YamlEnumAliasRegistry
{
    private static readonly object Sync = new();
    private static Alias[] _aliases = [];

    public static IDisposable Install<TEnum>(string ownerName, string legacyName, TEnum replacement)
        where TEnum : struct, Enum
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyName);
        Alias alias = new(ownerName, typeof(TEnum), legacyName, replacement);

        lock (Sync)
        {
            Alias? existing = Array.Find(
                _aliases,
                candidate => candidate.EnumType == alias.EnumType
                    && string.Equals(candidate.LegacyName, legacyName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                throw new InvalidOperationException(
                    $"YAML enum alias '{typeof(TEnum).FullName}.{legacyName}' is owned by both " +
                    $"'{existing.OwnerName}' and '{ownerName}'.");
            }

            Volatile.Write(ref _aliases, [.. _aliases, alias]);
        }

        return new AliasLease(alias);
    }

    internal static bool TryResolve(Type enumType, string name, out object? replacement)
    {
        foreach (Alias alias in Volatile.Read(ref _aliases))
        {
            if (alias.EnumType == enumType
                && string.Equals(alias.LegacyName, name, StringComparison.OrdinalIgnoreCase))
            {
                replacement = alias.Replacement;
                return true;
            }
        }

        replacement = null;
        return false;
    }

    private sealed record Alias(string OwnerName, Type EnumType, string LegacyName, object Replacement);

    private sealed class AliasLease(Alias alias) : IDisposable
    {
        private Alias? _alias = alias;

        public void Dispose()
        {
            Alias? current = Interlocked.Exchange(ref _alias, null);
            if (current is null)
                return;

            lock (Sync)
            {
                int index = Array.FindIndex(_aliases, candidate => ReferenceEquals(candidate, current));
                if (index < 0)
                    return;

                Alias[] updated = new Alias[_aliases.Length - 1];
                if (index > 0)
                    Array.Copy(_aliases, 0, updated, 0, index);
                if (index < _aliases.Length - 1)
                    Array.Copy(_aliases, index + 1, updated, index, _aliases.Length - index - 1);
                Volatile.Write(ref _aliases, updated);
            }
        }
    }
}

/// <summary>Consumes explicitly registered legacy enum aliases before YamlDotNet parses enum names.</summary>
public sealed class YamlEnumAliasNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser reader,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value,
        ObjectDeserializer rootDeserializer)
    {
        Type targetType = Nullable.GetUnderlyingType(expectedType) ?? expectedType;
        if (!targetType.IsEnum
            || !reader.Accept<Scalar>(out Scalar? scalar)
            || !YamlEnumAliasRegistry.TryResolve(targetType, scalar.Value, out value))
        {
            value = null;
            return false;
        }

        reader.Consume<Scalar>();
        return true;
    }
}
