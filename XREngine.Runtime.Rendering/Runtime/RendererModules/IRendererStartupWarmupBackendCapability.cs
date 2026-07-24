namespace XREngine.Rendering;

/// <summary>
/// Allows an application composition root to temporarily increase backend startup budgets.
/// </summary>
public interface IRendererStartupWarmupBackendCapability
{
    /// <summary>
    /// Gets the number of backend startup operations still waiting to complete.
    /// </summary>
    int PendingStartupWorkCount { get; }

    /// <summary>
    /// True while backend shader programs needed by the active render path are still linking.
    /// </summary>
    bool HasPendingAsyncPrograms { get; }

    void BoostMeshGenerationBudgetUntilDrained(double budgetMilliseconds);

    void BoostStartupBudgets(
        double meshBudgetMilliseconds,
        int normalRendererCap,
        int priorityRendererCap,
        double uploadBudgetMilliseconds);
}
