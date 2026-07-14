using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using System.Text.RegularExpressions;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase4Tests
{
    [Test]
    public void RetirementTicketMerge_PreservesStrongestCompletionPoint()
    {
        VulkanRenderer.VulkanRetirementTicket first = new(
            GraphicsSequence: 4,
            TransferSequence: 8,
            OtherSequence: 2,
            EnqueuedTimestamp: 200,
            ResourceGeneration: 7,
            ExternalOwnershipPending: false,
            PinSet: VulkanRenderer.VulkanRetirementPinSet.Single(
                new VulkanRenderer.VulkanResourceLifetimeKey(ObjectType.Buffer, 0xA),
                7));
        VulkanRenderer.VulkanRetirementTicket second = new(
            GraphicsSequence: 9,
            TransferSequence: 3,
            OtherSequence: 5,
            EnqueuedTimestamp: 100,
            ResourceGeneration: 11,
            ExternalOwnershipPending: true,
            PinSet: VulkanRenderer.VulkanRetirementPinSet.Single(
                new VulkanRenderer.VulkanResourceLifetimeKey(ObjectType.ImageView, 0xB),
                11));

        VulkanRenderer.VulkanRetirementTicket merged = first.Merge(second);

        merged.GraphicsSequence.ShouldBe(9UL);
        merged.TransferSequence.ShouldBe(8UL);
        merged.OtherSequence.ShouldBe(5UL);
        merged.EnqueuedTimestamp.ShouldBe(100L);
        merged.ResourceGeneration.ShouldBe(11UL);
        merged.ExternalOwnershipPending.ShouldBeTrue();
        merged.PinSet.ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Test]
    public void LifetimeState_SeparatesCpuRecordedSubmittedExternalAndRetirementOwnership()
    {
        VulkanRenderer.EVulkanResourceLifetimeState values =
            VulkanRenderer.EVulkanResourceLifetimeState.CpuOwned |
            VulkanRenderer.EVulkanResourceLifetimeState.Recorded |
            VulkanRenderer.EVulkanResourceLifetimeState.Submitted |
            VulkanRenderer.EVulkanResourceLifetimeState.Completed |
            VulkanRenderer.EVulkanResourceLifetimeState.External |
            VulkanRenderer.EVulkanResourceLifetimeState.PendingRetirement |
            VulkanRenderer.EVulkanResourceLifetimeState.Destroyed |
            VulkanRenderer.EVulkanResourceLifetimeState.Queued;

        Enum.GetValues<VulkanRenderer.EVulkanResourceLifetimeState>()
            .Where(static value => value != VulkanRenderer.EVulkanResourceLifetimeState.None)
            .ShouldAllBe(value => values.HasFlag(value));
    }

    [Test]
    public void LifetimeRejectionDiagnostic_NamesEveryRequiredRaceIdentity()
    {
        VulkanRenderer.VulkanRetirementTicket ticket = new(
            GraphicsSequence: 13,
            TransferSequence: 2,
            OtherSequence: 0,
            EnqueuedTimestamp: 100,
            ResourceGeneration: 41,
            ExternalOwnershipPending: false);
        string text = VulkanRenderer.DescribeVulkanLifetimeRejection(
            new VulkanRenderer.VulkanResourceLifetimeKey(ObjectType.Buffer, 0xCAFE),
            "StrictStereo.UniformBuffer",
            oldGeneration: 40,
            newGeneration: 41,
            output: "OpenXR.TrueSinglePassStereo",
            commandBufferHandle: 0xBEEF,
            in ticket,
            state: VulkanRenderer.EVulkanResourceLifetimeState.PendingRetirement,
            reason: "recorded dependency is pending retirement");
        text.ShouldContain("resource=Buffer:0xCAFE");
        text.ShouldContain("owner=StrictStereo.UniformBuffer");
        text.ShouldContain("oldGeneration=40");
        text.ShouldContain("newGeneration=41");
        text.ShouldContain("output=OpenXR.TrueSinglePassStereo");
        text.ShouldContain("commandBuffer=0xBEEF");
        text.ShouldContain("retirementTicket=gfx:13/transfer:2/other:0/generation:41");
        text.ShouldContain("state=PendingRetirement");
        text.ShouldContain("reason=recorded dependency is pending retirement");
    }

    [Test]
    public void QueueGateway_ValidatesDependenciesBeforeDispatchAndRecordsSuccessfulUse()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string method = SliceBetween(
            synchronization,
            "private Result SubmitToQueueTracked(",
            "internal Result WaitForQueueIdleTracked(");

        int validation = method.IndexOf("ValidateVulkanSubmissionResourceLifetimes", StringComparison.Ordinal);
        int dispatch = method.IndexOf("Api!.QueueSubmit", StringComparison.Ordinal);
        int successfulUse = method.IndexOf("RecordSuccessfulVulkanSubmissionLifetime", StringComparison.Ordinal);

        validation.ShouldBeGreaterThanOrEqualTo(0);
        dispatch.ShouldBeGreaterThan(validation);
        successfulUse.ShouldBeGreaterThan(dispatch);
        method.ShouldContain("submit-rejected-resource-lifetime");
    }

    [Test]
    public void CompletionTracking_CoversTimelineFenceQueueAndDeviceIdlePaths()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string syncObjects = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SyncObjects.cs");
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string initialization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string transfer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanRenderer.TextureUploadTransfer.cs");

        lifetime.ShouldContain("NotifyVulkanTimelineCompleted");
        lifetime.ShouldContain("NotifyVulkanFenceCompleted");
        lifetime.ShouldContain("NotifyVulkanQueueIdle");
        lifetime.ShouldContain("NotifyVulkanDeviceIdle");
        syncObjects.ShouldContain("NotifyVulkanTimelineCompleted(semaphore, currentValue)");
        synchronization.ShouldContain("NotifyVulkanQueueIdle(queue)");
        initialization.ShouldContain("NotifyVulkanDeviceIdle()");
        openXr.ShouldContain("NotifyVulkanFenceCompleted(fence)");
        transfer.ShouldContain("NotifyVulkanFenceCompleted(submitted.Fence)");
    }

    [Test]
    public void RetirementQueues_CoverEveryPhase4ObjectFamily()
    {
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");

        retirement.ShouldContain("RetiredImageResourceEntry");
        retirement.ShouldContain("RetiredFramebuffer");
        retirement.ShouldContain("RetiredBuffer");
        retirement.ShouldContain("RetiredBufferView");
        retirement.ShouldContain("RetiredDescriptorSet");
        retirement.ShouldContain("RetiredDescriptorPool");
        retirement.ShouldContain("RetiredCommandBuffer");
        retirement.ShouldContain("RetiredPipeline");
        retirement.ShouldContain("RetiredQueryPool");
        retirement.ShouldContain("IsVulkanRetirementReady(candidate.Ticket)");
    }

    [Test]
    public void VulkanDestruction_ForRuntimeFamilies_IsCentralizedInRetirementQueue()
    {
        AssertRawVulkanCallOnlyIn(
            "DestroyFramebuffer(device",
            "Frame/VulkanRenderer.ResourceRetirement.cs");
        AssertRawVulkanCallOnlyIn(
            "FreeDescriptorSets(device",
            "Frame/VulkanRenderer.ResourceRetirement.cs");
        AssertRawVulkanCallOnlyIn(
            "ResetDescriptorPool(device",
            "Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        AssertRawVulkanCallOnlyIn(
            "DestroyQueryPool(device",
            "Frame/VulkanRenderer.ResourceRetirement.cs");
        AssertRawVulkanCallOnlyIn(
            "DestroyBufferView(device",
            "Frame/VulkanRenderer.ResourceRetirement.cs");
    }

    [Test]
    public void DescriptorLifetime_PreventsIllegalMutationAndTracksPoolChildren()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string descriptorSets = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSets.cs");
        string commandState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");

        lifetime.ShouldContain("Cannot update in-flight Vulkan descriptor set");
        lifetime.ShouldContain("CaptureVulkanDescriptorPoolRetirementTicket");
        lifetime.ShouldContain("CanMutateVulkanDescriptorPool");
        descriptorSets.ShouldContain("ValidateAndRecordVulkanDescriptorWrites");
        commandState.ShouldContain("ResetVulkanDescriptorPoolTracked");
    }

    [Test]
    public void DeviceLossTeardown_ForcesDestructionWithoutCompletingTimelines()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");
        string initialization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");

        lifetime.ShouldContain("NotifyVulkanResourceLifetimeDeviceLost");
        lifetime.ShouldContain("_vulkanForcedResourceDestructionCount");
        retirement.ShouldContain("Force-destroying retired resources after device loss without waiting");
        initialization.ShouldContain("BeginForcedVulkanRetirementDrain");
        initialization.ShouldContain("EndForcedVulkanRetirementDrain");
    }

    [Test]
    public void DescriptorSetLayout_DuplicateReleaseIsSkipped()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSetLayoutLifetime.cs");
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLayoutCache.cs");

        lifetime.ShouldContain("Skipping stale descriptor-set-layout destroy");
        lifetime.ShouldContain("_liveDescriptorSetLayoutHandles.TryRemove");
        cache.ShouldContain("TryBeginDestroyDescriptorSetLayout(layout, \"DescriptorLayoutCache.UncachedRelease\")");
        cache.ShouldNotContain("if (!_descriptorSetLayoutsByHandle.TryGetValue(layout.Handle, out CachedDescriptorSetLayout? cached))\n            {\n                Api!.DestroyDescriptorSetLayout");
    }

    [Test]
    public void CommandRecording_TracksSecondaryCopyBlitAndDescriptorDependencies()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string commandState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");

        lifetime.ShouldContain("CmdExecuteCommandsTracked");
        lifetime.ShouldContain("CmdCopyBufferTracked");
        lifetime.ShouldContain("CmdCopyBufferToImageTracked");
        lifetime.ShouldContain("CmdCopyImageToBufferTracked");
        lifetime.ShouldContain("CmdCopyImageTracked");
        lifetime.ShouldContain("CmdBlitImageTracked");
        lifetime.ShouldContain("CommandBuffer.SecondaryExecution");
        lifetime.ShouldContain("RegisterVulkanFramebuffer");
        lifetime.ShouldContain("Framebuffer.Attachment");
        commandState.ShouldContain("TrackVulkanDescriptorSetBinding");
        commandState.ShouldContain("TrackVulkanCommandBufferResource");
    }

    [Test]
    public void PipelineCreation_RegistersEverySuccessfulNativeHandleGeneration()
    {
        string[] files =
        [
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgramPipeline.cs",
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs",
        ];

        foreach (string file in files)
        {
            string source = ReadWorkspaceFile(file);
            int nativeCreates = Regex.Matches(
                source,
                @"Api!?\.Create(?:Graphics|Compute)Pipelines\(",
                RegexOptions.CultureInvariant).Count;
            int registrations = Regex.Matches(
                source,
                @"RegisterVulkanPipeline\(",
                RegexOptions.CultureInvariant).Count;

            registrations.ShouldBe(
                nativeCreates,
                $"Every successful pipeline creation site in {file} must register its native handle generation.");
        }
    }

    [Test]
    public void ParentChildAndDescriptorOwnership_AreRetainedUntilSafeDestruction()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");

        lifetime.ShouldContain("HasUndestroyedVulkanBufferViewReference");
        lifetime.ShouldContain("HasUndestroyedVulkanImageDependency");
        lifetime.ShouldContain("PropagateVulkanDescriptorSetSubmission_NoLock");
        lifetime.ShouldContain("UpdateVulkanResourceCompletionState_NoLock");
        retirement.ShouldContain("RemoveRetiredDescriptorSetsForPool_NoLock");
        retirement.ShouldContain("_retiredPipelineHandlesAll");
        retirement.ShouldContain("_retiredImageViewHandlesAll");
        AssertRawVulkanCallOnlyIn(
            "AllocateCommandBuffers(device",
            "Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        AssertRawVulkanCallOnlyIn(
            "CreateImage(device",
            "Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        AssertRawVulkanCallOnlyIn(
            "DestroyImage(device",
            "Frame/VulkanRenderer.ResourceLifetimeTracking.cs",
            "Frame/VulkanRenderer.ResourceRetirement.cs",
            "Features/Upscaling/VulkanUpscaleBridgeSharedImage.cs");
    }

    [Test]
    public void MipmapBarriers_UseTrackedResourceRecordingGateway()
    {
        string texture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string mipmapMethod = SliceBetween(
            texture,
            "protected void GenerateMipmapsWithBlit()",
            "private ImageBlit CreateMipBlit");

        mipmapMethod.ShouldContain("Renderer.CmdPipelineBarrierTracked");
        mipmapMethod.ShouldNotContain("Api.CmdPipelineBarrier(");
    }

    private static void AssertRawVulkanCallOnlyIn(string token, params string[] allowedRelativePaths)
    {
        string vulkanRoot = Path.Combine(
            ResolveRepoRoot(),
            "XREngine.Runtime.Rendering",
            "Rendering",
            "API",
            "Rendering",
            "Vulkan");
        string[] offenders = Directory
            .EnumerateFiles(vulkanRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("VulkanUpscaleBridgeSidecar.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(vulkanRoot, path).Replace('\\', '/'))
            .Where(path => !allowedRelativePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        offenders.ShouldBeEmpty(
            $"Raw Vulkan call '{token}' must stay in {string.Join(", ", allowedRelativePaths)}.");
    }

    private static string SliceBetween(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected start token '{startToken}'.");
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Expected end token '{endToken}'.");
        return source[start..end];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{relativePath}'.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
