using XREngine.Data.Geometry;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the three reusable native proofs for checked world UI output on one Vulkan renderer.
/// A proof is reserved when a world UI output attempt begins, marked authored when its
/// recorded steps complete, and claimed by the accepted frame plan that submits it.
/// One lock serializes every proof and token change, because attempts begin on the render
/// thread and claims happen during frame planning.
/// </summary>
internal sealed class VulkanWorldUiProofSet(VulkanRenderer owner)
{
    private readonly object _sync = new();
    private VulkanWorldUiNativeProof[]? _proofs;
    private ulong _nextNativeToken;
    private ulong _nextStepToken;

    /// <summary>
    /// Reserves an idle proof for one attempt and returns its new nonzero native token.
    /// Returns false when all three proofs are in use.
    /// </summary>
    public bool TryBeginAttempt(XRWindow window, XRRenderPipelineInstance pipeline,
        UIPublication publication, BoundingRectangle region, in RenderFrameViewDescriptor frozenView,
        ulong frameId, ulong dispatchToken, ulong receiptId, out ulong token)
    {
        lock (_sync)
        {
            _proofs ??=
            [
                new VulkanWorldUiNativeProof(owner),
                new VulkanWorldUiNativeProof(owner),
                new VulkanWorldUiNativeProof(owner),
            ];
            foreach (VulkanWorldUiNativeProof candidate in _proofs)
            {
                ulong next = NextToken(ref _nextNativeToken);
                if (!candidate.TryReserve(window, pipeline, publication, region,
                        frozenView, frameId, dispatchToken, receiptId, next))
                    continue;
                token = next;
                return true;
            }
        }
        token = 0UL;
        return false;
    }

    /// <summary>Marks the proof that owns <paramref name="token"/> as authored.</summary>
    public bool TryCompleteAttempt(ulong token)
    {
        lock (_sync)
        {
            if (_proofs is not null)
                foreach (VulkanWorldUiNativeProof candidate in _proofs)
                    if (candidate.NativeToken == token && candidate.TryMarkAuthored(token))
                        return true;
        }
        return false;
    }

    /// <summary>Gives the authored proof for one frame and receipt to the frame plan.</summary>
    public bool TryClaimPending(ulong frameId, ulong receiptId, out VulkanWorldUiNativeProof proof)
    {
        lock (_sync)
        {
            if (_proofs is not null)
                foreach (VulkanWorldUiNativeProof candidate in _proofs)
                    if (candidate.TryClaim(frameId, receiptId))
                    {
                        proof = candidate;
                        return true;
                    }
        }
        proof = null!;
        return false;
    }

    /// <summary>
    /// Fails the attempt. A proof that no frame plan owns is released after the lock exits,
    /// because the release can retire native resources.
    /// </summary>
    public void AbortAttempt(ulong token)
    {
        VulkanWorldUiNativeProof? toRelease = null;
        lock (_sync)
        {
            if (_proofs is null)
                return;
            foreach (VulkanWorldUiNativeProof candidate in _proofs)
                if (candidate.NativeToken == token)
                {
                    bool ownedByPlan = candidate.IsOwnedByPlan;
                    candidate.Fail();
                    if (!ownedByPlan)
                        toRelease = candidate;
                    break;
                }
        }
        toRelease?.ReleaseUnclaimed(token);
    }

    /// <summary>Returns true while the attempt's proof still holds the token and has not failed.</summary>
    public bool IsAttemptStillValid(ulong token)
    {
        lock (_sync)
        {
            if (_proofs is null)
                return false;
            foreach (VulkanWorldUiNativeProof candidate in _proofs)
                if (candidate.NativeToken == token)
                    return candidate.IsStillValid;
        }
        return false;
    }

    /// <summary>Reserves one recorded step inside an attempt and returns its new nonzero step token.</summary>
    public bool TryBeginStep(ulong attemptToken, EUIWorldOutputStage stage, XRTexture source,
        XRFrameBuffer? destination, XRMeshRenderer mesh, XRMaterial material,
        BoundingRectangle region, out ulong stepToken)
    {
        stepToken = 0UL;
        lock (_sync)
        {
            if (_proofs is null)
                return false;
            foreach (VulkanWorldUiNativeProof candidate in _proofs)
            {
                if (candidate.NativeToken != attemptToken)
                    continue;
                ulong next = NextToken(ref _nextStepToken);
                if (!candidate.TryReserveStep(stage, source, destination, mesh, material, region, next))
                    return false;
                stepToken = next;
                return true;
            }
        }
        return false;
    }

    /// <summary>Marks a reserved step as recorded.</summary>
    public bool TryCompleteStep(ulong stepToken)
    {
        lock (_sync)
        {
            if (_proofs is not null)
                foreach (VulkanWorldUiNativeProof candidate in _proofs)
                    if (candidate.OwnsStep(stepToken))
                        return candidate.TryMarkStepAuthored(stepToken);
        }
        return false;
    }

    /// <summary>Fails the attempt that owns the step.</summary>
    public void AbortStep(ulong stepToken)
    {
        lock (_sync)
        {
            if (_proofs is null)
                return;
            foreach (VulkanWorldUiNativeProof candidate in _proofs)
                if (candidate.OwnsStep(stepToken))
                {
                    candidate.Fail();
                    return;
                }
        }
    }

    /// <summary>Advances a token counter and skips zero, which means "no token".</summary>
    private static ulong NextToken(ref ulong counter)
    {
        ulong next = ++counter;
        return next == 0UL ? ++counter : next;
    }
}
