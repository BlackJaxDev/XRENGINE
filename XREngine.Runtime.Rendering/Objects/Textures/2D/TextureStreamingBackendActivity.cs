namespace XREngine.Rendering;

/// <summary>
/// Allocation-free activity snapshot for one or more imported-texture streaming backends.
/// </summary>
internal readonly record struct TextureStreamingBackendActivity(
    int ActiveDecodeCount,
    int QueuedDecodeCount,
    int ActiveGpuUploadCount,
    long UploadBytesScheduledThisFrame,
    bool HasUploadServiceWork)
{
    public bool HasDecodeWork => ActiveDecodeCount > 0 || QueuedDecodeCount > 0;
    public bool HasGpuUploadWork => ActiveGpuUploadCount > 0 || HasUploadServiceWork;
    public bool HasWork => HasDecodeWork || HasGpuUploadWork;

    public TextureStreamingBackendActivity Add(TextureStreamingBackendActivity other)
        => new(
            ActiveDecodeCount + other.ActiveDecodeCount,
            QueuedDecodeCount + other.QueuedDecodeCount,
            ActiveGpuUploadCount + other.ActiveGpuUploadCount,
            UploadBytesScheduledThisFrame + other.UploadBytesScheduledThisFrame,
            HasUploadServiceWork || other.HasUploadServiceWork);

    public static TextureStreamingBackendActivity Capture(
        ITextureResidencyBackend defaultBackend,
        ITextureResidencyBackend sparseBackend,
        bool hasUploadServiceWork = false)
    {
        TextureStreamingBackendActivity activity = Capture(defaultBackend, hasUploadServiceWork);
        return ReferenceEquals(defaultBackend, sparseBackend)
            ? activity
            : activity.Add(Capture(sparseBackend));
    }

    private static TextureStreamingBackendActivity Capture(
        ITextureResidencyBackend backend,
        bool hasUploadServiceWork = false)
        => new(
            backend.ActiveDecodeCount,
            backend.QueuedDecodeCount,
            backend.ActiveGpuUploadCount,
            backend.UploadBytesScheduledThisFrame,
            hasUploadServiceWork);
}
