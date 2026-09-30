namespace XREngine.Components;

/// <summary>
/// Structural-boundary template operations for <see cref="PhysicsChainWorld"/>.
/// </summary>
internal static class PhysicsChainWorldTemplateExtensions
{
    public static bool TryGetOrCreateTemplate(
        this PhysicsChainWorld world,
        PhysicsChainRuntimeHandle handle,
        out PhysicsChainTemplate? template)
    {
        template = null;
        if (!world.TryResolveRuntimeHandle(handle, out PhysicsChainComponent? component) || component is null)
            return false;

        template = component.GetOrCreateRuntimeTemplate(world);
        return true;
    }

    public static int GetUniqueTemplateCount(this PhysicsChainWorld world)
        => PhysicsChainTemplateCache.ForWorld(world).UniqueTemplateCount;
}
