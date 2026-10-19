namespace XREngine.Rendering.Vulkan;

/// <summary>Checks frozen capture ownership before an unsubmitted frame can execute.</summary>
internal static class VulkanCaptureOwnerReadiness
{
    internal static bool HasClosedOwner(ReadOnlySpan<FrameOp> operations)
    {
        for (int index = 0; index < operations.Length; index++)
            if (operations[index].ContextReference.CaptureOwnerToken is { IsOpen: false })
                return true;
        return false;
    }

    internal static bool HasClosedOwner(ReadOnlySpan<VulkanMeshRenderRequest> requests)
    {
        for (int index = 0; index < requests.Length; index++)
            if (requests[index].Context.CaptureOwnerToken is { IsOpen: false })
                return true;
        return false;
    }

    internal static bool HasClosedOwner(VulkanPreparedMeshIngress ingress)
    {
        for (int index = 0; index < ingress.Count; index++)
            if (ingress.GetEntry(index).Context.CaptureOwnerToken is { IsOpen: false })
                return true;
        return false;
    }

    internal static bool HasClosedOwner(FrameOperationSequence operations)
    {
        for (int index = 0; index < operations.Length; index++)
            if (operations.GetContext(index).CaptureOwnerToken is { IsOpen: false })
                return true;
        return false;
    }

    internal static bool HasClosedOwner(FramePlan plan)
        => plan.IsSealed &&
           (HasClosedOwner(plan.GetNativeStaticOperationsForRecording()) ||
            HasClosedOwner(plan.GetNativeDynamicOverlayOperationsForRecording()) ||
            HasClosedOwner(plan.GetNativeTextureUploadOperationsForRecording()));

    internal static bool HasClosedOwner(VulkanAcceptedFramePlan plan)
        => HasClosedOwner(plan.AuthoredOperations) ||
           HasClosedOwner(plan.StaticOperations.AsSpan(0, plan.StaticOperationCount)) ||
           HasClosedOwner(plan.DynamicUiOperations.AsSpan(0, plan.DynamicUiOperationCount)) ||
           HasClosedOwner(plan.PreparedMeshIngress) ||
           (plan.IsSealed && HasClosedOwner(plan.LogicalPlan));
}
