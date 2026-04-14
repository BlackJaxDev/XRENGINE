namespace XREngine.Data.Trees;

public enum EOctreeCommandKind
{
    None = 0,
    Add = 1,
    Move = 2,
    Remove = 3,
}

public readonly record struct OctreeSwapTimingStats(
    int DrainedCommandCount,
    int BufferedCommandCount,
    int ExecutedCommandCount,
    long DrainTicks,
    long ExecuteTicks,
    long MaxCommandTicks,
    EOctreeCommandKind MaxCommandKind);

public readonly record struct OctreeRaycastTimingStats(
    int ProcessedCommandCount,
    int DroppedCommandCount,
    long TraversalTicks,
    long CallbackTicks,
    long MaxTraversalTicks,
    long MaxCallbackTicks,
    long MaxCommandTicks);