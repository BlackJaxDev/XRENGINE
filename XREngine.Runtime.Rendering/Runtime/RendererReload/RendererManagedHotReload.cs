namespace XREngine.Rendering;

/// <summary>
/// Metadata-update handler shared by renderer leaf assemblies.
/// </summary>
public static class RendererManagedHotReload
{
    private static long _appliedUpdateCount;
    private static DateTimeOffset? _lastAppliedAt;
    private static string _lastMechanism = "None";
    private static bool _moduleReloadRecommended;

    public static long AppliedUpdateCount => Interlocked.Read(ref _appliedUpdateCount);
    public static DateTimeOffset? LastAppliedAt => _lastAppliedAt;
    public static string LastMechanism => _lastMechanism;
    public static bool ModuleReloadRecommended => _moduleReloadRecommended;

    public static void ClearCache(Type[]? updatedTypes)
    {
        if (RendererReplacementCoordinator.Current.IsReloadInProgress)
        {
            _moduleReloadRecommended = true;
            _lastMechanism = "Managed delta arrived during backend reload; explicit backend reload recommended.";
            return;
        }

        ShaderSourceResolver.ClearCaches();
    }

    public static void UpdateApplication(Type[]? updatedTypes)
    {
        if (RendererReplacementCoordinator.Current.IsReloadInProgress)
        {
            _moduleReloadRecommended = true;
            _lastMechanism = "Managed delta deferred because a backend reload transaction is active.";
            return;
        }

        void Invalidate()
        {
            foreach (XRViewport viewport in RuntimeEngine.EnumerateActiveViewports(
                         RuntimeEngine.EViewportEnumerationMode.IncludeVrEyeViewports))
            {
                viewport.RenderPipelineInstance.InvalidatePhysicalResources();
            }
        }

        if (RuntimeEngine.IsRenderThread)
            Invalidate();
        else
            RuntimeEngine.EnqueueRenderThreadTask(Invalidate, "RendererManagedHotReload.Invalidate");

        _lastAppliedAt = DateTimeOffset.UtcNow;
        _lastMechanism = updatedTypes is { Length: > 0 }
            ? $"Managed delta plus render-pipeline invalidation ({updatedTypes.Length} type(s))."
            : "Managed delta plus render-pipeline invalidation.";
        _moduleReloadRecommended = false;
        Interlocked.Increment(ref _appliedUpdateCount);
    }

    public static void RecommendModuleReload(string reason)
    {
        _moduleReloadRecommended = true;
        _lastMechanism = reason;
    }
}

