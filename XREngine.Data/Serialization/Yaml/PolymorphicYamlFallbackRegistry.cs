using System.Collections.Concurrent;
using YamlDotNet.Core.Events;

namespace XREngine;

/// <summary>
/// Lower YAML registry for owner-provided legacy polymorphic fallbacks. Feature assemblies
/// install their mappings explicitly and retain the returned leases.
/// </summary>
public static class PolymorphicYamlFallbackRegistry
{
    private static readonly ConcurrentDictionary<Type, Registration> Registrations = new();

    public static IDisposable Install(Type expectedType, Type concreteType)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        ArgumentNullException.ThrowIfNull(concreteType);
        return Install(expectedType, _ => concreteType);
    }

    public static IDisposable Install(Type expectedType, Func<IReadOnlyList<ParsingEvent>, Type?> resolver)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        ArgumentNullException.ThrowIfNull(resolver);

        Registration registration = new(resolver);
        if (!Registrations.TryAdd(expectedType, registration))
            throw new InvalidOperationException($"A polymorphic YAML fallback is already installed for '{expectedType.FullName}'.");

        return new RegistrationLease(expectedType, registration);
    }

    public static bool TryResolve(
        Type expectedType,
        IReadOnlyList<ParsingEvent> events,
        out Type? concreteType)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        ArgumentNullException.ThrowIfNull(events);

        if (Registrations.TryGetValue(expectedType, out Registration? registration))
        {
            concreteType = registration.Resolver(events);
            return concreteType is not null;
        }

        concreteType = null;
        return false;
    }

    private sealed record Registration(Func<IReadOnlyList<ParsingEvent>, Type?> Resolver);

    private sealed class RegistrationLease(Type expectedType, Registration registration) : IDisposable
    {
        private Registration? _registration = registration;

        public void Dispose()
        {
            Registration? current = Interlocked.Exchange(ref _registration, null);
            if (current is not null)
                Registrations.TryRemove(new KeyValuePair<Type, Registration>(expectedType, current));
        }
    }
}
