namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compatibility surface translating renderer API calls into the frame-operation
/// queue and its renderer-free lowering/signature semantics.
/// </summary>
public unsafe partial class VulkanRenderer
{
    internal void EnqueueFrameOp(FrameOp operation)
    {
        int passIndex = operation is TextureUploadFrameOp
            ? operation.PassIndex
            : EnsureValidPassIndex(
                operation.PassIndex,
                VulkanFrameOperationSemantics.GetFrameOpDiagnosticName(operation),
                operation.Context.PassMetadata);
        _frameOperationQueue.EnqueuePrepared(
            VulkanFrameOperationSemantics.Prepare(operation, passIndex));
    }

    public bool TryBeginOrderedComputeBatch()
        => _frameOperationQueue.TryBeginOrderedBatch();

    public void CommitOrderedComputeBatch()
        => _frameOperationQueue.CommitOrderedBatch();

    public void RollbackOrderedComputeBatch()
        => _frameOperationQueue.RollbackOrderedBatch();

    private bool TryGetLastFrameOpForTarget(XRFrameBuffer target, out FrameOp operation)
        => _frameOperationQueue.TryGetLastForTarget(target, out operation);

    internal FrameOp[] CaptureFrameOpsExcludingTextureUploads(
        Action emitFrameOps,
        out ulong signature)
    {
        FrameOp[] operations = _frameOperationQueue.Capture(
            emitFrameOps,
            excludeTextureUploads: true);
        signature = operations.Length == 0
            ? 0
            : VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations);
        return operations;
    }

    internal FrameOp[] CaptureFrameOpsExcludingTextureUploads(
        IOpenXrEyeFrameOpEmitter emitter,
        in OpenXrEyeFrameOpEmission emission,
        out ulong signature)
    {
        FrameOp[] operations = _frameOperationQueue.Capture(
            emitter,
            emission,
            excludeTextureUploads: true);
        signature = operations.Length == 0
            ? 0
            : VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations);
        return operations;
    }

    private void ReleaseCurrentThreadFrameOpCaptureCaches()
        => _frameOperationQueue.ReleaseCurrentThread();

    internal FrameOp[] DrainFrameOps()
        => _frameOperationQueue.DrainPending();

    internal FrameOp[] DrainFrameOps(out ulong signature)
        => DrainFrameOps(out signature, computeSignature: true);

    internal FrameOp[] DrainFrameOps(out ulong signature, bool computeSignature)
    {
        FrameOp[] operations = _frameOperationQueue.DrainPending();
        signature = computeSignature && operations.Length != 0
            ? VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations)
            : 0;
        return operations;
    }

    internal FrameOp[] DrainFrameOpsSplitTextureUploads(
        out FrameOp[] textureUploadOperations,
        out ulong signature,
        bool computeSignature)
    {
        FrameOp[] operations = _frameOperationQueue.DrainForPrimary(
            out textureUploadOperations);
        signature = computeSignature && operations.Length != 0
            ? VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations)
            : 0;
        return operations;
    }

    internal FrameOp[] DrainTextureUploadFrameOps()
        => _frameOperationQueue.DrainTextureUploads();

    internal FrameOp[] DrainFrameOpsExcludingTextureUploads(
        out ulong signature,
        bool computeSignature = true)
    {
        FrameOp[] operations = _frameOperationQueue.DrainExcludingTextureUploads();
        signature = computeSignature && operations.Length != 0
            ? VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations)
            : 0;
        return operations;
    }

    internal static bool IsFrameSourceSamplerName(string? name)
        => VulkanFrameOperationSemantics.IsFrameSourceSamplerName(name);

    internal static string GetFrameOpDiagnosticName(FrameOp operation)
        => VulkanFrameOperationSemantics.GetFrameOpDiagnosticName(operation);

    internal static ulong ComputeFrameOpsSignature(FrameOperationSequence operations)
        => VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations);
}
