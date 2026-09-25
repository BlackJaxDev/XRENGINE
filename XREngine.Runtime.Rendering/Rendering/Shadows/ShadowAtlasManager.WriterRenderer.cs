namespace XREngine.Rendering.Shadows;

public sealed partial class ShadowAtlasManager
{
    // Weak so a retired renderer generation is never kept alive by the atlas.
    private WeakReference<AbstractRenderer>? _atlasWriterRenderer;

    /// <summary>
    /// Invalidates tile residency when a different renderer instance starts writing the atlas.
    /// </summary>
    /// <remarks>
    /// Resident tiles live in backend textures owned by the renderer that encoded them. A
    /// replacement renderer (restart, hot reload or device recovery) wraps the same engine
    /// atlas pages with fresh, unwritten backend storage, so content versions recorded for the
    /// previous writer cannot justify reuse. Without this check, unchanged lights and cameras
    /// keep sampling the unwritten pages until an unrelated projection change forces a refresh.
    /// Pending write receipts belong to the previous writer's command streams and are dropped
    /// on this render-thread boundary; residency itself is cleared on the planning thread
    /// through the existing per-kind reset request.
    /// </remarks>
    private void ObserveAtlasWriterRenderer()
    {
        AbstractRenderer? current = AbstractRenderer.Current;
        if (current is null)
            return;

        if (_atlasWriterRenderer is null)
        {
            _atlasWriterRenderer = new WeakReference<AbstractRenderer>(current);
            return;
        }

        if (_atlasWriterRenderer.TryGetTarget(out AbstractRenderer? previous) &&
            ReferenceEquals(previous, current))
        {
            return;
        }

        _atlasWriterRenderer.SetTarget(current);
        ResetSubmissionTracking();
        RequestAtlasKindReset(EShadowAtlasKind.Directional);
        RequestAtlasKindReset(EShadowAtlasKind.Spot);
        RequestAtlasKindReset(EShadowAtlasKind.Point);
        XREngine.Debug.LightingWarningEvery(
            $"ShadowAtlas.WriterRendererChanged.{GetHashCode()}",
            TimeSpan.FromSeconds(1.0),
            "[ShadowAtlas] Atlas writer changed to renderer generation {0}; resetting tile residency so every tile is re-rendered on the new backend.",
            current.BackendGeneration);
    }
}
