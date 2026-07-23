using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainSelectiveReadbackDispatcherTests
{
    [Test]
    public void GpuFenceAdapter_UsesOnlyNonBlockingPollAndOwnsFenceLifetime()
    {
        var rendererFence = new RecordingGpuFence(EGpuFenceStatus.Pending);
        var adapter = new PhysicsChainGpuReadbackFence();
        adapter.Reset(rendererFence);

        adapter.Poll().ShouldBe(PhysicsChainReadbackFenceStatus.Pending);
        rendererFence.PollCount.ShouldBe(1);
        rendererFence.Status = EGpuFenceStatus.Signaled;
        adapter.Poll().ShouldBe(PhysicsChainReadbackFenceStatus.Signaled);

        adapter.Dispose();
        adapter.Dispose();
        rendererFence.DisposeCount.ShouldBe(1);
    }

    [Test]
    public void Dispatcher_SubmitsExactMappedStagingCopiesAfterProducerBarriers()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");
        int producerBarrier = source.IndexOf("TryCompletePass(backend, BonePaletteCompletionPass)", StringComparison.Ordinal);
        int selectiveQueue = source.IndexOf("QueueSelectiveReadbacks(backend, _dispatchGroup);", producerBarrier, StringComparison.Ordinal);

        producerBarrier.ShouldBeGreaterThanOrEqualTo(0);
        selectiveQueue.ShouldBeGreaterThan(producerBarrier);
        source.ShouldContain("nuint exactByteCount = (nuint)plan.ByteCount;");
        source.ShouldContain("DefaultMemoryPolicy = XRBufferMemoryPolicy.GpuToCpuReadback");
        source.ShouldContain("EBufferMapStorageFlags.Read | EBufferMapStorageFlags.ClientStorage");
        source.ShouldContain("EMemoryBarrierMask.BufferUpdate | EMemoryBarrierMask.GpuReadback");
        source.ShouldContain("world.CommitReadbackStagingSlot(lease, source, fence");
        source.ShouldContain("PollSelectiveReadbackTransfersOnce();");
        source.ShouldContain("if (_lastReadbackPollFrame == _readbackFrameIndex)");
    }

    [Test]
    public void StrictZeroReadback_DoesNotMapOrReadActiveCountsAndSelectiveMappingIsRequestDriven()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");
        string selectiveMethod = SliceMethod(source, "private void SubmitSelectiveReadbackPlan(", "private bool TryFindRequest(");
        string activeMethod = SliceMethod(source, "private bool DispatchActiveWorkGeneration(", "private bool DispatchMainPhysics(");

        selectiveMethod.ShouldContain("PhysicsChainReadbackGatherPlan plan");
        selectiveMethod.ShouldContain("TryAcquireReadbackStagingSlot");
        selectiveMethod.ShouldNotContain("TryReadBuffer(");
        activeMethod.ShouldNotContain("TryReadBuffer(");
        activeMethod.ShouldNotContain("MapBufferData(");
        source.ShouldNotContain("TryReadBuffer(_activeWorkCounterBuffer");
    }

    [Test]
    public void Epochs_AreBumpedAtBackendArenaAndStableSliceBoundaries()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");

        source.ShouldContain("BumpEpoch(ref _readbackBackendGeneration);");
        source.ShouldContain("BumpEpoch(ref _readbackArenaGeneration);");
        source.ShouldContain("if (allocationChanged)\n                BumpEpoch(ref _readbackLayoutGeneration);");
        source.ShouldContain("new(_readbackBackendGeneration, _readbackArenaGeneration, _readbackLayoutGeneration)");
    }

    private static string SliceMethod(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }

    private sealed class RecordingGpuFence(EGpuFenceStatus status) : XRGpuFence
    {
        public EGpuFenceStatus Status { get; set; } = status;
        public int PollCount { get; private set; }
        public int DisposeCount { get; private set; }

        protected override EGpuFenceStatus PollCore()
        {
            ++PollCount;
            return Status;
        }

        protected override void DisposeCore()
            => ++DisposeCount;
    }
}
