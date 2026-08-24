namespace XREngine.Rendering;

/// <summary>
/// Immutable, backend-neutral final-output state acquired for one render frame.
/// Native images, views, synchronization primitives, and JavaScript handles stay
/// in the concrete backend lease and never cross this runtime contract.
/// </summary>
public readonly record struct RenderFrameOutputDescription(
    RenderExecutionMode ExecutionMode,
    RenderTargetOutputProperties Properties,
    ulong TargetGeneration,
    uint FrameSlotIndex,
    uint ViewIndex = 0,
    RenderFrameOutputCapabilities Capabilities = RenderFrameOutputCapabilities.None)
{
    /// <summary>Gets whether this description identifies a usable acquired output.</summary>
    public bool IsValid =>
        TargetGeneration != 0 &&
        Properties.Width != 0 &&
        Properties.Height != 0 &&
        Properties.Layers != 0;

    /// <summary>Validates the frame-scoped output before render-graph execution.</summary>
    public void Validate()
    {
        Properties.Validate();
        if (TargetGeneration == 0)
            throw new ArgumentOutOfRangeException(nameof(TargetGeneration), "A frame output must identify a non-zero target generation.");
        if (ViewIndex >= Properties.Layers)
            throw new ArgumentOutOfRangeException(nameof(ViewIndex), "The frame-output view index must address one of the declared layers.");
    }
}
