using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private long _lastOpenXrFailureDiagnosticTimestamp;
    private int _openXrFailureDiagnosticCount;

    /// <summary>Records bounded opt-in rejection evidence even when release logging is compiled out.</summary>
    private void RecordOpenXrRenderFailure(
        in OpenXrEyeMirrorRenderRequest request,
        string stage,
        string? detail = null,
        Exception? exception = null)
    {
        if (!VulkanFrameDiagnosticsTraceEnabled || _openXrFailureDiagnosticCount >= 64)
            return;

        long now = Stopwatch.GetTimestamp();
        if (_lastOpenXrFailureDiagnosticTimestamp != 0 &&
            now - _lastOpenXrFailureDiagnosticTimestamp < Stopwatch.Frequency)
            return;

        _lastOpenXrFailureDiagnosticTimestamp = now;
        _openXrFailureDiagnosticCount++;
        Debug.WriteAuxiliaryLog("openxr-render-failures.log",
            $"Stage={stage} EngineFrame={RuntimeEngine.Rendering.State.RenderFrameId} " +
            $"XrFrame={request.SubmissionMetadata.FrameId} DisplayTime={request.SubmissionMetadata.PredictedDisplayTime} " +
            $"View={request.OpenXrViewIndex} Image={request.OpenXrImageIndex} " +
            $"Planner={request.ResourcePlannerStateIndex} External={request.RendersExternalSwapchainTarget} " +
            $"Extent={request.Extent.Width}x{request.Extent.Height} Detail={detail} Exception={exception}");
    }
}
