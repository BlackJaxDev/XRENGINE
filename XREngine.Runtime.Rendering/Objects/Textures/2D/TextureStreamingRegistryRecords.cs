using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

internal sealed class ImportedTextureStreamingRecord(XRTexture2D texture)
{
    /// <summary>
    /// Guards transition state, format, source, backend, pending-upload bookkeeping,
    /// and any other fields that mutate during streaming work. Held briefly by snapshot
    /// collection and may be held longer during transition queueing/finalization.
    /// </summary>
    public readonly object Sync = new();

    /// <summary>
    /// Guards the per-frame visibility / material-binding observation fields
    /// (<see cref="LastVisibleFrameId"/>, <see cref="MinVisibleDistance"/>,
    /// <see cref="MaxProjectedPixelSpan"/>, <see cref="MaxScreenCoverage"/>,
    /// <see cref="UvDensityHint"/>, <see cref="VisiblePageSelection"/>,
    /// <see cref="LastBoundFrameId"/>, <see cref="LastBoundMaterialTextureCount"/>).
    /// Kept independent from <see cref="Sync"/> so per-frame CollectVisible /
    /// material-binding observations never block on (or get dropped because of)
    /// transition / import work that holds <see cref="Sync"/>.
    /// </summary>
    public readonly object VisibilityLock = new();

    public readonly WeakReference<XRTexture2D> Texture = new(texture);
    public string? FilePath = texture.FilePath;
    public ITextureStreamingSource? Source;
    public ITextureResidencyBackend? Backend;
    public ESizedInternalFormat Format = texture.SizedInternalFormat;
    public uint SourceWidth;
    public uint SourceHeight;
    public uint ResidentMaxDimension;
    public uint DesiredMaxDimension;
    public uint PendingMaxDimension;
    public long ResidentGeneration;
    public long PublishedGeneration;
    public long UploadGeneration;
    public long RetirementGeneration;
    public int SparseNumLevels;
    public long LastVisibleFrameId = long.MinValue;
    public float MinVisibleDistance = float.PositiveInfinity;
    public float MaxProjectedPixelSpan;
    public float MaxScreenCoverage;
    public float UvDensityHint = 1.0f;
    public SparseTextureStreamingPageSelection VisiblePageSelection = SparseTextureStreamingPageSelection.Full;
    public long LastBoundFrameId = long.MinValue;
    public int LastBoundMaterialTextureCount;
    public bool PreviewReady;
    public CancellationTokenSource? PendingLoadCts;
    public SparseTextureStreamingPageSelection PendingPageSelection = SparseTextureStreamingPageSelection.Full;
    public SparseTextureStreamingTransitionRequest PendingSparseTransitionRequest;
    public SparseTextureStreamingTransitionResult? PendingSparseTransitionResult;
    public long LastTransitionFrameId = long.MinValue;
    public long PendingTransitionQueuedTimestamp;
    public double LastTransitionQueueWaitMilliseconds;
    public double LastTransitionExecutionMilliseconds;
    public long PromotionCooldownUntilFrameId = long.MinValue;
    public long DemotionCooldownUntilFrameId = long.MinValue;
    public long FailedTransitionCooldownUntilFrameId = long.MinValue;
    public uint FailedTransitionTargetMaxDimension;
    public int ConsecutiveTransitionFailureCount;
    public long PinUntilFrameId = long.MinValue;
    public long LastPressureDemotionFrameId = long.MinValue;
    public bool PendingTransitionWasPressureDemotion;
    public uint PendingTransitionPreviousResidentSize;
    public long PendingTransitionPreviousCommittedBytes;
    public long PendingTransitionTargetCommittedBytes;
    public string PendingTransitionBackendName = string.Empty;
    public string PendingTransitionReason = string.Empty;
    public JobPriority PendingTransitionPriority = JobPriority.Low;
    public TextureUploadPriorityClass PendingTransitionUploadPriorityClass = TextureUploadPriorityClass.Background;
    public float BaseLodBias;
    public float CurrentStreamingLodBias;
    public float PromotionFadeStartBias;
    public long PromotionFadeStartFrameId = long.MinValue;
    public long PromotionFadeEndFrameId = long.MinValue;
}

internal readonly record struct ImportedTextureStreamingSnapshot(
    ImportedTextureStreamingRecord Record,
    ITextureResidencyBackend Backend,
    string? TextureName,
    string? FilePath,
    string? SamplerName,
    string FairnessGroupKey,
    ESizedInternalFormat Format,
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    uint PendingMaxDimension,
    long ResidentGeneration,
    long PublishedGeneration,
    long UploadGeneration,
    long RetirementGeneration,
    int SparseNumLevels,
    long CurrentCommittedBytes,
    SparseTextureStreamingPageSelection CurrentPageSelection,
    long LastVisibleFrameId,
    long LastBoundFrameId,
    int LastBoundMaterialTextureCount,
    float MinVisibleDistance,
    float MaxProjectedPixelSpan,
    float MaxScreenCoverage,
    float UvDensityHint,
    SparseTextureStreamingPageSelection VisiblePageSelection,
    bool PreviewReady,
    long LastTransitionFrameId,
    long PendingTransitionQueuedTimestamp,
    double LastTransitionQueueWaitMilliseconds,
    double LastTransitionExecutionMilliseconds,
    long LastPressureDemotionFrameId,
    int ValidationFailureCount,
    double LastTextureQueueWaitMilliseconds,
    double LastTextureUploadMilliseconds,
    long FailedTransitionCooldownUntilFrameId,
    uint FailedTransitionTargetMaxDimension,
    int ConsecutiveTransitionFailureCount);


