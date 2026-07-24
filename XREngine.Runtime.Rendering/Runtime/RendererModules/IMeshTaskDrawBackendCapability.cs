namespace XREngine.Rendering;

/// <summary>
/// Backend operations used by the stable meshlet collection's legacy task-mesh path.
/// </summary>
public interface IMeshTaskDrawBackendCapability
{
    bool SupportsMeshTaskDraw { get; }
    void PrepareMeshTaskDraw();
    void DrawMeshTasks(uint firstTask, uint taskCount);
}
