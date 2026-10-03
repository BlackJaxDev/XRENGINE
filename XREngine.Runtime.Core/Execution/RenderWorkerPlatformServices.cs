namespace XREngine.Execution;

/// <summary>Installs platform-specific render-worker scheduling capabilities at application startup.</summary>
public static class RenderWorkerPlatformServices
{
    public static Action? HighPriorityInitializer { get; set; }

    public static void ApplyHighRenderPriority()
        => (HighPriorityInitializer ?? throw new NotSupportedException(
            "High-priority render-worker scheduling requires an installed platform capability."))();
}
