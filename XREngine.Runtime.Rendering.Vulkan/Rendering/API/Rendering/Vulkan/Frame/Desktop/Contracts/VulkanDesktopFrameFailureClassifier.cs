using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Converts stage-specific frame state into a stable terminal category without
/// parsing exception messages. Native OOM and cancellation take precedence over
/// the outer readiness/recording wrapper that carried them to settlement.
/// </summary>
internal static class VulkanDesktopFrameFailureClassifier
{
    internal static VulkanDesktopFrameFailure Classify(
        EDesktopFrameReason reason,
        EVulkanFrameStage lastStage,
        Result acquireResult,
        Result submitResult,
        Result presentResult,
        Exception? exception)
    {
        if (exception is null &&
            reason is EDesktopFrameReason.None or
                EDesktopFrameReason.Success or
                EDesktopFrameReason.PresentSuboptimal)
        {
            return VulkanDesktopFrameFailure.None;
        }

        Result nativeResult = SelectNativeResult(
            reason,
            acquireResult,
            submitResult,
            presentResult);
        EVulkanDesktopFrameFailureKind kind =
            ClassifyException(exception, ref nativeResult);
        if (kind == EVulkanDesktopFrameFailureKind.None)
            kind = ClassifyNativeResult(nativeResult);
        if (kind == EVulkanDesktopFrameFailureKind.None)
            kind = ClassifyReason(reason, exception);

        return new VulkanDesktopFrameFailure(
            kind,
            ResolveStage(reason, lastStage),
            nativeResult,
            exception?.GetType().FullName,
            exception?.Message);
    }

    private static EVulkanDesktopFrameFailureKind ClassifyException(
        Exception? exception,
        ref Result nativeResult)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is OperationCanceledException)
                return EVulkanDesktopFrameFailureKind.CallerCanceled;

            if (current is VulkanOutOfMemoryException vulkanOom)
            {
                if (vulkanOom.NativeResult is { } oomResult)
                    nativeResult = oomResult;

                return nativeResult == Result.ErrorOutOfHostMemory
                    ? EVulkanDesktopFrameFailureKind.HostOutOfMemory
                    : EVulkanDesktopFrameFailureKind.DeviceOutOfMemory;
            }

            if (current is OutOfMemoryException)
                return EVulkanDesktopFrameFailureKind.HostOutOfMemory;
        }

        return EVulkanDesktopFrameFailureKind.None;
    }

    private static EVulkanDesktopFrameFailureKind ClassifyNativeResult(
        Result result)
        => result switch
        {
            Result.ErrorOutOfHostMemory =>
                EVulkanDesktopFrameFailureKind.HostOutOfMemory,
            Result.ErrorOutOfDeviceMemory =>
                EVulkanDesktopFrameFailureKind.DeviceOutOfMemory,
            Result.ErrorDeviceLost =>
                EVulkanDesktopFrameFailureKind.DeviceLost,
            Result.ErrorOutOfDateKhr =>
                EVulkanDesktopFrameFailureKind.OutOfDate,
            Result.ErrorSurfaceLostKhr =>
                EVulkanDesktopFrameFailureKind.SurfaceLost,
            Result.NotReady or Result.Timeout =>
                EVulkanDesktopFrameFailureKind.NoImageAvailable,
            _ => EVulkanDesktopFrameFailureKind.None,
        };

    private static EVulkanDesktopFrameFailureKind ClassifyReason(
        EDesktopFrameReason reason,
        Exception? exception)
        => reason switch
        {
            EDesktopFrameReason.AcquireNotReady or
            EDesktopFrameReason.AcquireTimeout =>
                EVulkanDesktopFrameFailureKind.NoImageAvailable,
            EDesktopFrameReason.AcquireOutOfDate or
            EDesktopFrameReason.PresentOutOfDate =>
                EVulkanDesktopFrameFailureKind.OutOfDate,
            EDesktopFrameReason.AcquireSurfaceLost or
            EDesktopFrameReason.PresentSurfaceLost =>
                EVulkanDesktopFrameFailureKind.SurfaceLost,
            EDesktopFrameReason.AcquireDeviceLost or
            EDesktopFrameReason.PresentDeviceLost =>
                EVulkanDesktopFrameFailureKind.DeviceLost,
            EDesktopFrameReason.ResourceGenerationBlocked or
            EDesktopFrameReason.FrameSlotBusy or
            EDesktopFrameReason.RecordingDeferred or
            EDesktopFrameReason.RecordingResourceRetired or
            EDesktopFrameReason.RecordingDirtied =>
                EVulkanDesktopFrameFailureKind.AdmissionDeferred,
            EDesktopFrameReason.PresentNowReadinessFailed =>
                EVulkanDesktopFrameFailureKind.ReadinessFailed,
            EDesktopFrameReason.RecordingFailed or
            EDesktopFrameReason.OverlayRecordingFailed =>
                EVulkanDesktopFrameFailureKind.RecordingFailed,
            EDesktopFrameReason.SubmitFailed =>
                EVulkanDesktopFrameFailureKind.SubmissionFailed,
            EDesktopFrameReason.PresentUnexpectedFailure =>
                EVulkanDesktopFrameFailureKind.PresentationFailed,
            EDesktopFrameReason.AcquireUnexpectedFailure =>
                EVulkanDesktopFrameFailureKind.Unexpected,
            _ when exception is not null =>
                EVulkanDesktopFrameFailureKind.Unexpected,
            _ => EVulkanDesktopFrameFailureKind.None,
        };

    private static Result SelectNativeResult(
        EDesktopFrameReason reason,
        Result acquireResult,
        Result submitResult,
        Result presentResult)
    {
        if (reason is >= EDesktopFrameReason.AcquireNotReady and
            <= EDesktopFrameReason.AcquireUnexpectedFailure)
        {
            return acquireResult;
        }

        if (reason == EDesktopFrameReason.SubmitFailed)
            return submitResult;

        if (reason is >= EDesktopFrameReason.PresentOutOfDate and
            <= EDesktopFrameReason.PresentUnexpectedFailure)
        {
            return presentResult;
        }

        if (IsTerminalNativeFailure(acquireResult))
            return acquireResult;
        if (IsTerminalNativeFailure(submitResult))
            return submitResult;
        return IsTerminalNativeFailure(presentResult)
            ? presentResult
            : Result.Success;
    }

    private static bool IsTerminalNativeFailure(Result result)
        => result is Result.ErrorOutOfHostMemory or
            Result.ErrorOutOfDeviceMemory or
            Result.ErrorDeviceLost or
            Result.ErrorOutOfDateKhr or
            Result.ErrorSurfaceLostKhr;

    private static EVulkanFrameStage ResolveStage(
        EDesktopFrameReason reason,
        EVulkanFrameStage lastStage)
        => reason switch
        {
            EDesktopFrameReason.AcquireNotReady or
            EDesktopFrameReason.AcquireTimeout or
            EDesktopFrameReason.AcquireOutOfDate or
            EDesktopFrameReason.AcquireSurfaceLost or
            EDesktopFrameReason.AcquireDeviceLost or
            EDesktopFrameReason.AcquireUnexpectedFailure =>
                EVulkanFrameStage.OutputAcquire,
            EDesktopFrameReason.PresentNowReadinessFailed =>
                EVulkanFrameStage.ResourcePrepare,
            EDesktopFrameReason.RecordingDeferred or
            EDesktopFrameReason.RecordingResourceRetired or
            EDesktopFrameReason.RecordingFailed or
            EDesktopFrameReason.OverlayRecordingFailed or
            EDesktopFrameReason.RecordingDirtied =>
                EVulkanFrameStage.CommandRecord,
            EDesktopFrameReason.SubmitFailed =>
                EVulkanFrameStage.QueueSubmit,
            EDesktopFrameReason.PresentOutOfDate or
            EDesktopFrameReason.PresentSuboptimal or
            EDesktopFrameReason.PresentSurfaceLost or
            EDesktopFrameReason.PresentDeviceLost or
            EDesktopFrameReason.PresentUnexpectedFailure =>
                EVulkanFrameStage.OutputComplete,
            _ => lastStage,
        };
}
