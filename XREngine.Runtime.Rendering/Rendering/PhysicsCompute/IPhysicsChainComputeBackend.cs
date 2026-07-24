using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Narrow renderer-backend contract required by the batched physics-chain dispatcher.
/// </summary>
public interface IPhysicsChainComputeBackend
{
    AbstractRenderer Renderer { get; }
    string Name { get; }
    PhysicsChainComputeCapabilities Capabilities { get; }

    bool BeginBatch();
    void CommitBatch();
    void RollbackBatch();
    bool EnsureGpuBufferReady(XRDataBuffer buffer);
    PhysicsChainComputeEnqueueStatus TryDispatchDirect(
        XRRenderProgram program,
        uint groupsX,
        uint groupsY,
        uint groupsZ,
        PhysicsChainComputePassKind passKind);
    PhysicsChainComputeEnqueueStatus TryCopyBuffer(in PhysicsChainComputeBufferCopy copy);
    PhysicsChainComputeEnqueueStatus TryDispatchIndirect(
        XRRenderProgram program,
        XRDataBuffer arguments,
        nint byteOffset);
    PhysicsChainComputeEnqueueStatus TryCompletePass(in PhysicsChainComputePass pass);
    XRGpuFence? InsertFence();
    bool TryReadBuffer(XRDataBuffer buffer, Span<byte> destination);
}
