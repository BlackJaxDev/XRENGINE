namespace XREngine.Rendering;

/// <summary>
/// Owns pending imported-texture transition state on registry records.
/// Thread-safety: methods lock the target record unless the name explicitly says
/// the caller must already hold <see cref="ImportedTextureStreamingRecord.Sync"/>.
/// </summary>
internal sealed class TextureTransitionQueue
{
    public bool TryBeginTransition(
        ImportedTextureStreamingRecord record,
        CancellationTokenSource cts,
        uint pendingMaxDimension,
        SparseTextureStreamingPageSelection pageSelection,
        long frameId,
        bool pressureDemotion,
        uint previousResidentSize,
        long previousCommittedBytes,
        long targetCommittedBytes,
        string backendName,
        string reason,
        JobPriority priority,
        TextureUploadPriorityClass uploadPriorityClass,
        out CancellationTokenSource? previousPendingLoadCts)
    {
        previousPendingLoadCts = null;
        lock (record.Sync)
        {
            previousPendingLoadCts = record.PendingLoadCts;
            record.PendingLoadCts = cts;
            record.PendingMaxDimension = pendingMaxDimension;
            record.PendingPageSelection = pageSelection;
            record.PendingTransitionQueuedTimestamp = TextureRuntimeDiagnostics.StartTiming();
            record.PendingTransitionWasPressureDemotion = pressureDemotion;
            record.PendingTransitionPreviousResidentSize = previousResidentSize;
            record.PendingTransitionPreviousCommittedBytes = previousCommittedBytes;
            record.PendingTransitionTargetCommittedBytes = targetCommittedBytes;
            record.PendingTransitionBackendName = backendName;
            record.PendingTransitionReason = reason;
            record.PendingTransitionPriority = priority;
            record.PendingTransitionUploadPriorityClass = uploadPriorityClass;
            record.PendingSparseTransitionRequest = default;
            record.PendingSparseTransitionResult = null;
            record.LastTransitionFrameId = frameId;
            return true;
        }
    }

    public bool TryForceClearStuckTransition(
        ImportedTextureStreamingRecord record,
        long frameId,
        out CancellationTokenSource? pendingLoadCts)
    {
        pendingLoadCts = null;
        lock (record.Sync)
        {
            if (record.PendingMaxDimension == 0)
                return false;

            pendingLoadCts = record.PendingLoadCts;
            ClearPendingStateAfterLock(record);
            record.LastTransitionFrameId = frameId;
            return true;
        }
    }

    public static void ClearPendingStateAfterLock(ImportedTextureStreamingRecord record)
    {
        record.PendingMaxDimension = 0;
        record.PendingLoadCts = null;
        record.PendingPageSelection = SparseTextureStreamingPageSelection.Full;
        record.PendingSparseTransitionRequest = default;
        record.PendingSparseTransitionResult = null;
        record.PendingTransitionQueuedTimestamp = 0L;
        record.PendingTransitionWasPressureDemotion = false;
        record.PendingTransitionPreviousResidentSize = 0;
        record.PendingTransitionPreviousCommittedBytes = 0L;
        record.PendingTransitionTargetCommittedBytes = 0L;
        record.PendingTransitionBackendName = string.Empty;
        record.PendingTransitionReason = string.Empty;
        record.PendingTransitionPriority = JobPriority.Low;
        record.PendingTransitionUploadPriorityClass = TextureUploadPriorityClass.Background;
    }

    public static void CancelPendingLoad(CancellationTokenSource? cts)
    {
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
