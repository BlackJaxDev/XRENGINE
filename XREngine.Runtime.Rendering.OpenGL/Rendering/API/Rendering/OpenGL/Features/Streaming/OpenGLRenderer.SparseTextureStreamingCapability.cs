namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    public bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(onCompleted);

        string textureName = string.IsNullOrWhiteSpace(texture.Name) ? "UnnamedTexture" : texture.Name;
        RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(() =>
        {
            try
            {
                GLTexture2D? glTexture = GetOrCreateAPIRenderObject(texture, generateNow: false) as GLTexture2D;
                if (glTexture is null)
                {
                    onCompleted(SparseTextureStreamingTransitionResult.Unsupported(
                        "OpenGL texture wrapper is unavailable for sparse texture streaming."));
                    return;
                }

                void CompleteAsyncTransition(SparseTextureStreamingTransitionResult result)
                    => RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(
                        () => onCompleted(result),
                        $"XRTexture2D.CompleteSparseTransition[{textureName}]",
                        RenderThreadJobKind.TextureUpload);

                void ReportAsyncTransitionError(Exception ex)
                    => RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(
                        () => onError?.Invoke(ex),
                        $"XRTexture2D.FailSparseTransition[{textureName}]",
                        RenderThreadJobKind.TextureUpload);

                if (!glTexture.TryScheduleSparseTextureStreamingTransitionAsync(
                    request,
                    cancellationToken,
                    CompleteAsyncTransition,
                    ReportAsyncTransitionError))
                {
                    onCompleted(texture.ApplySparseTextureStreamingTransition(request));
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }, $"XRTexture2D.ScheduleSparseTransition[{textureName}]", RenderThreadJobKind.TextureUpload);

        return true;
    }

    public SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult)
    {
        GLTexture2D? glTexture = GetOrCreateAPIRenderObject(texture, generateNow: false) as GLTexture2D;
        return glTexture is null
            ? SparseTextureStreamingFinalizeResult.Failed(
                "OpenGL texture wrapper is unavailable for sparse texture finalization.")
            : glTexture.FinalizeSparseTextureStreamingTransition(request, transitionResult);
    }
}
