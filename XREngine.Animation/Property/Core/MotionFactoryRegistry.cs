namespace XREngine.Animation;

/// <summary>
/// Statically rooted constructors for concrete <see cref="MotionBase"/> subclasses created by
/// runtime type without reflection so NativeAOT builds and editor menus share one registry.
/// </summary>
public static class MotionFactoryRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Type, Func<MotionBase>> Factories = [];
    private static readonly List<Type> OrderedTypes = [];

    /// <summary>
    /// Registers an AOT-safe parameterless constructor for a concrete <see cref="MotionBase"/> type.
    /// </summary>
    public static void Register(Type type, Func<MotionBase> factory)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(factory);
        if (type.IsAbstract || !typeof(MotionBase).IsAssignableFrom(type))
            throw new ArgumentException($"'{type}' must be a concrete MotionBase subclass.", nameof(type));

        lock (Sync)
        {
            if (Factories.ContainsKey(type))
                return;

            Factories.Add(type, factory);
            OrderedTypes.Add(type);
            OrderedTypes.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        }
    }

    /// <summary>
    /// Returns a snapshot of all registered concrete <see cref="MotionBase"/> types in sorted order.
    /// </summary>
    public static Type[] GetRegisteredTypes()
    {
        lock (Sync)
            return [.. OrderedTypes];
    }

    /// <summary>
    /// Returns true when <paramref name="type"/> has a registered AOT-safe factory.
    /// </summary>
    public static bool TryGetFactory(Type type, out Func<MotionBase>? factory)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (Sync)
            return Factories.TryGetValue(type, out factory);
    }

    /// <summary>
    /// Creates a new <see cref="MotionBase"/> instance for <paramref name="type"/> using its
    /// registered factory without reflection.
    /// </summary>
    public static bool TryCreate(Type type, out MotionBase? motion)
    {
        if (!TryGetFactory(type, out Func<MotionBase>? factory))
        {
            motion = null;
            return false;
        }

        motion = factory!();
        if (motion is null || motion.GetType() != type)
            throw new InvalidOperationException($"Motion factory '{type}' returned a different or null type.");
        return true;
    }

    /// <summary>
    /// Creates a new <see cref="MotionBase"/> instance matching <paramref name="typeName"/>
    /// (by full name or short class name) without reflection.
    /// </summary>
    public static bool TryCreate(string typeName, out MotionBase? motion)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            motion = null;
            return false;
        }

        Func<MotionBase>? factory = null;
        Type? matchedType = null;
        lock (Sync)
        {
            for (int i = 0; i < OrderedTypes.Count; i++)
            {
                Type candidate = OrderedTypes[i];
                if (string.Equals(candidate.FullName, typeName, StringComparison.Ordinal)
                    || string.Equals(candidate.Name, typeName, StringComparison.Ordinal))
                {
                    matchedType = candidate;
                    factory = Factories[candidate];
                    break;
                }
            }
        }

        if (factory is null || matchedType is null)
        {
            motion = null;
            return false;
        }

        motion = factory();
        if (motion is null || motion.GetType() != matchedType)
            throw new InvalidOperationException($"Motion factory '{matchedType}' returned a different or null type.");
        return true;
    }

    /// <summary>
    /// Formats a readable label for a <see cref="MotionBase"/> subclass in editor menus.
    /// </summary>
    public static string GetDisplayName(Type motionType)
    {
        ArgumentNullException.ThrowIfNull(motionType);
        return motionType.Name switch
        {
            nameof(AnimationClip) => "Animation Clip",
            nameof(BlendTree1D) => "Blend Tree 1D",
            nameof(BlendTree2D) => "Blend Tree 2D",
            nameof(BlendTreeDirect) => "Blend Tree Direct",
            nameof(MotionBricksLocomotionMotion) => "MotionBricks Locomotion",
            _ => motionType.Name,
        };
    }
}
