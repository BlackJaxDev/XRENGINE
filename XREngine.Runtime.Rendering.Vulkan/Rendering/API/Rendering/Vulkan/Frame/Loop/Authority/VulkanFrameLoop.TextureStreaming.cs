using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop : IVulkanTextureUploadScheduler
{
    /// <summary>
    /// Advances the bounded Vulkan upload lane before a new frame snapshot is
    /// accepted, so PresentNow never depends on a render-thread coroutine that
    /// can run only after the frame returns.
    /// </summary>
    internal void ProcessPendingUploads()
        => _resourceRuntime.Uploads.ProcessPendingUploads(
            CreateTextureUploadSchedulingContext());

    /// <summary>
    /// Freezes the exact renderer and backend generation that own texture work.
    /// Queued callbacks retain this identity instead of consulting ambient
    /// renderer state when a later render-thread job pump executes them.
    /// </summary>
    private VulkanTextureUploadSchedulingContext CreateTextureUploadSchedulingContext()
        => new(
            _ownerRenderer,
            _backendGeneration,
            BackendObjectContext,
            _resourceRuntime,
            _commandRuntime);

    bool IVulkanTextureUploadScheduler.IsSynchronizedUploadAvailable
        => VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable;

    bool IVulkanTextureUploadScheduler.TryScheduleImportedTextureUpload(
        XRTexture2D target,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        uint maxResidentDimension,
        long streamingGeneration,
        TextureUploadPriorityClass priority,
        Func<bool>? shouldAcceptResult,
        Action<XRTexture2D>? onFinished,
        Action? onCanceled,
        Action<Exception>? onError,
        CancellationToken cancellationToken)
        => TryScheduleImportedTextureResidencyTransition(
            target,
            residentData,
            includeMipChain,
            maxResidentDimension,
            streamingGeneration,
            priority,
            shouldAcceptResult,
            onFinished,
            onCanceled,
            onError,
            cancellationToken,
            out _);

    internal void EnqueueImportedTextureUpload(VulkanImportedTexturePendingUpload upload)
    {
        TextureUploadFrameOp operation = new(upload, CaptureFrameOpContextOrLastActive());
        _commandRuntime.EnqueueFrameOperation(
            _framePlanner.Operations,
            operation,
            operation.PassIndex);
    }

    internal void CancelPendingImportedTextureUploadFrameOps(string reason)
    {
        FrameOp[] pendingOps = _commandRuntime.DrainFrameOperations(
            _framePlanner.Operations,
            excludeTextureUploads: false);
        VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
            pendingOps);
        int canceledUploads = 0;
        for (int i = 0; i < pendingOps.Length; i++)
        {
            if (pendingOps[i] is not TextureUploadFrameOp uploadOp)
                continue;

            VulkanImportedTexturePendingUpload upload = uploadOp.Upload;
            upload.Texture.ReleasePreparedImportedUploadResources(upload);
            _resourceRuntime.Uploads.RecordState(
                upload.Request,
                VulkanTextureUploadGenerationState.Canceled,
                reason);
            InvokePendingTextureUploadCanceled(upload);
            canceledUploads++;
        }

        if (canceledUploads > 0)
        {
            Debug.Vulkan(
                "[Vulkan] Canceled {0} pending imported texture upload frame op(s). Reason={1}",
                canceledUploads,
                reason);
        }
    }

    internal bool TryScheduleImportedTextureResidencyTransition(
        XRTexture2D texture,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        uint targetResidentMaxDimension,
        long streamingGeneration,
        TextureUploadPriorityClass priorityClass,
        Func<bool>? shouldAcceptResult,
        Action<XRTexture2D>? onFinished,
        Action? onCanceled,
        Action<Exception>? onError,
        CancellationToken cancellationToken)
        => TryScheduleImportedTextureResidencyTransition(
            texture, residentData, includeMipChain, targetResidentMaxDimension,
            streamingGeneration, priorityClass, shouldAcceptResult, onFinished,
            onCanceled, onError, cancellationToken, out _);

    internal bool TryScheduleImportedTextureResidencyTransition(
        XRTexture2D texture,
        TextureStreamingResidentData residentData,
        bool includeMipChain,
        uint targetResidentMaxDimension,
        long streamingGeneration,
        TextureUploadPriorityClass priorityClass,
        Func<bool>? shouldAcceptResult,
        Action<XRTexture2D>? onFinished,
        Action? onCanceled,
        Action<Exception>? onError,
        CancellationToken cancellationToken,
        out VulkanTextureUploadTicket ticket)
        => _resourceRuntime.Uploads.TryScheduleImportedTextureUpload(
            CreateTextureUploadSchedulingContext(),
            texture,
            residentData,
            includeMipChain,
            targetResidentMaxDimension,
            streamingGeneration,
            priorityClass,
            shouldAcceptResult,
            onFinished,
            onCanceled,
            onError,
            cancellationToken,
            out ticket);

    private static void InvokePendingTextureUploadCanceled(
        VulkanImportedTexturePendingUpload upload)
    {
        try
        {
            upload.OnCanceled?.Invoke();
        }
        catch (Exception exception)
        {
            upload.OnError?.Invoke(exception);
        }
    }
}
