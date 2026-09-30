namespace XREngine.Timers;

/// <summary>Controls how presentation reacts when visibility collection misses its frame boundary.</summary>
public enum ECollectVisibleLatePolicy
{
    BlockUntilFresh = 0,
    ReusePreviousVisibility = 1,
}
