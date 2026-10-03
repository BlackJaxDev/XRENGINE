namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private WebGpuEngineFrameStatistics? _engineFrameStatistics;
    private bool _engineFrameStatisticsActive;

    /// <summary>Formats pending shader stages outside successful frame recording.</summary>
    public string GetPendingEngineProgramStatus()
    {
        RequireEngineFrameStatisticsBoundary();
        System.Text.StringBuilder output = new();
        foreach (AbstractRenderAPIObject resource in RenderObjectCache.Values)
            if (resource is WebGpuRenderProgram program)
                program.AppendPendingPreparation(output);
        if (OperatingSystem.IsBrowser() && _session != 0)
        {
            string resources = WebGpuImports.GetResourcePreparationStatus(_session);
            if (resources.Length != 0)
            {
                if (output.Length != 0)
                    output.Append(" | ");
                output.Append("GPU resources=").Append(resources);
            }
        }
        return output.Length == 0 ? "none" : output.ToString();
    }

    /// <summary>Changes allocation/counter capture between frames; storage is allocated only here.</summary>
    public void ConfigureEngineFrameStatistics(bool enabled, bool reset = true)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        RequireEngineFrameStatisticsBoundary();
        WebGpuEngineFrameStatistics statistics = !reset && _engineFrameStatistics is { } previous ? previous : new();
        statistics.Enabled = enabled;
        SetField(ref _engineFrameStatistics, statistics, publishNotifications: false);
    }

    /// <summary>Allocates a detached snapshot only on explicit request, outside frame recording.</summary>
    public WebGpuEngineFrameStatistics CaptureEngineFrameStatistics()
    {
        RequireEngineFrameStatisticsBoundary();
        return _engineFrameStatistics is { } statistics ? statistics with { } : new();
    }

    private void RequireEngineFrameStatisticsBoundary()
    {
        if (_engineRecording || _engineFrameStatisticsActive)
            throw new InvalidOperationException("WebGPU.Statistics.ActiveFrame: configure or capture statistics between frames.");
    }

    private WebGpuEngineFrameStatistics? BeginEngineFrameStatistics()
    {
        if (_engineFrameStatistics is not { Enabled: true } statistics)
            return null;
        statistics.FrameAttempts++;
        if (_engineFrameStatisticsActive)
        {
            statistics.ReentrantFrameAttempts++;
            statistics.FailedFrames++;
            throw new InvalidOperationException("WebGPU.Frame.Reentrant: an engine frame is already recording.");
        }
        SetField(ref _engineFrameStatisticsActive, true, publishNotifications: false);
        statistics.LastFrameSubmitEngineFrameInteropCalls = 0;
        statistics.LastFrameCompletionPollInteropCalls = 0;
        statistics.LastFrameAdvancedPreparationUploadInteropCalls = 0;
        statistics.LastFrameAdvancedPreparationUploadBytes = 0;
        statistics.LastFrameSubmissionReturned = false;
        statistics.LastFrameSubmissionPresented = false;
        statistics.LastFrameSubmissionPacketBytes = 0;
        statistics.LastFrameSubmissionUniformBytes = 0;
        statistics.LastFrameSubmissionStorageBytes = 0;
        return statistics;
    }

    private void CountEngineFrameSubmission(int packetBytes, int uniformBytes, int storageBytes)
    {
        if (!_engineFrameStatisticsActive || _engineFrameStatistics is not { } statistics)
            return;
        statistics.SubmitEngineFrameInteropCalls++;
        statistics.LastFrameSubmitEngineFrameInteropCalls++;
        statistics.SubmissionPacketBytes += packetBytes;
        statistics.SubmissionUniformBytes += uniformBytes;
        statistics.SubmissionStorageBytes += storageBytes;
        statistics.LastFrameSubmissionPacketBytes += packetBytes;
        statistics.LastFrameSubmissionUniformBytes += uniformBytes;
        statistics.LastFrameSubmissionStorageBytes += storageBytes;
    }

    private void CountEngineFrameSubmissionResult(bool presented)
    {
        if (!_engineFrameStatisticsActive || _engineFrameStatistics is not { } statistics)
            return;
        statistics.ReturnedSubmissions++;
        if (presented) statistics.PresentedSubmissions++;
        else statistics.UnpresentedSubmissions++;
        statistics.LastFrameSubmissionReturned = true;
        statistics.LastFrameSubmissionPresented = presented;
    }

    private void CompleteEngineFrameStatistics(WebGpuEngineFrameStatistics statistics, bool frameBegun,
        string outcome, long recordingBytes, long submissionBytes, long totalBytes)
    {
        if (frameBegun) statistics.FramesBegun++;
        switch (outcome)
        {
            case "NoOutput": statistics.NoOutputFrames++; break;
            case "Incomplete": statistics.IncompleteFrames++; break;
            case "Faulted": statistics.FailedFrames++; break;
            case "Presented": statistics.CompletedFrames++; break;
        }
        statistics.LastFrameSequence = frameBegun ? _engineFrameSequence : 0;
        statistics.LastFrameOutcome = outcome;
        statistics.LastFrameCommandRecords = frameBegun ? _engineCommandCount : 0;
        statistics.LastFrameMeshDraws = frameBegun ? _engineMeshDrawCount : 0;
        statistics.LastFrameStorageUploadRecords = frameBegun ? _engineUploadCount : 0;
        statistics.LastFrameRecordedUniformBytes = frameBegun ? _engineUniformBytes : 0;
        statistics.LastFrameRecordedStorageBytes = frameBegun ? _engineStorageBytes : 0;
        statistics.CommandRecords += statistics.LastFrameCommandRecords;
        statistics.MeshDraws += statistics.LastFrameMeshDraws;
        statistics.StorageUploadRecords += statistics.LastFrameStorageUploadRecords;
        statistics.RecordedUniformBytes += statistics.LastFrameRecordedUniformBytes;
        statistics.RecordedStorageBytes += statistics.LastFrameRecordedStorageBytes;
        statistics.LastFrameRecordingAllocatedBytes = recordingBytes;
        statistics.LastFrameSubmissionAllocatedBytes = submissionBytes;
        statistics.LastFrameCleanupAllocatedBytes = totalBytes - recordingBytes - submissionBytes;
        statistics.LastFrameAllocatedBytes = totalBytes;
        statistics.RecordingAllocatedBytes += recordingBytes;
        statistics.SubmissionAllocatedBytes += submissionBytes;
        statistics.CleanupAllocatedBytes += statistics.LastFrameCleanupAllocatedBytes;
        statistics.FrameAllocatedBytes += totalBytes;
        SetField(ref _engineFrameStatisticsActive, false, publishNotifications: false);
    }
}
