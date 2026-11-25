using XREngine.Scene;
using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Places a canonical owned clone through the existing guarded hierarchy transaction.</summary>
internal static class HierarchyCloneMutation
{
    internal static HierarchyCloneCustody ExecuteMany(string description,
        IReadOnlyList<SceneNode> sources, RuntimeWorld world,
        Func<int, SceneNode, HierarchyPlacement> destination,
        Action<SceneNode>? onApplied = null,
        Func<bool>? ownerIsCurrent = null, HierarchyCloneCustodyStore? store = null,
        Action<int, SceneNode, ObjectConstructionContext, Action>? configure = null,
        Action? requireSources = null, bool selectClones = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        if (sources.Count < 2)
            throw new InvalidOperationException("A grouped duplicate requires at least two top-level sources.");
        HierarchyCloneCustody custody = (store ?? HierarchyCloneCustodyStore.Shared).Reserve(world);
        bool initialPhase = true;
        try
        {
            if (selectClones) custody.CaptureSelection();
            HierarchyCommandContext context = new(world, sources, ownerIsCurrent ?? (() => true));
            HierarchyMutationBatchOperation? batch = null;
            HierarchyMutation.ExecuteBatch(description, () =>
            {
                try
                {
                    if (!context.CanStart())
                        throw new InvalidOperationException("A clone source changed before forest construction.");
                    IReadOnlyList<SceneNode> clones = custody.CloneMany(sources, configure, requireSources);
                    custody.BeginInitialAdmission();
                    if (!context.CanStart())
                        throw new InvalidOperationException("A clone source changed during forest construction.");
                    bool[] active = new bool[clones.Count];
                    HierarchyPlacement[] before = new HierarchyPlacement[clones.Count];
                    HierarchyBatchLocalValues[] local = new HierarchyBatchLocalValues[clones.Count];
                    for (int i = 0; i < clones.Count; i++)
                    {
                        active[i] = clones[i].IsActiveSelf;
                        before[i] = HierarchyPlacement.Capture(clones[i], world);
                        local[i] = HierarchyBatchLocalValues.Capture(before[i].Transform);
                        if (before[i].Parent is not null || before[i].InWorldRoots || before[i].Scenes.Length != 0)
                            throw new InvalidOperationException("The clone forest was not detached before preparation.");
                    }
                    for (int i = 0; i < clones.Count; i++)
                    {
                        RequireInactivePeers(clones, active, i);
                        clones[i].IsActiveSelf = false;
                        RequireInactivePeers(clones, active, i + 1);
                    }
                    HierarchyPlacement[] after = new HierarchyPlacement[clones.Count];
                    for (int i = 0; i < clones.Count; i++)
                    {
                        RequireInactivePeers(clones, active, clones.Count);
                        after[i] = destination(i, clones[i]);
                        RequireInactivePeers(clones, active, clones.Count);
                    }
                    void RequireWhole()
                    {
                        custody.RequireOperationIdentity();
                        if (initialPhase)
                        {
                            if (!context.CanStart())
                                throw new InvalidOperationException("A clone source changed during the batch.");
                            requireSources?.Invoke();
                        }
                        custody.RequireOperationIdentity();
                    }
                    HierarchyMutationBatchOperation operation = new(clones, before, after, active,
                        RequireWhole);
                    operation.SetPropertyReplayGuard(custody);
                    batch = operation;
                    return operation;

                    void RequireInactivePeers(IReadOnlyList<SceneNode> nodes, bool[] original, int next)
                    {
                        custody.RequireOperationIdentity();
                        for (int index = 0; index < nodes.Count; index++)
                            if (nodes[index].IsActiveSelf != (index >= next && original[index]) ||
                                !before[index].Matches() || !local[index].Matches(before[index].Transform))
                                throw new InvalidOperationException("A clone preparation callback changed a peer.");
                    }
                }
                catch (Exception failure) { custody.RetainFailure(failure); throw; }
            }, () =>
            {
                batch!.RequireAfter(true);
                custody.SelectApplied();
                batch.RequireAfter(true);
                if (custody.Nodes is { } nodes)
                    foreach (SceneNode node in nodes)
                    {
                        batch.RequireAfter(true);
                        onApplied?.Invoke(node);
                        batch.RequireAfter(true);
                    }
            }, context.IsOwnerCurrent, cloneCustody: custody);
            return custody;
        }
        finally
        {
            initialPhase = false;
            custody.CompleteCommand();
        }
    }

    internal static HierarchyCloneCustody Execute(string description, SceneNode source, RuntimeWorld world,
        Func<SceneNode, HierarchyPlacement> destination, Action<SceneNode>? onApplied = null,
        Func<bool>? ownerIsCurrent = null, HierarchyCloneCustodyStore? store = null,
        Action<SceneNode, ObjectConstructionContext, Action>? configure = null,
        Action? requireSource = null, bool selectClone = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        HierarchyCloneCustody custody = (store ?? HierarchyCloneCustodyStore.Shared).Reserve(world);
        try
        {
            if (selectClone) custody.CaptureSelection();
            HierarchyCommandContext context = new(world, [source], ownerIsCurrent ?? (() => true));
            HierarchyMutation.Execute(description, () =>
            {
                try
                {
                    if (!context.CanStart())
                        throw new InvalidOperationException("The clone source changed before construction.");
                    SceneNode clone = custody.Clone(source, configure, requireSource);
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
                        requireIdentity: custody.RequireOperationIdentity);
                    operation.SetPropertyReplayGuard(new HierarchyClonePropertyReplayGuard(custody, operation));
                    return operation;
                }
                catch (Exception failure)
                {
                    custody.RetainFailure(failure);
                    throw;
                }
            }, () =>
            {
                custody.SelectApplied();
                onApplied?.Invoke(custody.Node!);
            }, context.IsOwnerCurrent, cloneCustody: custody);
            return custody;
        }
        finally { custody.CompleteCommand(); }
    }
}
