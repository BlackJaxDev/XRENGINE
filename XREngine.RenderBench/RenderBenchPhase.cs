namespace XREngine.RenderBench;

public enum RenderBenchPhase
{
    Starting,
    Idle,
    Warmup,
    Stabilizing,
    Capturing,
    Draining,
    Completed,
    Failed,
    Stopping,
}
