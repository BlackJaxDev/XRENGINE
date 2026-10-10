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
        if (Interlocked.CompareExchange(ref _referenceCount, -1, 0) != 0)
        {
            failureReason =
                "The advanced visibility authoring lease is already active.";
            return false;
        }
        bool captured = false;
        try
        {
            captured = Input.TryCaptureAtAuthoring(in request, out failureReason);
            if (!captured)
                Input.Reset();
            return captured;
        }
        catch
        {
            Input.Reset();
            throw;
        }
        finally
        {
            Volatile.Write(ref _referenceCount, captured ? 1 : 0);
        }
    }

    internal void Release()
    {
        while (true)
        {
            int observed = Volatile.Read(ref _referenceCount);
            if (observed <= 0)
                throw new InvalidOperationException(
                    "An advanced visibility authoring lease was released more than once.");
            int next = observed == 1 ? -1 : observed - 1;
            if (Interlocked.CompareExchange(ref _referenceCount, next, observed) != observed)
                continue;
            if (next != -1)
                return;
            try
            {
                Input.Reset();
            }
            finally
            {
                Volatile.Write(ref _referenceCount, 0);
            }
            return;
        }
    }

    internal static void ReleaseOperations(ReadOnlySpan<FrameOp> operations)
    {
        for (int index = 0; index < operations.Length; ++index)
        {
            FrameOp? operation = operations[index];
            if (operation is null)
                continue;

            operation.ReleaseAuthoringSnapshot();
            if (operation is AdvancedVisibilityOp visibility)
                visibility.ReleaseInputLease();
        }
    }
}
