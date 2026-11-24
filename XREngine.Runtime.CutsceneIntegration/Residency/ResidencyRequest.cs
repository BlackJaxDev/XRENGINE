namespace XREngine.Cutscenes;

/// <summary>A request to make one level of an asset resident before a cutscene time.</summary>
/// <param name="AssetId">The asset to load.</param>
/// <param name="TargetLevel">The level index to load. 0 is T0, 1 is T1, and 2 is T2.</param>
/// <param name="Deadline">The cutscene time at which the playhead needs the level.</param>
/// <param name="ScreenCoverage">The largest screen coverage of the asset in the request window, from 0 to 1.</param>
/// <param name="Generation">The planner generation. Each jump increments it. Running work compares it to find stale work.</param>
public readonly record struct ResidencyRequest(
    Guid AssetId,
    int TargetLevel,
    CutsceneTime Deadline,
    float ScreenCoverage,
    int Generation);
