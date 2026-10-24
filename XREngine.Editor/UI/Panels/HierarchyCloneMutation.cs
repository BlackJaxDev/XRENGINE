using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Places a canonical owned clone through the existing guarded hierarchy transaction.</summary>
internal static class HierarchyCloneMutation
{
    internal static HierarchyCloneCustody Execute(string description, SceneNode source, RuntimeWorld world,
        Func<SceneNode, HierarchyPlacement> destination, Action<SceneNode>? onApplied = null,
        Func<bool>? ownerIsCurrent = null, HierarchyCloneCustodyStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        HierarchyCloneCustody custody = (store ?? HierarchyCloneCustodyStore.Shared).Reserve(world);
        try
        {
            HierarchyCommandContext context = new(world, [source], ownerIsCurrent ?? (() => true));
            HierarchyMutation.Execute(description, () =>
            {
                try
                {
                    if (!context.CanStart())
                        throw new InvalidOperationException("The clone source changed before construction.");
                    SceneNode clone = custody.Clone(source);
                    custody.BeginInitialAdmission();
                    if (!context.CanStart())
                        throw new InvalidOperationException("The clone source changed during construction.");
                    custody.RequireIdentity();
                    bool active = clone.IsActiveSelf;
                    clone.IsActiveSelf = false;
                    custody.RequireIdentity();
                    if (clone.IsActiveSelf)
                        throw new InvalidOperationException("The clone cannot enter the detached inactive state needed for Undo.");
                    HierarchyPlacement before = HierarchyPlacement.Capture(clone, world);
                    custody.RequireIdentity();
                    HierarchyPlacement after = destination(clone);
                    custody.RequireIdentity();
                    HierarchyMutationOperation operation = new(before, after, preserveWorld: false, activeAfter: active,
                        requireIdentity: custody.RequireIdentity);
                    operation.SetPropertyReplayGuard(new HierarchyClonePropertyReplayGuard(custody, operation));
                    return operation;
                }
                catch (Exception failure)
                {
                    custody.RetainFailure(failure);
                    throw;
                }
            }, () => onApplied?.Invoke(custody.Node!), context.IsOwnerCurrent, cloneCustody: custody);
            return custody;
        }
        finally { custody.CompleteCommand(); }
    }
}
