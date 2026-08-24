using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>Allocation-free CPU stage scope that records into <see cref="VulkanFrameTelemetry"/>.</summary>
internal readonly ref struct VulkanCpuStageScope
{
    private static readonly bool s_detailedDiagnosticsEnabled =
        XREnvironment.IsEnabled(
            XREngineEnvironmentVariables.VulkanRecordingDiag);
    private static readonly bool s_fineGrainedProfilingEnabled =
        s_detailedDiagnosticsEnabled ||
        XREnvironment.IsEnabled(
            XREngineEnvironmentVariables.VulkanRecordingProfileDetail);

    private readonly EVulkanCpuStage _stage;
    private readonly VulkanFrameTelemetry _telemetry;
    private readonly long _startTimestamp;
    private readonly long _startAllocatedBytes;
    private readonly long _beginBoundaryAllocatedBytes;
    private readonly VulkanCpuSpanProfiler.VulkanCpuSpanToken _spanToken;
    private readonly bool _active;
    private readonly bool _captureAllocations;

    internal static bool DetailedDiagnosticsEnabled
        => s_detailedDiagnosticsEnabled;

    public VulkanCpuStageScope(
        VulkanFrameTelemetry telemetry,
        EVulkanCpuStage stage,
        bool enabled = true)
    {
        _telemetry = telemetry;
        _stage = stage;
        bool spanCapture =
            VulkanCpuSpanProfiler.IsStageCaptureEnabled(stage);
        _active =
            (enabled || spanCapture) &&
            (!IsFineGrainedHotPathStage(stage) ||
             s_fineGrainedProfilingEnabled ||
             spanCapture);
        _captureAllocations =
            _active && (s_detailedDiagnosticsEnabled || spanCapture);
        if (!_active)
        {
            _startTimestamp = 0;
            _startAllocatedBytes = 0;
            _beginBoundaryAllocatedBytes = 0;
            _spanToken = default;
            return;
        }

        _startTimestamp = Stopwatch.GetTimestamp();
        long beforeBeginAllocatedBytes = _captureAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        _spanToken = VulkanCpuSpanProfiler.Begin(stage, _startTimestamp, beforeBeginAllocatedBytes);
        _startAllocatedBytes = _captureAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        _beginBoundaryAllocatedBytes = Math.Max(0, _startAllocatedBytes - beforeBeginAllocatedBytes);
    }

    public void Dispose()
    {
        if (!_active)
            return;

        long endAllocatedBytes = _captureAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        long endTimestamp = Stopwatch.GetTimestamp();
        VulkanCpuSpanProfiler.End(_spanToken, endTimestamp, endAllocatedBytes);
        long afterBoundaryAllocatedBytes = _captureAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : 0;
        _telemetry.RecordCpuStage(
            _stage,
            Stopwatch.GetElapsedTime(_startTimestamp, endTimestamp),
            Math.Max(0, endAllocatedBytes - _startAllocatedBytes),
            _beginBoundaryAllocatedBytes + Math.Max(0, afterBoundaryAllocatedBytes - endAllocatedBytes));
    }

    private static bool IsFineGrainedHotPathStage(EVulkanCpuStage stage)
        => stage is
            EVulkanCpuStage.PrimaryOperationPreparation or
            EVulkanCpuStage.PrimaryMeshOperation or
            EVulkanCpuStage.PrimaryNonMeshOperation or
            EVulkanCpuStage.ContextPassTransitions or
            EVulkanCpuStage.BarrierPlanningEmission or
            EVulkanCpuStage.OpDispatch or
            EVulkanCpuStage.MeshDrawPreparation or
            EVulkanCpuStage.MeshDrawResourcePreparation or
            EVulkanCpuStage.MeshDrawBindingPreparation or
            EVulkanCpuStage.MeshDrawMaterialBindings or
            EVulkanCpuStage.MeshDrawBindingSnapshotCopy or
            EVulkanCpuStage.MeshDrawEnqueue or
            EVulkanCpuStage.FrameDataDescriptorValidation or
            EVulkanCpuStage.FrameDataEngineUniformUpload or
            EVulkanCpuStage.FrameDataAutoUniformUpload or
            EVulkanCpuStage.CommandDependencyComparison or
            EVulkanCpuStage.CommandDirtyPropagation or
            EVulkanCpuStage.CommandCacheScanning or
            EVulkanCpuStage.MeshDrawPublisherState or
            EVulkanCpuStage.MeshDrawArtifactEligibility or
            EVulkanCpuStage.MeshDrawArtifactLookup;
}
