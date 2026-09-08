namespace XREngine.Rendering;

/// <summary>
/// Backend-owned receipt for one exact output-producing pipeline invocation.
/// The output contract and physical target remain frozen until the receipt is
/// submitted, failed, or discarded.
/// </summary>
internal readonly struct RenderOutputCompletionBackendReservation(
    ulong receiptId,
    RenderOutputRequest output,
    XRFrameBuffer targetFrameBuffer,
    XRGpuFence? fence,
    int backendSlot,
    uint backendGeneration)
{
    internal ulong ReceiptId { get; } = receiptId;
    internal RenderOutputRequest Output { get; } = output;
    internal XRFrameBuffer TargetFrameBuffer { get; } = targetFrameBuffer;
    internal XRGpuFence? Fence { get; } = fence;
    internal int BackendSlot { get; } = backendSlot;
    internal uint BackendGeneration { get; } = backendGeneration;
    internal bool IsValid =>
        ReceiptId != 0UL && Output.IsDefined && BackendSlot >= 0 &&
        BackendGeneration != 0U;
}
