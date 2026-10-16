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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");
        string groupMethod = SliceMethod(source, "private bool ProcessDispatchGroup(", "internal void ApplyPendingArenaLayoutReset()");
        string groupsMethod = SliceMethod(source, "private bool ProcessDispatchGroups(", "private bool PublishRegisteredOutputs(");
        string publishMethod = SliceMethod(source, "private bool PublishSelectiveReadbacksAfterSimulation(", "private void QueueSelectiveReadbacks(");
        string selectiveMethod = SliceMethod(source, "private void SubmitSelectiveReadbackPlan(", "private bool TryFindRequest(");

        // Each dispatch group closes its simulation with the producer barrier.
        // The selective gather does not run inside a group.
        ShouldAppearInOrder(groupMethod,
            "!DispatchMainPhysics(backend, applyObjectMove: true)",
            "TryCompletePass(backend, SimulationCompletionPass)");
        groupMethod.ShouldNotContain("QueueSelectiveReadbacks(");

        // The selective readback batch starts only after the group batches commit.
        ShouldAppearInOrder(groupsMethod,
            "if (!ProcessDispatchGroup(backend, out string failureStage))",
            "backend.CommitBatch();",
            "bool readbackPublished = PublishSelectiveReadbacksAfterSimulation(backend);");
        ShouldAppearInOrder(publishMethod,
            "backend.BeginBatch()",
            "QueueSelectiveReadbacks(backend);",
            "backend.CommitBatch();");
        publishMethod.ShouldContain("backend.RollbackBatch();");

        // The order is gather, barrier, exact copy to mapped staging, transfer barrier, fence, and commit.
        ShouldAppearInOrder(selectiveMethod,
            "PhysicsChainComputePassKind.SelectiveReadbackGather,",
            "TryCompletePass(backend, SelectiveReadbackGatherCompletionPass)",
            "nuint exactByteCount = (nuint)plan.ByteCount;",
            "TryCopyBuffer(backend, copy, \"selective-gather-to-mapped-staging\")",
            "TryCompletePass(backend, SelectiveReadbackTransferCompletionPass)",
            "XRGpuFence? gpuFence = backend.InsertFence();",
            "world.CommitReadbackStagingSlot(lease, source, fence");
        source.ShouldContain("DefaultMemoryPolicy = XRBufferMemoryPolicy.GpuToCpuReadback");
        source.ShouldContain("EBufferMapStorageFlags.Read | EBufferMapStorageFlags.ClientStorage");
        source.ShouldContain("EMemoryBarrierMask.BufferUpdate | EMemoryBarrierMask.GpuReadback");
        source.ShouldContain("PollSelectiveReadbackTransfersOnce();");
        source.ShouldContain("if (_lastReadbackPollFrame == _readbackFrameIndex)");
    }

    [Test]
    public void StrictZeroReadback_DoesNotMapOrReadActiveCountsAndSelectiveMappingIsRequestDriven()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");
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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");

        source.ShouldContain("BumpEpoch(ref _readbackBackendGeneration);");
        source.ShouldContain("BumpEpoch(ref _readbackArenaGeneration);");
        source.ShouldContain("if (allocationChanged)\n                BumpEpoch(ref _readbackLayoutGeneration);");
        source.ShouldContain("new(_readbackBackendGeneration, _readbackArenaGeneration, _readbackLayoutGeneration)");
    }

    private static string SliceMethod(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"'{startToken}' was not found.");
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"'{endToken}' was not found after '{startToken}'.");
        return source[start..end];
    }

    /// <summary>
    /// Checks that each token occurs in the source after the token before it.
    /// </summary>
    private static void ShouldAppearInOrder(string source, params string[] tokens)
    {
        int previousIndex = -1;
        string previousToken = "<start>";
        foreach (string token in tokens)
        {
            int index = source.IndexOf(token, previousIndex + 1, StringComparison.Ordinal);
            index.ShouldBeGreaterThan(previousIndex, $"'{token}' must occur after '{previousToken}'.");
            previousIndex = index;
            previousToken = token;
        }
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
