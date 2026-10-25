using XREngine.Scene.Prefabs;

namespace XREngine.Editor;

/// <summary>Releases the property latch and construction hold after one Undo setter.</summary>
internal sealed class HierarchyBatchPropertyAdmission(HierarchyMutationBatchOperation batch,
    HierarchyLifetimeAdmission admission) : IDisposable
{
    public void Dispose()
    {
        batch.EndPropertyReplay();
        admission.Dispose();
    }
}
