namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IRenderResourceRetirementBackendCapability
{
    private readonly List<WebGpuFence> _pendingEngineFences = new(4);

    bool IRenderResourceRetirementBackendCapability.RequiresBlockingRetirementProgress => false;

    void IRenderResourceRetirementBackendCapability.PrepareForPhysicalResourceDestruction(string reason)
        => PrepareForApiObjectTeardown();

    /// <summary>Receipts issued while recording are armed only after that frame reaches its submission boundary.</summary>
    public override XRGpuFence InsertGpuFence()
    {
        WebGpuFence fence = new(_engineRecording && _engineCommandCount != 0);
        if (_engineRecording)
        {
            if (_pendingEngineFences.Count >= 16)
            {
                fence.Dispose();
                throw new InvalidOperationException("WebGPU.Fence.CapacityExceeded: the active frame exceeds sixteen completion receipts.");
            }
            _pendingEngineFences.Add(fence);
        }
        else fence.Arm(this, submittedFrame: false);
        return fence;
    }

    private void ArmPendingEngineFences(bool submittedFrame = false)
    {
        // Retirement before the first draw fences the prior queue. A receipt
        // covering abandoned recorded commands instead reports failed submission.
        foreach (WebGpuFence fence in _pendingEngineFences) fence.Arm(this, submittedFrame);
        _pendingEngineFences.Clear();
    }
}
