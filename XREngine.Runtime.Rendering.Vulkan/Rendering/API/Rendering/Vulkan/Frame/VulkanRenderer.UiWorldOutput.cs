using XREngine.Data.Geometry;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    private readonly object _worldUiProofSync = new();
    private VulkanWorldUiNativeProof[]? _worldUiProofs;
    private ulong _nextWorldUiNativeToken;
    private ulong _nextWorldUiStepToken;

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

        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        lock (_worldUiProofSync)
        {
            _worldUiProofs ??=
            [
                new VulkanWorldUiNativeProof(this),
                new VulkanWorldUiNativeProof(this),
                new VulkanWorldUiNativeProof(this),
            ];
            foreach (VulkanWorldUiNativeProof candidate in _worldUiProofs)
            {
                ulong next = ++_nextWorldUiNativeToken;
                if (next == 0UL)
                    next = ++_nextWorldUiNativeToken;
                if (!candidate.TryReserve(window, pipeline, publication, region,
                        frozenView, frameId, dispatchToken, receiptId, next))
                    continue;
                token = next;
                return true;
            }
        }
        return false;
    }

    internal override bool CompleteUiWorldOutputAttempt(ulong token,
        out string? failure)
    {
        lock (_worldUiProofSync)
        {
            if (_worldUiProofs is not null)
                foreach (VulkanWorldUiNativeProof candidate in _worldUiProofs)
                    if (candidate.NativeToken == token &&
                        candidate.TryMarkAuthored(token))
                    {
                        failure = null;
                        return true;
                    }
        }
        failure = "The Vulkan world UI attempt lost its pending proof.";
        return false;
    }

    internal bool TryClaimPendingUiWorldOutput(ulong frameId, ulong receiptId,
        out VulkanWorldUiNativeProof proof)
    {
        lock (_worldUiProofSync)
        {
            if (_worldUiProofs is not null)
                foreach (VulkanWorldUiNativeProof candidate in _worldUiProofs)
                    if (candidate.TryClaim(frameId, receiptId))
                    {
                        proof = candidate;
                        return true;
                    }
        }
        proof = null!;
        return false;
    }

    internal override void AbortUiWorldOutputAttempt(ulong token)
    {
        VulkanWorldUiNativeProof? toDispose = null;
        lock (_worldUiProofSync)
        {
            if (_worldUiProofs is null)
                return;
            foreach (VulkanWorldUiNativeProof candidate in _worldUiProofs)
                if (candidate.NativeToken == token)
                {
                    bool ownedByPlan = candidate.IsOwnedByPlan;
                    candidate.Fail();
                    if (!ownedByPlan)
                        toDispose = candidate;
                    break;
                }
        }
        toDispose?.ReleaseUnclaimed(token);
    }

    internal override bool IsUiWorldOutputAttemptStillValid(ulong token)
    {
        lock (_worldUiProofSync)
        {
            if (_worldUiProofs is null)
                return false;
            foreach (VulkanWorldUiNativeProof candidate in _worldUiProofs)
                if (candidate.NativeToken == token)
                    return candidate.IsStillValid;
        }
        return false;
    }

    internal override bool TryBeginUiWorldOutputStep(ulong attemptToken,
        EUIWorldOutputStage stage, XRTexture source,
        XRFrameBuffer? destination, XRMeshRenderer mesh, XRMaterial material,
        BoundingRectangle region, out ulong stepToken)
    {
        stepToken = 0UL;
        lock (_worldUiProofSync)
        {
            if (_worldUiProofs is null)
                return false;
            foreach (VulkanWorldUiNativeProof candidate in _worldUiProofs)
            {
                if (candidate.NativeToken != attemptToken)
                    continue;
                ulong next = ++_nextWorldUiStepToken;
                if (next == 0UL)
                    next = ++_nextWorldUiStepToken;
                if (!candidate.TryReserveStep(stage, source, destination,
                        mesh, material, region, next))
                    return false;
                stepToken = next;
                return true;
            }
        }
        return false;
    }

    internal override bool CompleteUiWorldOutputStep(ulong stepToken)
    {
        lock (_worldUiProofSync)
        {
            if (_worldUiProofs is not null)
                foreach (VulkanWorldUiNativeProof candidate in _worldUiProofs)
                    if (candidate.OwnsStep(stepToken))
                        return candidate.TryMarkStepAuthored(stepToken);
        }
        return false;
    }

    internal override void AbortUiWorldOutputStep(ulong stepToken)
    {
        lock (_worldUiProofSync)
        {
            if (_worldUiProofs is null)
                return;
            foreach (VulkanWorldUiNativeProof candidate in _worldUiProofs)
                if (candidate.OwnsStep(stepToken))
                {
                    candidate.Fail();
                    return;
                }
        }
    }
}
