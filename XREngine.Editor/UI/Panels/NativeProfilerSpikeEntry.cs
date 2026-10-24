namespace XREngine.Editor;

/// <summary>Stores one bounded, grouped FPS drop path.</summary>
internal readonly record struct NativeProfilerSpikeEntry(
    int ThreadId,
    string HotPath,
    int SeenCount,
    float WorstFrameSeconds,
    float WorstReferenceFps,
    float WorstCurrentFps,
    float WorstDropFps,
    bool PathTruncated);
