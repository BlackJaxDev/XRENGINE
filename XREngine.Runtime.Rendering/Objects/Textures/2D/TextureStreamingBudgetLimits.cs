namespace XREngine.Rendering;

/// <summary>
/// The per-frame limits of imported texture streaming. A budget profile supplies one set of these values.
/// </summary>
/// <param name="MaxResidentTransitionsPerFrame">The maximum number of transitions, promotions and demotions, that one frame queues.</param>
/// <param name="MaxPromotionTransitionsPerFrame">The maximum number of promotions that one frame queues.</param>
/// <param name="MaxPromotionBytesPerFrame">The maximum number of bytes that the promotions of one frame add. The first promotion of a frame is not limited.</param>
/// <param name="PromotionCooldownFrames">The number of frames after a promotion before a texture that is not visible can promote again.</param>
/// <param name="PromotionFadeFrames">The number of frames over which a promoted texture fades in its new mips.</param>
public readonly record struct TextureStreamingBudgetLimits(
    int MaxResidentTransitionsPerFrame,
    int MaxPromotionTransitionsPerFrame,
    long MaxPromotionBytesPerFrame,
    int PromotionCooldownFrames,
    int PromotionFadeFrames)
{
    /// <summary>
    /// The limits of <see cref="ETextureStreamingBudgetProfile.Default"/>: 2 transitions, 1 promotion,
    /// 32 MB of promotions, a 6-frame promotion cooldown, and a 90-frame mip fade.
    /// </summary>
    public static TextureStreamingBudgetLimits Default { get; } = new(
        MaxResidentTransitionsPerFrame: 2,
        MaxPromotionTransitionsPerFrame: 1,
        MaxPromotionBytesPerFrame: 32L * 1024L * 1024L,
        PromotionCooldownFrames: 6,
        PromotionFadeFrames: 90);

    /// <summary>
    /// Interpolates between two sets of limits. Integer values round to the nearest value.
    /// </summary>
    /// <param name="from">The limits at <paramref name="t"/> = 0.</param>
    /// <param name="to">The limits at <paramref name="t"/> = 1.</param>
    /// <param name="t">The progress from 0 to 1. The method clamps it.</param>
    public static TextureStreamingBudgetLimits Lerp(in TextureStreamingBudgetLimits from, in TextureStreamingBudgetLimits to, float t)
    {
        if (!float.IsFinite(t) || t <= 0.0f)
            return from;
        if (t >= 1.0f)
            return to;

        return new TextureStreamingBudgetLimits(
            LerpInt(from.MaxResidentTransitionsPerFrame, to.MaxResidentTransitionsPerFrame, t),
            LerpInt(from.MaxPromotionTransitionsPerFrame, to.MaxPromotionTransitionsPerFrame, t),
            from.MaxPromotionBytesPerFrame + (long)Math.Round((to.MaxPromotionBytesPerFrame - from.MaxPromotionBytesPerFrame) * (double)t),
            LerpInt(from.PromotionCooldownFrames, to.PromotionCooldownFrames, t),
            LerpInt(from.PromotionFadeFrames, to.PromotionFadeFrames, t));
    }

    private static int LerpInt(int from, int to, float t)
        => from + (int)MathF.Round((to - from) * t);
}
