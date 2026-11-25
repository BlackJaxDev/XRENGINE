using XREngine.Data.Geometry;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    internal bool TryCaptureWorldUiTarget(XRFrameBuffer target,
        out VulkanRecordedRenderTargetSnapshot snapshot)
    {
        snapshot = default;
        return _resourceRuntime.BackendObjects.Get(target) is VkFrameBuffer wrapper &&
            wrapper.TryCaptureRecordedRenderTargetSnapshot(out snapshot);
    }

    internal override bool DefersUiWorldOutputNativeValidation => true;

    internal override bool TryBeginUiWorldOutputAttempt(XRWindow window,
        XRRenderPipelineInstance pipeline, UIPublication publication,
        BoundingRectangle region, ulong dispatchToken, ulong receiptId,
        in RenderFrameViewDescriptor frozenView, out ulong token)
    {
        token = 0UL;
        if (!SupportsCheckedWorldUiOutput ||
            !ReferenceEquals(window.Renderer, this) ||
            window.ActiveWorldUiDispatchToken != dispatchToken ||
            dispatchToken == 0UL || receiptId == 0UL)
            return false;

        return _worldUiProofs.TryBeginAttempt(window, pipeline, publication, region, in frozenView,
            RuntimeEngine.Rendering.State.RenderFrameId, dispatchToken, receiptId, out token);
    }

    internal override bool CompleteUiWorldOutputAttempt(ulong token,
        out string? failure)
    {
        if (_worldUiProofs.TryCompleteAttempt(token))
        {
            failure = null;
            return true;
        }
        failure = "The Vulkan world UI attempt lost its pending proof.";
        return false;
    }

    internal bool TryClaimPendingUiWorldOutput(ulong frameId, ulong receiptId,
        out VulkanWorldUiNativeProof proof)
        => _worldUiProofs.TryClaimPending(frameId, receiptId, out proof);

    internal override void AbortUiWorldOutputAttempt(ulong token)
        => _worldUiProofs.AbortAttempt(token);

    internal override bool IsUiWorldOutputAttemptStillValid(ulong token)
        => _worldUiProofs.IsAttemptStillValid(token);

    internal override bool TryBeginUiWorldOutputStep(ulong attemptToken,
        EUIWorldOutputStage stage, XRTexture source,
        XRFrameBuffer? destination, XRMeshRenderer mesh, XRMaterial material,
        BoundingRectangle region, out ulong stepToken)
        => _worldUiProofs.TryBeginStep(attemptToken, stage, source, destination, mesh, material,
            region, out stepToken);

    internal override bool CompleteUiWorldOutputStep(ulong stepToken)
        => _worldUiProofs.TryCompleteStep(stepToken);

    internal override void AbortUiWorldOutputStep(ulong stepToken)
        => _worldUiProofs.AbortStep(stepToken);
}
