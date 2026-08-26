using System.Collections.Concurrent;

namespace XREngine.Rendering;

/// <summary>
/// Owns the explicit attachment between a Core world and its rendering capability.
/// The registry is keyed by the runtime world context, never by serialized <c>XRWorld</c>
/// assets, so multi-world lifetimes cannot leak through an asset-global lookup.
/// </summary>
public static class RuntimeRenderWorldRegistry
{
    private static readonly ConcurrentDictionary<IRuntimeWorldContext, IRuntimeRenderWorld> Worlds =
        new(ReferenceEqualityComparer.Instance);

    public static void Attach(IRuntimeRenderWorld renderWorld)
    {
        ArgumentNullException.ThrowIfNull(renderWorld);
        if (!Worlds.TryAdd(renderWorld.WorldContext, renderWorld))
            throw new InvalidOperationException("A render world is already attached to this runtime world context.");
    }

    public static bool Detach(IRuntimeWorldContext worldContext, out IRuntimeRenderWorld? renderWorld)
    {
        ArgumentNullException.ThrowIfNull(worldContext);
        return Worlds.TryRemove(worldContext, out renderWorld);
    }

    public static bool TryGet(IRuntimeWorldContext? worldContext, out IRuntimeRenderWorld? renderWorld)
    {
        if (worldContext is not null && Worlds.TryGetValue(worldContext, out IRuntimeRenderWorld? attached))
        {
            renderWorld = attached;
            return true;
        }

        renderWorld = null;
        return false;
    }

    public static IRuntimeRenderWorld? Get(IRuntimeWorldContext? worldContext)
        => TryGet(worldContext, out IRuntimeRenderWorld? renderWorld) ? renderWorld : null;

    /// <summary>Clears owned attachments for deterministic test and host teardown.</summary>
    public static void ResetForTests() => Worlds.Clear();
}

public static class RuntimeRenderWorldExtensions
{
    public static IRuntimeRenderWorld? GetRenderWorld(this IRuntimeWorldContext? worldContext)
        => RuntimeRenderWorldRegistry.Get(worldContext);

    public static IRuntimeRenderInfo3DRegistrationTarget? GetRenderRegistrationTarget(this IRuntimeWorldContext? worldContext)
        => RuntimeRenderWorldRegistry.Get(worldContext) as IRuntimeRenderInfo3DRegistrationTarget;
}
