using System;
using System.Collections.Generic;

namespace XREngine.Scene.Transforms;

public static class TransformFactoryRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Type, Func<TransformBase>> Factories = [];

    static TransformFactoryRegistry()
    {
        Register<Transform>(static () => new Transform());
    }

    public static void Register<TTransform>(Func<TTransform> factory)
        where TTransform : TransformBase
    {
        ArgumentNullException.ThrowIfNull(factory);
        Register(typeof(TTransform), () => factory());
    }

    public static void Register(Type transformType, Func<TransformBase> factory)
    {
        ArgumentNullException.ThrowIfNull(transformType);
        ArgumentNullException.ThrowIfNull(factory);

        if (!typeof(TransformBase).IsAssignableFrom(transformType))
            throw new ArgumentException($"Type must derive from {nameof(TransformBase)}.", nameof(transformType));

        lock (Sync)
            Factories[transformType] = factory;
    }

    public static bool TryCreate(Type transformType, out TransformBase? transform)
    {
        ArgumentNullException.ThrowIfNull(transformType);

        Func<TransformBase>? factory;
        lock (Sync)
            Factories.TryGetValue(transformType, out factory);

        if (factory is null)
        {
            transform = null;
            return false;
        }

        transform = factory();
        return transform is not null;
    }
}