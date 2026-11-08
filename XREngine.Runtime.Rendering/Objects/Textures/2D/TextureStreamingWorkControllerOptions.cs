namespace XREngine.Rendering;

/// <summary>
/// The fixed configuration of one <see cref="TextureStreamingWorkController"/>.
/// </summary>
/// <param name="InitialItemsPerFrame">
/// The limit before the controller has a cost measurement. Use the fixed limit that the lane had before it became adaptive.
/// </param>
/// <param name="MaxItemsPerFrame">The hard upper bound of the adaptive limit. It protects the frame when a measurement is wrong.</param>
/// <param name="ToleranceMilliseconds">
/// The time that streaming work can add to a frame above the normal frame budget (the frame-time rule).
/// </param>
/// <param name="ItemsPerFrameOverride">When set, the limit is always this value. The controller still measures.</param>
/// <param name="ItemsPerFrameCap">When set, the adaptive limit is not more than this value.</param>
internal readonly record struct TextureStreamingWorkControllerOptions(
    int InitialItemsPerFrame,
    int MaxItemsPerFrame,
    double ToleranceMilliseconds,
    int? ItemsPerFrameOverride = null,
    int? ItemsPerFrameCap = null);
