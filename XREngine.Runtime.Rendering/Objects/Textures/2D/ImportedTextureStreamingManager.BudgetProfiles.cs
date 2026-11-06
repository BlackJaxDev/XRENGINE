namespace XREngine.Rendering;

internal sealed partial class ImportedTextureStreamingManager
{
    private int _budgetProfile = (int)ETextureStreamingBudgetProfile.Default;

    // The evaluation thread owns the blend and the active limits.
    private float _budgetProfileBlend;
    private TextureStreamingBudgetLimits _activeBudgetLimits = TextureStreamingBudgetLimits.Default;

    // Completion callbacks read these values on other threads, so they are separate volatile fields.
    private int _activePromotionCooldownFrames = TextureStreamingBudgetLimits.Default.PromotionCooldownFrames;
    private int _activePromotionFadeFrames = TextureStreamingBudgetLimits.Default.PromotionFadeFrames;

    /// <summary>The budget profile that the limits move to. Any thread can read it.</summary>
    internal ETextureStreamingBudgetProfile BudgetProfile
        => (ETextureStreamingBudgetProfile)Volatile.Read(ref _budgetProfile);

    /// <summary>
    /// The limits of the last evaluation. During a fade, they are between the two profiles.
    /// Read it on the evaluation thread, or after evaluation stops.
    /// </summary>
    internal TextureStreamingBudgetLimits ActiveBudgetLimits => _activeBudgetLimits;

    /// <summary>
    /// The fade progress of the last evaluation: 0 is <see cref="ETextureStreamingBudgetProfile.Default"/>
    /// and 1 is <see cref="ETextureStreamingBudgetProfile.Cutscene"/>.
    /// </summary>
    internal float BudgetProfileBlend => _budgetProfileBlend;

    /// <summary>
    /// Selects the per-frame limits. The limits fade to the new profile over
    /// <see cref="RuntimeEngine.Rendering.EngineSettings.TextureStreamingBudgetProfileFadeFrames"/> evaluations.
    /// The VRAM budget fit, the publish rule, and the generation checks do not change.
    /// Any thread can call it.
    /// </summary>
    /// <param name="profile">The profile to use.</param>
    internal void SetBudgetProfile(ETextureStreamingBudgetProfile profile)
    {
        if (profile is not ETextureStreamingBudgetProfile.Default and not ETextureStreamingBudgetProfile.Cutscene)
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown texture streaming budget profile.");

        int previous = Interlocked.Exchange(ref _budgetProfile, (int)profile);
        if (previous != (int)profile)
            Debug.Textures($"[TextureStreaming] Budget profile changed from {(ETextureStreamingBudgetProfile)previous} to {profile}.");
    }

    /// <summary>
    /// Gets the <see cref="ETextureStreamingBudgetProfile.Cutscene"/> limits from the engine settings.
    /// </summary>
    internal static TextureStreamingBudgetLimits ResolveCutsceneBudgetLimits(RuntimeEngine.Rendering.EngineSettings settings)
        => new(
            settings.TextureStreamingCutsceneMaxTransitionsPerFrame,
            settings.TextureStreamingCutsceneMaxPromotionsPerFrame,
            settings.TextureStreamingCutsceneMaxPromotionMegabytesPerFrame * 1024L * 1024L,
            settings.TextureStreamingCutscenePromotionCooldownFrames,
            settings.TextureStreamingCutscenePromotionFadeFrames);

    /// <summary>
    /// Moves the fade one evaluation toward the selected profile and stores the active limits.
    /// It allocates nothing.
    /// </summary>
    private TextureStreamingBudgetLimits AdvanceBudgetProfile()
    {
        float target = Volatile.Read(ref _budgetProfile) == (int)ETextureStreamingBudgetProfile.Cutscene ? 1.0f : 0.0f;
        float blend = _budgetProfileBlend;
        if (blend == target && blend == 0.0f)
            return _activeBudgetLimits;

        RuntimeEngine.Rendering.EngineSettings settings = RuntimeEngine.Rendering.Settings;
        if (blend != target)
        {
            int fadeFrames = settings.TextureStreamingBudgetProfileFadeFrames;
            float step = fadeFrames <= 0 ? 1.0f : 1.0f / fadeFrames;
            blend = target > blend
                ? Math.Min(target, blend + step)
                : Math.Max(target, blend - step);
            _budgetProfileBlend = blend;
        }

        TextureStreamingBudgetLimits limits = blend <= 0.0f
            ? TextureStreamingBudgetLimits.Default
            : TextureStreamingBudgetLimits.Lerp(TextureStreamingBudgetLimits.Default, ResolveCutsceneBudgetLimits(settings), blend);
        _activeBudgetLimits = limits;
        Volatile.Write(ref _activePromotionCooldownFrames, limits.PromotionCooldownFrames);
        Volatile.Write(ref _activePromotionFadeFrames, limits.PromotionFadeFrames);
        return limits;
    }
}
