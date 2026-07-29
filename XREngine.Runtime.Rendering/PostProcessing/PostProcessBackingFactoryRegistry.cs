using System.Collections.Concurrent;

namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Provides a registry for post-process backing factories, allowing registration and creation of backing instances based on their types.
/// </summary>
public static class PostProcessBackingFactoryRegistry
{
    /// <summary>
    /// Gets the concurrent dictionary that holds the registered factories for post-process backing types.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, Func<object>> Factories = new();

    /// <summary>
    /// Registers a factory for creating instances of the specified backing type.
    /// </summary>
    /// <typeparam name="TBacking">The type of the backing instance.</typeparam>
    /// <param name="factory">The factory function for creating instances of the backing type.</param>
    public static void Register<TBacking>(Func<TBacking> factory)
        where TBacking : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        Register(typeof(TBacking), () => factory());
    }

    /// <summary>
    /// Registers a factory for creating instances of the specified backing type.
    /// </summary>
    /// <param name="backingType">The type of the backing instance.</param>
    /// <param name="factory">The factory function for creating instances of the backing type.</param>
    public static void Register(Type backingType, Func<object> factory)
    {
        ArgumentNullException.ThrowIfNull(backingType);
        ArgumentNullException.ThrowIfNull(factory);

        Factories[backingType] = factory;
    }

    /// <summary>
    /// Attempts to create an instance of the specified backing type using the registered factory.
    /// </summary>
    /// <param name="backingType">The type of the backing instance to create.</param>
    /// <param name="backing">When this method returns, contains the created backing instance if successful; otherwise, null.</param>
    /// <returns>True if the backing instance was successfully created; otherwise, false.</returns>
    public static bool TryCreate(Type backingType, out object? backing)
    {
        ArgumentNullException.ThrowIfNull(backingType);

        if (!Factories.TryGetValue(backingType, out Func<object>? factory))
        {
            backing = null;
            return false;
        }

        backing = factory();
        return backing is not null;
    }
}
