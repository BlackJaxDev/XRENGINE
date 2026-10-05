namespace XREngine.Components;

/// <summary>Statically rooted constructors for components created by runtime type.</summary>
public static class RuntimeComponentFactoryRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Type, Func<XRComponent>> Factories = [];

    public static void Register(Type type, Func<XRComponent> factory)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(factory);
        if (!typeof(XRComponent).IsAssignableFrom(type))
            throw new ArgumentException($"'{type}' is not an XRComponent.", nameof(type));
        lock (Sync)
        {
            if (!Factories.TryAdd(type, factory))
                throw new InvalidOperationException($"Component factory '{type}' is already registered.");
        }
    }

    internal static bool TryGetFactory(Type type, out Func<XRComponent>? factory)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (Sync)
            return Factories.TryGetValue(type, out factory);
    }

    public static bool TryCreate(Type type, out XRComponent? component)
    {
        if (!TryGetFactory(type, out Func<XRComponent>? factory))
        {
            component = null;
            return false;
        }
        component = factory!();
        if (component is null || component.GetType() != type)
            throw new InvalidOperationException($"Component factory '{type}' returned a different or null type.");
        return true;
    }
}
