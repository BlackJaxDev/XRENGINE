using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

internal readonly record struct TextureStreamingResidentData(
    Mipmap2D[] Mipmaps,
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension);

public readonly record struct ImportedTextureStreamingUsage(
    float DistanceFromCamera,
    float ProjectedPixelSpan = 0.0f,
    float ScreenCoverage = 0.0f,
    float UvDensityHint = 1.0f,
    SparseTextureStreamingPageSelection PageSelection = default)
{
    public float ClampedDistance => MathF.Max(0.0f, DistanceFromCamera);
    public float ClampedProjectedPixelSpan
        => float.IsFinite(ProjectedPixelSpan)
            ? MathF.Max(0.0f, ProjectedPixelSpan)
            : 0.0f;

    public float ClampedScreenCoverage
        => float.IsFinite(ScreenCoverage)
            ? Math.Clamp(ScreenCoverage, 0.0f, 1.0f)
            : 0.0f;
    public float NormalizedUvDensityHint
        => float.IsFinite(UvDensityHint)
            ? Math.Clamp(UvDensityHint, 0.5f, 2.0f)
            : 1.0f;

    public SparseTextureStreamingPageSelection NormalizedPageSelection => PageSelection.Normalize();
}

internal readonly record struct ImportedTextureStreamingPolicyInput(
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    bool PreviewReady,
    long LastVisibleFrameId,
    float MinVisibleDistance,
    float MaxProjectedPixelSpan,
    float MaxScreenCoverage,
    float UvDensityHint,
    string? SamplerName,
    long LastBoundFrameId = long.MinValue);

internal readonly record struct ImportedTextureStreamingBudgetInput(
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    uint PreviewMaxDimension,
    SparseTextureStreamingPageSelection PageSelection);

public readonly record struct ImportedTextureStreamingTelemetry(
    string BackendName,
    int ActiveImportScopes,
    int TrackedTextureCount,
    int PendingTransitionCount,
    int ActiveDecodeCount,
    int QueuedDecodeCount,
    int ActiveGpuUploadCount,
    int QueuedTransitionsThisFrame,
    int QueuedPromotionTransitionsThisFrame,
    int QueuedDemotionTransitionsThisFrame,
    long LastFrameId,
    long CurrentManagedBytes,
    long AvailableManagedBytes,
    long AssignedManagedBytes,
    long UploadBytesScheduledThisFrame,
    bool PromotionsBlocked);

public readonly record struct ImportedTextureStreamingTextureTelemetry(
    string? TextureName,
    string? FilePath,
    string? SamplerName,
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    uint DesiredResidentMaxDimension,
    uint PendingResidentMaxDimension,
    long LastVisibleFrameId,
    float MinVisibleDistance,
    float MaxProjectedPixelSpan,
    float MaxScreenCoverage,
    float UvDensityHint,
    float CurrentPageCoverage,
    float DesiredPageCoverage,
    bool PreviewReady,
    long CurrentCommittedBytes,
    long DesiredCommittedBytes,
    bool HasPendingTransition,
    bool IsVisible,
    bool IsSlow,
    bool WasPressureDemoted,
    bool HasValidationFailure,
    int ValidationFailureCount,
    double OldestQueueWaitMilliseconds,
    double LastUploadMilliseconds,
    float PriorityScore,
    string BackendName);

internal interface ITextureStreamingSource
{
    string SourcePath { get; }
    TextureStreamingResidentData LoadResidentData(uint maxResidentDimension, bool includeMipChain, CancellationToken cancellationToken);
}

internal interface ITextureResidencyBackend
{
    string Name { get; }
    uint PreviewMaxDimension { get; }
    bool SupportsSparseResidency { get; }
    int ActiveDecodeCount { get; }
    int QueuedDecodeCount { get; }
    int ActiveGpuUploadCount { get; }
    long UploadBytesScheduledThisFrame { get; }
    long EstimateCommittedBytes(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, ESizedInternalFormat format = ESizedInternalFormat.Rgba8, int sparseNumLevels = 0, SparseTextureStreamingPageSelection pageSelection = default);
    uint GetNextLowerResidentSize(uint sourceMaxDimension, uint currentResidentSize);
    EnumeratorJob SchedulePreviewLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low);
    EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low);
}
