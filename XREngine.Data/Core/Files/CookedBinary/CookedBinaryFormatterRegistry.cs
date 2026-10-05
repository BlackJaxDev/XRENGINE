namespace XREngine.Core.Files;

/// <summary>Closed collection and tuple constructors rooted by generated contracts.</summary>
public static class CookedBinaryFormatterRegistry
{
    private sealed record Entry(Guid Token, Type Type, Func<object> Create);
    private sealed record ArrayEntry(Guid Token, Func<int, Array> Create);
    private sealed record ValueDefaultEntry(Guid Token, Func<object> Create);
    private sealed record HashSetEntry(Guid Token, Type ElementType, Func<int, object> Create, Action<object, object?> Add);
    private sealed record TupleEntry(Guid Token, Type[] Elements, Func<object?[], object> Create);

    private static readonly object Sync = new();
    private static readonly Dictionary<Type, Entry> Collections = [];
    private static readonly Dictionary<Type, ArrayEntry> Arrays = [];
    private static readonly Dictionary<Type, ValueDefaultEntry> ValueDefaults = [];
    private static readonly Dictionary<Type, HashSetEntry> HashSets = [];
    private static readonly Dictionary<string, TupleEntry> Tuples = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, Entry> Nullables = [];
    private static readonly Dictionary<string, Entry> KnownTypes = new(StringComparer.Ordinal);

    public static IDisposable RegisterKnownType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        string name = type.FullName ?? throw new ArgumentException("Closed formatter type must have a full name.", nameof(type));
        Entry entry = new(Guid.NewGuid(), type, static () => throw new NotSupportedException());
        lock (Sync)
        {
            if (KnownTypes.ContainsKey(name))
                throw new InvalidOperationException($"Closed formatter type '{name}' is already registered.");
            KnownTypes.Add(name, entry);
        }
        return new Lease(() =>
        {
            lock (Sync)
                if (KnownTypes.TryGetValue(name, out Entry? current) && current.Token == entry.Token)
                    KnownTypes.Remove(name);
        });
    }

    public static bool TryResolve(string fullName, out Type? type, bool ignoreCase = false)
    {
        lock (Sync)
        {
            if (KnownTypes.TryGetValue(fullName, out Entry? entry))
            {
                type = entry.Type;
                return true;
            }
            if (ignoreCase)
            {
                foreach (Entry candidate in KnownTypes.Values)
                {
                    if (string.Equals(candidate.Type.FullName, fullName, StringComparison.OrdinalIgnoreCase))
                    {
                        type = candidate.Type;
                        return true;
                    }
                }
            }
        }
        type = null;
        return false;
    }

    public static IDisposable RegisterList<T>()
        => RegisterCollection(typeof(List<T>), static () => new List<T>());

    /// <summary>Registers construction of one closed vector array, including an array whose element is another array.</summary>
    public static IDisposable RegisterArray<T>()
    {
        Type arrayType = typeof(T[]);
        ArrayEntry entry = new(Guid.NewGuid(), static length => new T[length]);
        lock (Sync)
        {
            if (Arrays.ContainsKey(arrayType))
                throw new InvalidOperationException($"Array formatter for '{arrayType}' is already registered.");
            Arrays.Add(arrayType, entry);
        }
        return new Lease(() =>
        {
            lock (Sync)
                if (Arrays.TryGetValue(arrayType, out ArrayEntry? current) && current.Token == entry.Token)
                    Arrays.Remove(arrayType);
        });
    }

    public static bool TryCreateArray(Type arrayType, int length, out Array? array)
    {
        ArgumentNullException.ThrowIfNull(arrayType);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        lock (Sync)
        {
            if (Arrays.TryGetValue(arrayType, out ArrayEntry? entry))
            {
                array = entry.Create(length);
                return true;
            }
        }
        array = null;
        return false;
    }

    /// <summary>Registers the language default of a closed value type without invoking its constructor.</summary>
    public static IDisposable RegisterValueDefault<T>() where T : struct
    {
        Type type = typeof(T);
        ValueDefaultEntry entry = new(Guid.NewGuid(), static () => default(T));
        lock (Sync)
        {
            if (ValueDefaults.ContainsKey(type))
                throw new InvalidOperationException($"Value default for '{type}' is already registered.");
            ValueDefaults.Add(type, entry);
        }
        return new Lease(() =>
        {
            lock (Sync)
                if (ValueDefaults.TryGetValue(type, out ValueDefaultEntry? current) && current.Token == entry.Token)
                    ValueDefaults.Remove(type);
        });
    }

    public static bool TryCreateValueDefault(Type type, out object? value)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (Sync)
        {
            if (ValueDefaults.TryGetValue(type, out ValueDefaultEntry? entry))
            {
                value = entry.Create();
                return true;
            }
        }
        value = null;
        return false;
    }

    public static IDisposable RegisterDictionary<TKey, TValue>() where TKey : notnull
        => RegisterCollection(typeof(Dictionary<TKey, TValue>), static () => new Dictionary<TKey, TValue>());

    public static IDisposable RegisterNullable<T>() where T : struct
    {
        Type nullableType = typeof(T?);
        Entry entry = new(Guid.NewGuid(), nullableType, static () => typeof(T?));
        lock (Sync)
        {
            if (Nullables.ContainsKey(typeof(T)))
                throw new InvalidOperationException($"Nullable formatter for '{typeof(T)}' is already registered.");
            Nullables.Add(typeof(T), entry);
        }
        return new Lease(() =>
        {
            lock (Sync)
                if (Nullables.TryGetValue(typeof(T), out Entry? current) && current.Token == entry.Token)
                    Nullables.Remove(typeof(T));
        });
    }

    public static IDisposable RegisterHashSet<T>()
    {
        HashSetEntry entry = new(Guid.NewGuid(), typeof(T), static count => new HashSet<T>(count),
            static (set, item) => ((HashSet<T>)set).Add((T)item!));
        lock (Sync)
        {
            if (HashSets.ContainsKey(typeof(T)))
                throw new InvalidOperationException($"HashSet formatter for '{typeof(T)}' is already registered.");
            HashSets.Add(typeof(T), entry);
        }
        return new Lease(() =>
        {
            lock (Sync)
                if (HashSets.TryGetValue(typeof(T), out HashSetEntry? current) && current.Token == entry.Token)
                    HashSets.Remove(typeof(T));
        });
    }

    public static IDisposable RegisterTuple(Type[] elements, Func<object?[], object> create)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(create);
        Type[] copy = (Type[])elements.Clone();
        string key = TupleKey(copy);
        TupleEntry entry = new(Guid.NewGuid(), copy, create);
        lock (Sync)
        {
            if (Tuples.ContainsKey(key))
                throw new InvalidOperationException($"Tuple formatter '{key}' is already registered.");
            Tuples.Add(key, entry);
        }
        return new Lease(() =>
        {
            lock (Sync)
                if (Tuples.TryGetValue(key, out TupleEntry? current) && current.Token == entry.Token)
                    Tuples.Remove(key);
        });
    }

    public static bool TryCreateCollection(Type type, out object? collection)
    {
        lock (Sync)
        {
            if (Collections.TryGetValue(type, out Entry? entry))
            {
                collection = entry.Create();
                return true;
            }
        }
        collection = null;
        return false;
    }

    public static bool TryCreateHashSet(Type elementType, int count, out object? set, out Action<object, object?>? add)
    {
        lock (Sync)
        {
            if (HashSets.TryGetValue(elementType, out HashSetEntry? entry))
            {
                set = entry.Create(count);
                add = entry.Add;
                return true;
            }
        }
        set = null;
        add = null;
        return false;
    }

    public static bool TryCreateTuple(Type[] elements, object?[] values, out object? tuple)
    {
        lock (Sync)
        {
            if (Tuples.TryGetValue(TupleKey(elements), out TupleEntry? entry))
            {
                tuple = entry.Create(values);
                return true;
            }
        }
        tuple = null;
        return false;
    }

    public static bool IsNullableRegistered(Type underlying)
    {
        lock (Sync)
            return Nullables.ContainsKey(underlying);
    }

    private static IDisposable RegisterCollection(Type type, Func<object> create)
    {
        Entry entry = new(Guid.NewGuid(), type, create);
        lock (Sync)
        {
            if (Collections.ContainsKey(type))
                throw new InvalidOperationException($"Collection formatter for '{type}' is already registered.");
            Collections.Add(type, entry);
        }
        return new Lease(() =>
        {
            lock (Sync)
                if (Collections.TryGetValue(type, out Entry? current) && current.Token == entry.Token)
                    Collections.Remove(type);
        });
    }

    private static string TupleKey(Type[] types)
        => string.Join("|", Array.ConvertAll(types, static type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name));

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
