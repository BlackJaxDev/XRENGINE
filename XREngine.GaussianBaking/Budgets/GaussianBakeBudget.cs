namespace XREngine.Rendering.Models.Gaussian.Baking.Budgets;

/// <summary>
/// Gives the size and quality limits for one kind of bake. A bake that goes above a limit fails with
/// <see cref="GaussianBakeFailureCodes.BudgetExceeded"/>.
/// </summary>
/// <param name="MaxSplats">The maximum number of splats at full detail.</param>
/// <param name="MaxDriverVertices">The maximum number of driver mesh vertices at full detail.</param>
/// <param name="MaxDurationSeconds">The maximum length of the baked motion, in seconds.</param>
/// <param name="FrameRate">The stored frame rate of the baked motion, in frames per second.</param>
/// <param name="Chunk0Bytes">The maximum size of the first cache chunk, in bytes.</param>
/// <param name="MaxCacheBytes">The maximum size of the full cache, in bytes.</param>
/// <param name="MaxPositionErrorMeters">The maximum position error of a quantized driver vertex, in meters.</param>
/// <param name="L0TargetSplats">The target number of splats in the coarsest detail level.</param>
public sealed record GaussianBakeBudget(
    int MaxSplats,
    int MaxDriverVertices,
    float MaxDurationSeconds,
    float FrameRate,
    int Chunk0Bytes,
    long MaxCacheBytes,
    float MaxPositionErrorMeters,
    int L0TargetSplats)
{
    private static readonly GaussianBakeBudget Prefab = new(
        MaxSplats: 50_000,
        MaxDriverVertices: 8_000,
        MaxDurationSeconds: 10f,
        FrameRate: 30f,
        Chunk0Bytes: 262_144,
        MaxCacheBytes: 16_777_216L,
        MaxPositionErrorMeters: 0.0005f,
        L0TargetSplats: 2_000);

    private static readonly GaussianBakeBudget WorldBudget = new(
        MaxSplats: 300_000,
        MaxDriverVertices: 16_000,
        MaxDurationSeconds: 10f,
        FrameRate: 30f,
        Chunk0Bytes: 1_048_576,
        MaxCacheBytes: 50_331_648L,
        MaxPositionErrorMeters: 0.002f,
        L0TargetSplats: 5_000);

    /// <summary>
    /// Returns the budget for a kind. Avatars and items use the same budget.
    /// </summary>
    public static GaussianBakeBudget For(GaussianBakeKind kind)
        => kind switch
        {
            GaussianBakeKind.Avatar or GaussianBakeKind.Item => Prefab,
            GaussianBakeKind.World => WorldBudget,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The bake kind is not defined."),
        };
}
