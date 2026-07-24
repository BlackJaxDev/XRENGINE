namespace XREngine;

/// <summary>
/// Controls how the physics/instanced debug visualizer populates its buffers each frame.
/// </summary>
public enum EDebugVisualizerPopulationMode
{
    TasksWithParallelFor,
    Tasks,
    ParallelFor,
    JobSystem,
    Sequential,
    DirectMemory,
}
