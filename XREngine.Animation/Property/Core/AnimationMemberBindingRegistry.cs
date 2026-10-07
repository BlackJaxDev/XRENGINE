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
    private static readonly ConcurrentDictionary<(Type TargetType, string MemberName), Delegate> Getters = new();

    /// <summary>Registers a typed getter for a member on <typeparamref name="TTarget"/>.</summary>
    public static IDisposable RegisterGetter<TTarget, TValue>(string memberName, Func<TTarget, TValue> getter)
        where TTarget : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        ArgumentNullException.ThrowIfNull(getter);

        Func<object, object?> accessor = target => getter((TTarget)target);
        (Type TargetType, string MemberName) key = (typeof(TTarget), memberName);
        if (!Getters.TryAdd(key, accessor))
            throw new InvalidOperationException($"An animation getter for {typeof(TTarget).FullName}.{memberName} is already registered.");

        return new GetterRegistrationLease(key, accessor);
    }

    /// <summary>Finds a typed getter on the target type or one of its base types.</summary>
    public static bool TryGetGetter(Type targetType, string memberName, out Func<object, object?>? getter)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        for (Type? type = targetType; type is not null; type = type.BaseType)
        {
            if (Getters.TryGetValue((type, memberName), out Delegate? found))
            {
                getter = (Func<object, object?>)found;
                return true;
            }
        }

        getter = null;
        return false;
    }

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

    private sealed class GetterRegistrationLease((Type TargetType, string MemberName) key, Delegate accessor) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (Getters.TryGetValue(key, out Delegate? current) && ReferenceEquals(current, accessor))
                Getters.TryRemove(key, out _);
        }
    }
}
