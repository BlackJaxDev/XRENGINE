namespace XREngine.Rendering.Shadows;

/// <summary>
/// Reason why a cascade group of the directional shadow lane did not use the copy of the
/// static caster tile in a frame. The lane counts each reason in
/// <see cref="DirectionalShadowStaticCacheDiagnostics"/>.
/// </summary>
public enum EDirectionalShadowStaticCacheCause
{
    /// <summary>The cache setting is off.</summary>
    Disabled,
    /// <summary>No dynamic caster changed in the cascade recently, so the cascade has no static tile and draws all casters.</summary>
    NoDynamicCasters,
    /// <summary>The frame selects casters on the GPU (strict indexed frame); the cache supports only CPU record masks.</summary>
    GpuCasterSelection,
    /// <summary>The static tile framebuffer is missing, has another size, or has another depth format.</summary>
    TileUnavailable,
    /// <summary>The static tile was never drawn, or a failed submission invalidated it.</summary>
    NoStaticContent,
    /// <summary>The cascade matrix differs from the matrix of the static tile (a refit).</summary>
    FitMismatch,
    /// <summary>The tile rectangle or the atlas page differs from the static tile.</summary>
    TileMismatch,
    /// <summary>A static caster change met the cascade tile after the static tile was drawn.</summary>
    StaticEpochChanged,
    /// <summary>The set of static caster records of the cascade differs (promotion, demotion, membership).</summary>
    RecordSetChanged,
    /// <summary>The static tile was drawn in this render frame (a recording retry), so its content is not trusted.</summary>
    SameFrame,
}
