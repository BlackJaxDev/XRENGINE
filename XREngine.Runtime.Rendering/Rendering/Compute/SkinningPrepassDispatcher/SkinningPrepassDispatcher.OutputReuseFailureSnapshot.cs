using XREngine.Rendering;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    /// <summary>
    /// Keeps the input, receipt, and buffer reuse state from one rejected overwrite.
    /// </summary>
    internal readonly record struct OutputReuseFailureSnapshot(
        string? MeshName,
        string FailureReason,
        string? ReuseRejection,
        bool SeedInputsSettled,
        bool HasValidOutput,
        bool SkinnedOutputDirty,
        bool HasPendingInputChanges,
        ulong LastOutputVersion,
        ulong CurrentOutputVersion,
        ulong OutputAttemptId,
        ulong OutputAttemptVersion,
        ulong ActiveProducerAttemptId,
        ulong LastComputePrepassFrameId,
        ulong? ReceiptAttemptId,
        bool? ReceiptProducerAccepted,
        int? ReceiptReferenceCount,
        int? ReceiptConsumerUseCount,
        int LastPoseHash,
        int CurrentPoseHash,
        bool HasExternalSkinPaletteSource,
        bool HasGpuDrivenBoneSource,
        EGpuFenceSubmissionStatus? ReceiptSubmissionStatus,
        bool ReceiptHasConsumerUses,
        EGpuBufferContentReuseStatus? PositionsReuse,
        EGpuBufferContentReuseStatus? NormalsReuse,
        EGpuBufferContentReuseStatus? TangentsReuse,
        EGpuBufferContentReuseStatus? InterleavedReuse);
}
