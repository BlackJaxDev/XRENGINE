namespace XREngine.Rendering;

/// <summary>
/// Cumulative times of the light swap in the world swap.
/// </summary>
/// <remarks>
/// The light swap time minus these stages is the local shadow relevance update and the manual lightmap requests.
/// </remarks>
/// <param name="Calls">The number of light swaps.</param>
/// <param name="DirectionalMilliseconds">The directional shadow view swaps: the primary view and the cascades.</param>
/// <param name="LocalMilliseconds">The spot and point shadow view swaps.</param>
/// <param name="AtlasPublicationMilliseconds">The shadow atlas frame publication.</param>
/// <param name="CapturesMilliseconds">The scene-capture swaps.</param>
public readonly record struct CollectVisibleSwapLightStageTimings(
    long Calls,
    double DirectionalMilliseconds,
    double LocalMilliseconds,
    double AtlasPublicationMilliseconds,
    double CapturesMilliseconds);
