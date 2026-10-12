namespace XREngine.Rendering.WebGPU;

/// <summary>A cold, detached snapshot of opt-in managed engine-frame counters.</summary>
/// <remarks>
/// Allocation deltas use GC.GetAllocatedBytesForCurrentThread on the synchronous render
/// callback. Recording includes frame setup and resource preparation; submission includes
/// packet preparation, interop and managed result publication; cleanup includes frame
/// retirement and scope disposal. They exclude world update/collection outside the callback,
/// other threads, JavaScript/GPU allocations and this on-demand snapshot's serialization.
/// Recorded counts include incomplete and faulted frames. Submission bytes count spans passed
/// to SubmitEngineFrame, including rejected or throwing calls, not confirmed GPU uploads.
/// Returned submission results report canvas presentation, not GPU validation/completion;
/// an unpresented submission can still enqueue offscreen, compute or preparation-only work.
/// </remarks>
public sealed record WebGpuEngineFrameStatistics
{
    public bool Enabled { get; internal set; }
    /// <summary>Renderer callback entries while capture is enabled, excluding host ticks that never call the renderer.</summary>
    public long FrameAttempts { get; internal set; }
    public long FramesBegun { get; internal set; }
    public long NoOutputFrames { get; internal set; }
    public long IncompleteFrames { get; internal set; }
    public long FailedFrames { get; internal set; }
    public long ReentrantFrameAttempts { get; internal set; }
    /// <summary>Callbacks that finished with a current output-ready frame and complete managed publication.</summary>
    public long CompletedFrames { get; internal set; }
    /// <summary>Managed import call attempts, including calls that throw during marshalling or JavaScript execution.</summary>
    public long SubmitEngineFrameInteropCalls { get; internal set; }
    /// <summary>Legacy standalone receipt polls; completion now returns with frame acceptance.</summary>
    public long CompletionPollInteropCalls { get; internal set; }
    /// <summary>Legacy separate preparation imports; ordinary engine frames leave this counter at zero.</summary>
    public long AdvancedPreparationUploadInteropCalls { get; internal set; }
    public long AdvancedPreparationUploadBytes { get; internal set; }
    /// <summary>Initialization and texture-transfer records passed to the shared acceptance import, including rejected attempts.</summary>
    public long PreparationUploadRecords { get; internal set; }
    /// <summary>Exact retained preparation payload extents; texture transport can include at most three padding bytes per mip.</summary>
    public long PreparationUploadBytes { get; internal set; }
    public long ReturnedSubmissions { get; internal set; }
    public long PresentedSubmissions { get; internal set; }
    public long UnpresentedSubmissions { get; internal set; }
    /// <summary>Committed managed command-arena records, which may each replay multiple prepared GPU commands.</summary>
    public long CommandRecords { get; internal set; }
    /// <summary>Managed mesh draws recorded by the renderer; this is not the JavaScript executor's total GPU draw count.</summary>
    public long MeshDraws { get; internal set; }
    /// <summary>Ordered buffer-copy uploads, including authored non-storage buffers and retry preambles.</summary>
    public long StorageUploadRecords { get; internal set; }
    /// <summary>Used uniform arena extents, including dynamic-offset alignment padding.</summary>
    public long RecordedUniformBytes { get; internal set; }
    /// <summary>Payload bytes in the shared buffer-upload arena; the historical name also includes non-storage buffers.</summary>
    public long RecordedStorageBytes { get; internal set; }
    public long SubmissionPacketBytes { get; internal set; }
    public long SubmissionUniformBytes { get; internal set; }
    public long SubmissionStorageBytes { get; internal set; }
    public long RecordingAllocatedBytes { get; internal set; }
    public long SubmissionAllocatedBytes { get; internal set; }
    public long CleanupAllocatedBytes { get; internal set; }
    public long FrameAllocatedBytes { get; internal set; }
    public uint LastFrameSequence { get; internal set; }
    /// <summary>NotCaptured, NoOutput, Incomplete, Faulted, Unpresented, or Presented; never a GPU completion assertion.</summary>
    public string LastFrameOutcome { get; internal set; } = "NotCaptured";
    public int LastFrameCommandRecords { get; internal set; }
    public int LastFrameMeshDraws { get; internal set; }
    public int LastFrameStorageUploadRecords { get; internal set; }
    public int LastFrameRecordedUniformBytes { get; internal set; }
    public int LastFrameRecordedStorageBytes { get; internal set; }
    public int LastFrameSubmitEngineFrameInteropCalls { get; internal set; }
    public int LastFrameCompletionPollInteropCalls { get; internal set; }
    public int LastFrameAdvancedPreparationUploadInteropCalls { get; internal set; }
    public long LastFrameAdvancedPreparationUploadBytes { get; internal set; }
    public int LastFramePreparationUploadRecords { get; internal set; }
    public int LastFramePreparationUploadBytes { get; internal set; }
    public bool LastFrameSubmissionReturned { get; internal set; }
    public bool LastFrameSubmissionPresented { get; internal set; }
    public int LastFrameSubmissionPacketBytes { get; internal set; }
    public int LastFrameSubmissionUniformBytes { get; internal set; }
    public int LastFrameSubmissionStorageBytes { get; internal set; }
    public long LastFrameRecordingAllocatedBytes { get; internal set; }
    public long LastFrameSubmissionAllocatedBytes { get; internal set; }
    public long LastFrameCleanupAllocatedBytes { get; internal set; }
    public long LastFrameAllocatedBytes { get; internal set; }
}
