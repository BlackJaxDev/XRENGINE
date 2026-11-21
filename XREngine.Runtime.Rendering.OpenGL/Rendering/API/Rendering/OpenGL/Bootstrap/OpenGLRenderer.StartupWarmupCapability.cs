namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IRendererStartupWarmupBackendCapability
{
    /// <inheritdoc />
    public int PendingStartupWorkCount => MeshGenerationQueue.PendingCount;

    /// <inheritdoc />
    public bool HasPendingAsyncPrograms => HasPendingShaderProgramShutdownWork(out _, out _, out _);

    /// <inheritdoc />
    public void BoostMeshGenerationBudgetUntilDrained(double budgetMilliseconds)
        => MeshGenerationQueue.BoostBudgetUntilDrained(budgetMilliseconds);

    /// <inheritdoc />
    public void BoostStartupBudgets(
        double meshBudgetMilliseconds,
        int normalRendererCap,
        int priorityRendererCap,
        double uploadBudgetMilliseconds)
    {
        MeshGenerationQueue.BoostBudgetUntilDrained(
            meshBudgetMilliseconds,
            normalRendererCap,
            priorityRendererCap);
        UploadQueue.BoostBudgetUntilDrained(uploadBudgetMilliseconds);
    }
}
