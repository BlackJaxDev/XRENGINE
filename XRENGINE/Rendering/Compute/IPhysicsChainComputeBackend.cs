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

    bool EnsureGpuBufferReady(XRDataBuffer buffer);
    bool TryCopyBuffer(in PhysicsChainComputeBufferCopy copy);
    bool TryDispatchIndirect(XRRenderProgram program, XRDataBuffer arguments, nint byteOffset);
    void CompletePass(in PhysicsChainComputePass pass);
    XRGpuFence? InsertFence();
    bool TryReadBuffer(XRDataBuffer buffer, Span<byte> destination);
}
