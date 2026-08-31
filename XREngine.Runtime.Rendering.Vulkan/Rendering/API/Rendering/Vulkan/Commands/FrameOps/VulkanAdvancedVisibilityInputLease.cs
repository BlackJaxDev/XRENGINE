using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded queue-owned lease over an immutable advanced-visibility authoring
/// snapshot. One reference belongs to each authored operation; copies retain
/// explicitly and lowering or abandonment releases exactly once.
/// </summary>
internal sealed class VulkanAdvancedVisibilityInputLease
{
    private int _referenceCount;

    internal VulkanAdvancedVisibilityInputLease()
        => Input = new VulkanAdvancedVisibilityInputStorage(
            drawCapacity: 0,
            indirectRangeCapacity: 0,
            fixedCapacity: false,
            EVulkanAcceptedFrameLane.MainScene);

    internal VulkanAdvancedVisibilityInputStorage Input { get; }

    internal bool IsAvailable => Volatile.Read(ref _referenceCount) == 0;

    internal bool MatchesRequest(
        in VulkanAdvancedVisibilityStageRequest request)
        => Volatile.Read(ref _referenceCount) > 0 &&
           Input.MatchesRequest(in request);

    internal bool TryRetain()
    {
        int observed = Volatile.Read(ref _referenceCount);
        while (observed > 0)
        {
            int prior = Interlocked.CompareExchange(
                ref _referenceCount,
                observed + 1,
                observed);
            if (prior == observed)
                return true;
            observed = prior;
        }

        return false;
    }

    internal void RetainOrThrow()
    {
        if (!TryRetain())
        {
            throw new InvalidOperationException(
                "An advanced visibility authoring operation copied an inactive input lease.");
        }
    }

    internal bool TryCapture(
        in VulkanAdvancedVisibilityStageRequest request,
        out string failureReason)
    {
        if (!IsAvailable)
        {
            failureReason =
                "The advanced visibility authoring lease is already active.";
            return false;
        }
        if (!Input.TryCaptureAtAuthoring(in request, out failureReason))
            return false;

        Volatile.Write(ref _referenceCount, 1);
        return true;
    }

    internal void Release()
    {
        int remaining = Interlocked.Decrement(ref _referenceCount);
        if (remaining >= 0)
            return;

        Interlocked.Exchange(ref _referenceCount, 0);
        throw new InvalidOperationException(
            "An advanced visibility authoring lease was released more than once.");
    }

    internal static void ReleaseOperations(ReadOnlySpan<FrameOp> operations)
    {
        for (int index = 0; index < operations.Length; ++index)
        {
            operations[index].ReleaseAuthoringSnapshot();
            if (operations[index] is AdvancedVisibilityOp visibility)
                visibility.ReleaseInputLease();
        }
    }
}
