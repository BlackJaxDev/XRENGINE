namespace XREngine.Rendering;

/// <summary>
/// Selects the per-frame limits that imported texture streaming uses.
/// A change between profiles fades over a number of frames, so the limits do not jump.
/// </summary>
public enum ETextureStreamingBudgetProfile
{
    /// <summary>The normal limits: 2 transitions, 1 promotion, 32 MB of promotions, and a 6-frame cooldown in each frame.</summary>
    Default = 0,

    /// <summary>
    /// Higher limits and a shorter mip fade while a cutscene runs. The engine settings supply the values.
    /// The VRAM budget fit still applies.
    /// </summary>
    Cutscene = 1,
}
