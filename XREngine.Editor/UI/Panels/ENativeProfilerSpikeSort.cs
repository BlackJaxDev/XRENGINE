namespace XREngine.Editor;

/// <summary>Selects the column that orders the native FPS spike rows.</summary>
internal enum ENativeProfilerSpikeSort
{
    DropFps,
    DropPercent,
    Count,
    WorstFrame,
    ReferenceFps,
    CurrentFps,
    Thread,
    HotPath,
}
