namespace XREngine.Rendering;

/// <summary>Records an owned offscreen viewport through the renderer's normal frame transaction.</summary>
public interface IAsyncSceneCaptureBackendCapability
{
    /// <summary>Copies the exact supported shadow producers and freezes their projection records without CPU readback.</summary>
    Task<SceneCaptureLightingSnapshot> CaptureSceneLightingAsync(IRuntimeRenderWorld world, Func<bool> isCurrent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns tightly packed linear RGBA floats only after the exact layer producer was accepted
    /// and its asynchronous readback completed. The caller retains the request resources until completion.
    /// </summary>
    Task<SceneCaptureReadback> CaptureSceneLayerAsync(SceneCaptureRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Adopts the exact persisted layer bytes and completes mips on the captured allocation.</summary>
    Task FinalizeSceneCaptureAsync(SceneCaptureFinalizeRequest request, CancellationToken cancellationToken = default);
}
