using XREngine.Data.Core;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Protects only the captured local transform keys of one clone transaction.</summary>
internal sealed class HierarchyClonePropertyReplayGuard(HierarchyCloneCustody custody,
    HierarchyMutationOperation operation) : IUndoPropertyReplayGuard
{
    public IDisposable Enter(XRBase target, string propertyName)
    {
        if (!ReferenceEquals(target, operation.Before.Transform) || propertyName is not
            (nameof(Transform.Translation) or nameof(Transform.Rotation) or nameof(Transform.Scale) or nameof(Transform.Order)))
            throw new InvalidOperationException("The clone replay guard does not own this exact property.");
        HierarchyLifetimeAdmission admission = custody.AcquireAdmission();
        try
        {
            RequireCurrent();
            return admission;
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }

    public void RequireCurrent()
    {
        custody.RequireIdentity();
        if (custody.State is not (EHierarchyCloneCustodyState.Undoing or EHierarchyCloneCustodyState.Redoing))
            throw new InvalidOperationException("The clone has no admitted property replay direction.");
        operation.RequireAfter(includeProperties: false);
    }

    public void Failed(Exception failure) => custody.RetainFailure(failure);
}
