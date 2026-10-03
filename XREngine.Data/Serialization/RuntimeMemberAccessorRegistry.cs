namespace XREngine;

/// <summary>Leased, generated accessors for player-visible properties, fields, methods, and events.</summary>
public static class RuntimeMemberAccessorRegistry
{
    private readonly record struct Key(Type Target, string Name, string Kind);
    private sealed record Entry(Guid Token, Type[] Parameters, Delegate? Getter, Delegate? Setter, Action<object, object?[]>? Invoke,
        Action<object, Delegate>? Subscribe, Action<object, Delegate>? Unsubscribe);

    private static readonly object Sync = new();
    private static readonly Dictionary<Key, Entry> Entries = [];

    public static IDisposable RegisterProperty<TTarget, TValue>(string name, Func<TTarget, TValue> get, Action<TTarget, TValue>? set)
        where TTarget : class
        => Register(new(typeof(TTarget), name, "property"), new(Guid.NewGuid(), [],
            (Func<object, object?>)(target => get((TTarget)target)),
            set is null ? null : (Action<object, object?>)((target, value) => set((TTarget)target, (TValue)value!)), null, null, null));

    public static IDisposable RegisterField<TTarget, TValue>(string name, Func<TTarget, TValue> get, Action<TTarget, TValue>? set)
        where TTarget : class
        => Register(new(typeof(TTarget), name, "field"), new(Guid.NewGuid(), [],
            (Func<object, object?>)(target => get((TTarget)target)),
            set is null ? null : (Action<object, object?>)((target, value) => set((TTarget)target, (TValue)value!)), null, null, null));

    public static IDisposable RegisterMethod<TTarget>(string name, Type[] parameters, Action<TTarget, object?[]> invoke)
        where TTarget : class
        => Register(new(typeof(TTarget), name, "method"), new(Guid.NewGuid(), (Type[])parameters.Clone(), null, null,
            (target, arguments) => invoke((TTarget)target, arguments), null, null));

    public static IDisposable RegisterEvent<TTarget, THandler>(string name, Action<TTarget, THandler> subscribe, Action<TTarget, THandler> unsubscribe)
        where TTarget : class where THandler : Delegate
        => Register(new(typeof(TTarget), name, "event"), new(Guid.NewGuid(), [], null, null, null,
            (target, handler) => subscribe((TTarget)target, (THandler)handler),
            (target, handler) => unsubscribe((TTarget)target, (THandler)handler)));

    public static bool TryGet(object target, string name, out object? value)
    {
        if (Find(target.GetType(), name, "property") is { Getter: Func<object, object?> propertyGet })
        {
            value = propertyGet(target);
            return true;
        }
        if (Find(target.GetType(), name, "field") is { Getter: Func<object, object?> fieldGet })
        {
            value = fieldGet(target);
            return true;
        }
        value = null;
        return false;
    }

    public static bool TrySet(object target, string name, object? value)
    {
        Entry? entry = Find(target.GetType(), name, "property") ?? Find(target.GetType(), name, "field");
        if (entry?.Setter is not Action<object, object?> setter)
            return false;
        setter(target, value);
        return true;
    }

    public static bool TryInvoke(object target, string name, Type[]? parameterTypes, object?[] arguments)
    {
        Entry? entry = Find(target.GetType(), name, "method");
        if (entry?.Invoke is null || entry.Parameters.Length != arguments.Length)
            return false;
        if (parameterTypes is not null && !entry.Parameters.SequenceEqual(parameterTypes))
            return false;
        entry.Invoke(target, arguments);
        return true;
    }

    public static bool TrySubscribe(object target, string name, Delegate handler, bool subscribe)
    {
        Entry? entry = Find(target.GetType(), name, "event");
        Action<object, Delegate>? action = subscribe ? entry?.Subscribe : entry?.Unsubscribe;
        if (action is null)
            return false;
        action(target, handler);
        return true;
    }

    private static Entry? Find(Type type, string name, string kind)
    {
        lock (Sync)
            for (Type? current = type; current is not null; current = current.BaseType)
                if (Entries.TryGetValue(new(current, name, kind), out Entry? entry))
                    return entry;
        return null;
    }

    private static IDisposable Register(Key key, Entry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Name);
        lock (Sync)
        {
            if (Entries.ContainsKey(key))
                throw new InvalidOperationException($"Runtime accessor '{key.Target.FullName}.{key.Name}' is already installed.");
            Entries.Add(key, entry);
        }
        return new Lease(() =>
        {
            lock (Sync)
                if (Entries.TryGetValue(key, out Entry? current) && current.Token == entry.Token)
                    Entries.Remove(key);
        });
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
