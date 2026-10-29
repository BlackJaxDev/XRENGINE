namespace XREngine.Execution;

/// <summary>Reports which dedicated engine loop workers have stopped.</summary>
public readonly record struct EngineLoopExitStatus(bool UpdateStopped, bool CollectVisibleStopped, bool FixedUpdateStopped)
{
    public bool AllStopped => UpdateStopped && CollectVisibleStopped && FixedUpdateStopped;
}
