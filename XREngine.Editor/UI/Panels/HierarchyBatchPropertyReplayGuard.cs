using XREngine.Data.Core;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Admits one captured local key setter while checking every peer clone.</summary>
internal sealed class HierarchyBatchPropertyReplayGuard(HierarchyMutationBatchOperation batch,
    HierarchyCloneCustody custody) : IUndoPropertyReplayGuard
{
    public IDisposable Enter(XRBase target, string propertyName)
    {
        if (target is not TransformBase transform)
            throw new InvalidOperationException("The batch property is not a captured transform.");
        HierarchyLifetimeAdmission admission = custody.AcquireAdmission();
        try
        {
            batch.BeginPropertyReplay(transform, propertyName);
            return new HierarchyBatchPropertyAdmission(batch, admission);
        }
        catch { admission.Dispose(); throw; }
    }

    public void RequireCurrent() => batch.RequirePropertyReplay();
    public void Failed(Exception failure) => custody.RetainFailure(failure);
}
