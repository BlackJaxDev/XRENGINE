using System.Collections.Concurrent;

namespace XREngine.Animation;

/// <summary>
/// Typed setters for animated members, registered by generated contracts or by hand. The animation
/// member consults this registry before falling back to reflective member binding, so a registered
/// target never needs reflection at runtime.
/// </summary>
public static class AnimationMemberBindingRegistry
{
    private readonly record struct BindingKey(Type TargetType, string MemberName, Type ValueType);

    private static readonly ConcurrentDictionary<BindingKey, Delegate> Setters = new();

    /// <summary>Registers a typed setter for <paramref name="memberName"/> on <typeparamref name="TTarget"/>.</summary>
    public static IDisposable Register<TTarget, TValue>(string memberName, Action<TTarget, TValue> setter)
        where TTarget : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentNullException.ThrowIfNull(setter);

        // The applier signature used by animation members takes an untyped target. Wrap once at
        // registration time so the per-frame apply path performs a single cast and no lookup.
        Action<object?, TValue> applier = (target, value) =>
        {
            if (target is TTarget typed)
                setter(typed, value);
        };

        BindingKey key = new(typeof(TTarget), memberName, typeof(TValue));
        if (!Setters.TryAdd(key, applier))
            throw new InvalidOperationException($"An animation binding for {typeof(TTarget).FullName}.{memberName} ({typeof(TValue).Name}) is already registered.");

        return new RegistrationLease(key, applier);
    }

    /// <summary>
    /// Finds a registered setter for the member, walking base types so a binding registered on a
    /// base class applies to derived targets.
    /// </summary>
    public static bool TryGetSetter<TValue>(Type targetType, string memberName, out Action<object?, TValue>? setter)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        for (Type? type = targetType; type is not null; type = type.BaseType)
        {
            if (Setters.TryGetValue(new BindingKey(type, memberName, typeof(TValue)), out Delegate? found))
            {
                setter = (Action<object?, TValue>)found;
                return true;
            }
        }

        setter = null;
        return false;
    }

    /// <summary>True when any typed setter is registered for the member on the type or one of its bases.</summary>
    public static bool IsRegistered(Type targetType, string memberName)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        foreach (BindingKey key in Setters.Keys)
        {
            if (!string.Equals(key.MemberName, memberName, StringComparison.Ordinal))
                continue;
            for (Type? type = targetType; type is not null; type = type.BaseType)
            {
                if (type == key.TargetType)
                    return true;
            }
        }

        return false;
    }

    private sealed class RegistrationLease(BindingKey key, Delegate applier) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (Setters.TryGetValue(key, out Delegate? current) && ReferenceEquals(current, applier))
                Setters.TryRemove(key, out _);
        }
    }
}
