using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanFrameLoopPhase2Tests
{
    [Test]
    public void CanonicalMutationJournal_PreservesContentBindingAndTopologyDomains()
    {
        AdvancedGpuRecordTable<uint> table = new(4u);

        table.TryAdd(10u, out AdvancedGpuHandle handle).ShouldBeTrue();
        table.PublicationDeltas[^1].Change.ShouldBe(
            EAdvancedGpuRecordPublicationChange.Added);
        table.PublicationDeltas[^1].Domain.ShouldBe(
            EAdvancedGpuMutationDomain.LayoutTopology);

        table.TryReplace(
            handle,
            11u,
            EAdvancedGpuMutationDomain.Content).ShouldBeTrue();
        table.PublicationDeltas[^1].Domain.ShouldBe(
            EAdvancedGpuMutationDomain.Content);

        table.TryReplace(
            handle,
            12u,
            EAdvancedGpuMutationDomain.ResourceBinding).ShouldBeTrue();
        table.PublicationDeltas[^1].Domain.ShouldBe(
            EAdvancedGpuMutationDomain.ResourceBinding);

        table.TryTombstone(handle).ShouldBeTrue();
        table.PublicationDeltas[^1].Change.ShouldBe(
            EAdvancedGpuRecordPublicationChange.Tombstoned);
        table.PublicationDeltas[^1].Domain.ShouldBe(
            EAdvancedGpuMutationDomain.LayoutTopology);
    }

    [Test]
    public void SealedSubmission_IsBuiltAtRecordingBoundaryAndSampledAgainstFullPath()
    {
        string recording = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "TrySealRecordedGraphicsSubmissionContract(commandBuffer)");
        string submission = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "TryValidateSealedSubmissionContractNoPins");

        recording.ShouldContain("else\n            TrySealRecordedGraphicsSubmissionContract(commandBuffer);");
        submission.ShouldContain(
            "sampledSealedValid == (imageStateValid && lifetimePinsValid)");
        submission.ShouldContain("DeviceContext.ValidationLayersEnabled");
        submission.ShouldContain("diagnostics.OpenXrStrictSpsFaultInjectionStage !=");
        submission.ShouldContain("(diagnostics.SubmissionSerial & 1023u) == 0u");
    }

    [Test]
    public void SealedSubmission_KeepsQueueGateNativeOnlyAndPublishesPercentiles()
    {
        string submission = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "SubmitToQueueTrackedWithDisposition(");
        string telemetry = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/VulkanTrackedSubmissionTelemetry.cs");
        string profiler = SourceContractWorkspace.ReadFile(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs");

        submission.ShouldContain("VulkanQueueOperationLease.TryEnter(");
        submission.ShouldContain("SubmitNative(queue, ref submitInfo, fence)");
        telemetry.ShouldContain("Stopwatch.Frequency");
        telemetry.ShouldContain("P50Milliseconds");
        telemetry.ShouldContain("P95Milliseconds");
        telemetry.ShouldContain("P99Milliseconds");
        profiler.ShouldContain("sealed_submission = new");
        profiler.ShouldContain("parity_mismatches");
    }

    [Test]
    public void SealedSubmission_UsesOrderedExitOverlayAndPreservesMatchingSampledClosure()
    {
        string submission = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "TryAppendSealedImageExits");
        string contract = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "VulkanSealedImageExitState");
        string refresh = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "CanRestoreSealedSubmissionContractAfterRefreshNoLock");
        string telemetry = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/VulkanTrackedSubmissionTelemetry.cs");
        string profiler = SourceContractWorkspace.ReadFile(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs");

        submission.ShouldContain("TryMatchSealedImageEntriesNoLock");
        submission.ShouldContain("TryAppendSealedImageExits");
        submission.ShouldContain("tracked.PendingQueueOwnershipRelease is not null");
        submission.ShouldContain("RefreshSubmittedDescriptorDependencies_NoLock(");
        refresh.ShouldContain("SealedSubmissionContract? previousContract");
        refresh.ShouldContain("commandLifetime.InvalidateSealedSubmissionContract()");
        refresh.ShouldContain("CanRestoreSealedSubmissionContractAfterRefreshNoLock(");
        refresh.ShouldContain("contract.Resources.Length != commandLifetime.TouchedDependencies.Count");
        refresh.ShouldContain("contract.MatchResourceVectorNoLock(");
        refresh.ShouldContain("commandLifetime.SealedSubmissionContract = previousContract");
        contract.ShouldContain("VulkanSealedImageExitState[] ImageExits");
        telemetry.ShouldContain("MissingContract");
        profiler.ShouldContain("missing_contract");
        telemetry.ShouldContain("GatewayTotal");
        profiler.ShouldContain("gateway_total");
    }

    [Test]
    public void ReverseDependencyFallback_IsCountedAndUnconditionallyResetsHeads()
    {
        string table = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "ClearReverseDependencyHeads()");
        string manifest = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "The intrusive reverse index is keyed by owner and stable slot");

        table.ShouldContain("RecordVulkanResidentTemplateBroadFallback(");
        table.ShouldContain("Array.Clear(_reverseDependencyHeads[owner])");
        table.ShouldContain("!candidate.ReverseLinks[index].IsLinked");
        table.ShouldContain("link.PreviousPrimaryIndex != expectedPreviousPrimaryIndex");
        manifest.ShouldContain("dependencies[left].Handle.Index == dependencies[right].Handle.Index");
    }

    [Test]
    public void RecordedSecondary_CannotBeRerecordedWhileAParentRetainsIt()
    {
        string lifetime = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "is retained by a recorded primary command buffer");

        lifetime.ShouldContain("commandRecord.Pins.HasRecordedReferences");
        lifetime.ShouldContain("TryValidateCommandBufferRecordingAdmissionNoLock");
    }

    [Test]
    public void StableResourceSlots_UseDirectGenerationChecksAndExactPinReceipts()
    {
        string tracker = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "Performs an allocation-free ABA-safe lookup in the flat lifetime");
        string contract = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "EVulkanSealedResourceMatch MatchResourceVectorNoLock");
        string receipt = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "Retains the exact flat resource vector pinned at queue admission");
        string submission = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "has no submission pin receipt to release");

        tracker.ShouldContain("private VulkanResourceLifetimeRecord?[] _resourceSlots");
        tracker.ShouldContain("candidate.Slot != slot");
        tracker.ShouldContain("candidate.Generation != slot.Generation");
        contract.ShouldContain("TryResolvePublishedResourceSlotNoLock(");
        contract.ShouldNotContain("ResourceLifetimes.TryGetValue");
        receipt.ShouldContain("ReadOnlySpan<VulkanResourceSlotHandle> Resources");
        receipt.ShouldContain("internal bool TryCapture(");
        submission.ShouldContain("lifetime.SubmissionPinReceipt");
        submission.ShouldContain("pinReceipt.Resources");
    }

    [Test]
    public void DescriptorClosure_HasIndependentGenerationsAndAtomicInvalidation()
    {
        string authority = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "A waiting submit must observe both the descriptor mutation");
        string manager = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "resourceClosure[closureIndex++] = slot");
        string contract = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "VulkanSealedDescriptorDependency(");
        string submission = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "Tracked submission rejected because a command buffer is pending reset");

        authority.ShouldContain("RequireSubmissionStateGate()");
        authority.ShouldContain("tracker.SyncRoot");
        authority.ShouldContain("state.ResourceClosureGeneration");
        authority.ShouldContain("state.ImagePayloadGeneration");
        authority.ShouldContain("PublishContentUpdate(");
        manager.ShouldContain("VulkanResourceSlotHandle[] resourceClosure");
        manager.ShouldContain("tracker.PublishDescriptorSnapshotNoLock(");
        contract.ShouldContain("ulong ResourceClosureGeneration");
        contract.ShouldContain("ulong ImagePayloadGeneration");
        submission.ShouldContain("ValidateSubmissionCommandBuffersReady(");
        submission.ShouldContain("InvalidatedBuffersPendingReset.ContainsKey(handle)");
    }

    [Test]
    public void WsiHandleReuse_RetainsAndReleasesTheExactDetachedGeneration()
    {
        string tracker = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "Only externally owned Vulkan resources may detach their native identity");
        string runtime = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "Releases the recorded-reference pin from the exact native generation");
        string swapchain = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "ImageLifetimeSlots");
        string target = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "TryBeginReleaseExternalImage");
        string imguiCommands = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "reset detached-window command pool before retirement");
        string imguiSwapchain = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "cannot release swapchain image");

        tracker.ShouldContain("DetachedResourceSlots.Add(pinned, slot)");
        tracker.ShouldContain("ResourceLifetimes.Remove(key)");
        tracker.ShouldContain("ReleaseDetachedResourceSlotNoLock(");
        tracker.ShouldContain("foreach (VulkanResourceSlotHandle slot in DetachedResourceSlots.Values)");
        runtime.ShouldContain("TryResolveResourceGenerationNoLock(");
        runtime.ShouldContain("ReleaseRecordedReference()");
        swapchain.ShouldContain("IsDetachedResourceSlotRetirementReadyNoLock(");
        swapchain.ShouldContain("CompleteDetachedExternalResourceDestruction(");
        target.ShouldContain("RegisterImageViewResource(");
        target.ShouldContain("requireExternal: true");
        imguiCommands.ShouldContain("ResetVulkanCommandPoolTracked(");
        imguiSwapchain.ShouldContain("TryBeginReleaseExternalImage(");
    }
}
