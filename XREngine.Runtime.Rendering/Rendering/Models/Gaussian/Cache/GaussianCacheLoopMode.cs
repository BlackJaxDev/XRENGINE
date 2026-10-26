namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>Tells what playback does at the end of the cache.</summary>
public enum GaussianCacheLoopMode : byte
{
    Loop = 0,
    PingPong = 1,
    Once = 2,
}
