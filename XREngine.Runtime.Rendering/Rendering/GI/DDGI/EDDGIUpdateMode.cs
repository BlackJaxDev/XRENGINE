namespace XREngine.Rendering.GI.DDGI;

/// <summary>Operating update modes for a DDGI volume.</summary>
public enum EDDGIUpdateMode
{
    /// <summary>Full dynamic GPU ray tracing and atlas updates run every frame.</summary>
    Dynamic = 0,
    /// <summary>Slow-update / infinite latency: updates infrequently or upon manual lighting invalidation.</summary>
    SlowUpdate = 1,
    /// <summary>Static baked data skips tracing and atlas updates; screen-space DDGI sampling and compositing still run.</summary>
    Baked = 2,
}
