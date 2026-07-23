using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainComputeBackendContractTests
{
    [Test]
    public void RequiredPipeline_RequiresEveryBackendOperation()
    {
        var complete = new PhysicsChainComputeCapabilities(true, true, true, true, true);
        var missingReadback = complete with { SupportsAsyncReadback = false };

        complete.SupportsRequiredPipeline.ShouldBeTrue();
        missingReadback.SupportsRequiredPipeline.ShouldBeFalse();
        missingReadback.SupportsCorePipeline.ShouldBeTrue();
        missingReadback.SupportsReadbackPipeline.ShouldBeFalse();
    }

    [Test]
    public void CompletePass_ForwardsBackendNeutralSynchronizationDescriptor()
    {
        var backend = new RecordingPhysicsChainComputeBackend();
        var pass = new PhysicsChainComputePass(
            PhysicsChainComputePassKind.Simulation,
            EMemoryBarrierMask.ShaderStorage);

        PhysicsChainComputeEnqueueStatus status = backend.TryCompletePass(pass);

        status.ShouldBe(PhysicsChainComputeEnqueueStatus.Enqueued);
        backend.LastCompletedPass.ShouldBe(pass);
    }

    private sealed class RecordingPhysicsChainComputeBackend : IPhysicsChainComputeBackend
    {
        public AbstractRenderer Renderer => null!;
        public string Name => "Recording";
        public PhysicsChainComputeCapabilities Capabilities => new(true, true, true, true, true);
        public PhysicsChainComputePass? LastCompletedPass { get; private set; }

        public bool BeginBatch() => true;
        public void CommitBatch() { }
        public void RollbackBatch() { }
        public bool EnsureGpuBufferReady(XRDataBuffer buffer) => true;
        public PhysicsChainComputeEnqueueStatus TryDispatchDirect(
            XRRenderProgram program,
            uint groupsX,
            uint groupsY,
            uint groupsZ,
            PhysicsChainComputePassKind passKind)
            => PhysicsChainComputeEnqueueStatus.Enqueued;
        public PhysicsChainComputeEnqueueStatus TryCopyBuffer(in PhysicsChainComputeBufferCopy copy)
            => PhysicsChainComputeEnqueueStatus.Enqueued;
        public PhysicsChainComputeEnqueueStatus TryDispatchIndirect(
            XRRenderProgram program,
            XRDataBuffer arguments,
            nint byteOffset)
            => PhysicsChainComputeEnqueueStatus.Enqueued;
        public PhysicsChainComputeEnqueueStatus TryCompletePass(in PhysicsChainComputePass pass)
        {
            LastCompletedPass = pass;
            return PhysicsChainComputeEnqueueStatus.Enqueued;
        }
        public XRGpuFence? InsertFence() => null;
        public bool TryReadBuffer(XRDataBuffer buffer, Span<byte> destination) => true;
    }
}
