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
    }

    [Test]
    public void CompletePass_ForwardsBackendNeutralSynchronizationDescriptor()
    {
        var backend = new RecordingPhysicsChainComputeBackend();
        var pass = new PhysicsChainComputePass(
            PhysicsChainComputePassKind.Simulation,
            EMemoryBarrierMask.ShaderStorage);

        backend.CompletePass(pass);

        backend.LastCompletedPass.ShouldBe(pass);
    }

    private sealed class RecordingPhysicsChainComputeBackend : IPhysicsChainComputeBackend
    {
        public AbstractRenderer Renderer => null!;
        public string Name => "Recording";
        public PhysicsChainComputeCapabilities Capabilities => new(true, true, true, true, true);
        public PhysicsChainComputePass? LastCompletedPass { get; private set; }

        public bool EnsureGpuBufferReady(XRDataBuffer buffer) => true;
        public bool TryCopyBuffer(in PhysicsChainComputeBufferCopy copy) => true;
        public bool TryDispatchIndirect(XRRenderProgram program, XRDataBuffer arguments, nint byteOffset) => true;
        public void CompletePass(in PhysicsChainComputePass pass) => LastCompletedPass = pass;
        public XRGpuFence? InsertFence() => null;
        public bool TryReadBuffer(XRDataBuffer buffer, Span<byte> destination) => true;
    }
}
