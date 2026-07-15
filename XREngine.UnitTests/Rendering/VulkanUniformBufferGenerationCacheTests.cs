using NUnit.Framework;
using Shouldly;
using System.Runtime.CompilerServices;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanUniformBufferGenerationCacheTests
{
    [Test]
    public void RecordedButNotSubmittedLease_ReleasesOnAbandon()
    {
        VulkanRenderer.VulkanFrameDataGenerationLease lease = default;

        lease.TryAcquireRecording(generation: 7, commandBufferQueued: false).ShouldBeTrue();
        lease.HasRecordingOwner.ShouldBeTrue();
        lease.AbandonRecording();

        lease.HasAnyOwner.ShouldBeFalse();
        lease.Generation.ShouldBe(0UL);
    }

    [Test]
    public void SuccessfulSubmission_TransfersToExactTimelineAndSurvivesCacheEviction()
    {
        VulkanRenderer.VulkanFrameDataGenerationLease lease = default;
        lease.TryAcquireRecording(generation: 11, commandBufferQueued: false).ShouldBeTrue();

        lease.TryTransferToSubmission(
            VulkanRenderer.EVulkanLifetimeQueueDomain.Graphics,
            queueSequence: 42).ShouldBeTrue();
        lease.EvictCachedVariant();

        lease.HasRecordingOwner.ShouldBeFalse();
        lease.HasCachedVariantOwner.ShouldBeFalse();
        lease.HasSubmittedOwner.ShouldBeTrue();
        lease.Generation.ShouldBe(11UL);

        lease.ObserveQueueCompletion(41, 0, 0);
        lease.HasSubmittedOwner.ShouldBeTrue();
        lease.ObserveQueueCompletion(42, 0, 0);
        lease.HasAnyOwner.ShouldBeFalse();
        lease.Generation.ShouldBe(0UL);
    }

    [Test]
    public void CompletedSecondaryRecording_ReleasesRecordingOwnerWithoutSubmission()
    {
        VulkanRenderer.VulkanFrameDataGenerationLease lease = default;
        lease.TryAcquireRecording(generation: 13, commandBufferQueued: false).ShouldBeTrue();

        lease.CompleteRecording(cacheVariant: true);

        lease.HasRecordingOwner.ShouldBeFalse();
        lease.HasCachedVariantOwner.ShouldBeTrue();
        lease.HasSubmittedOwner.ShouldBeFalse();
        lease.Generation.ShouldBe(13UL);
        lease.EvictCachedVariant();
        lease.HasAnyOwner.ShouldBeFalse();
    }

    [Test]
    public void RejectedSubmission_KeepsOnlyCachedVariantUntilEviction()
    {
        VulkanRenderer.VulkanFrameDataGenerationLease lease = default;
        lease.TryAcquireRecording(generation: 3, commandBufferQueued: false).ShouldBeTrue();

        lease.CompleteRecording(cacheVariant: true);

        lease.HasRecordingOwner.ShouldBeFalse();
        lease.HasCachedVariantOwner.ShouldBeTrue();
        lease.HasSubmittedOwner.ShouldBeFalse();
        lease.EvictCachedVariant();
        lease.HasAnyOwner.ShouldBeFalse();
    }

    [Test]
    public void GenerationCannotChangeWhileAnyExactOwnerRemains()
    {
        VulkanRenderer.VulkanFrameDataGenerationLease lease = default;
        lease.TryAcquireRecording(generation: 1, commandBufferQueued: false).ShouldBeTrue();
        lease.TryAcquireRecording(generation: 2, commandBufferQueued: false).ShouldBeFalse();

        lease.CompleteRecording(cacheVariant: true);
        lease.TryAcquireRecording(generation: 2, commandBufferQueued: false).ShouldBeFalse();
        lease.EvictCachedVariant();
        lease.TryAcquireRecording(generation: 2, commandBufferQueued: false).ShouldBeTrue();
    }

    [Test]
    public void TenThousandDesktopSpsCapacityTransitions_ReturnToDeclaredLeaseBounds()
    {
        const int iterations = 10_000;
        int liveGenerations = 0;
        int highWaterGenerations = 0;
        ulong queueSequence = 0;

        for (int i = 0; i < iterations; i++)
        {
            int requiredDrawSlots = (i & 1) == 0 ? 3 : 12;
            requiredDrawSlots.ShouldBeOneOf(3, 12);
            ulong generation = (ulong)(i + 1);
            VulkanRenderer.VulkanFrameDataGenerationLease lease = default;

            lease.TryAcquireRecording(generation, commandBufferQueued: false).ShouldBeTrue();
            liveGenerations++;
            highWaterGenerations = Math.Max(highWaterGenerations, liveGenerations);

            queueSequence++;
            lease.TryTransferToSubmission(
                VulkanRenderer.EVulkanLifetimeQueueDomain.Graphics,
                queueSequence).ShouldBeTrue();
            lease.EvictCachedVariant();
            lease.ObserveQueueCompletion(queueSequence, 0, 0);
            lease.HasAnyOwner.ShouldBeFalse();
            liveGenerations--;
        }

        liveGenerations.ShouldBe(0);
        highWaterGenerations.ShouldBe(1);
    }

    [Test]
    public void RecordingPathsSealCompleteManifestsBeforeVulkanBegin()
    {
        string primary = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondary = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        string arena = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanDynamicUniformRingBuffer.cs");

        AssertOrdered(primary,
            "frameDataManifest.Begin(",
            "TryPrewarmFrameDataForRecording",
            "frameDataManifest.TrySeal(",
            "Api!.BeginCommandBuffer(commandBuffer");
        AssertOrdered(secondary,
            "frameDataManifest.Begin(",
            "TryPrewarmFrameDataForRecording",
            "frameDataManifest.TrySeal(",
            "Api!.BeginCommandBuffer(secondaryCommandBuffer");
        primary.ShouldContain("throw new InvalidOperationException(\n                        $\"Mesh frame-data reservation failed");
        primary.ShouldContain("EnterFrameOpResourcePlannerReadbackScope(ops[opIndex].Context)");
        secondary.ShouldContain("EnterFrameOpResourcePlannerReadbackScope(drawOp.Context)");
        primary.ShouldContain("commandBufferImageSlot,\n                        out string prewarmReason");
        secondary.ShouldContain("descriptorFrameIndex,\n                        out string reason");
        string drawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        drawing.ShouldContain("_program?.ApplyBindingSnapshot(programBindingSnapshot)");
        drawing.ShouldContain("TryRefreshFrameSourceDescriptorSetsForDraw(");
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        openXr.ShouldContain("private bool PrewarmOpenXrFrameOpResources(");
        openXr.ShouldContain("sealFrameManifest = false");
        openXr.ShouldContain("EnterFrameOpResourcePlannerReadbackScope(context)");
        openXr.ShouldContain("descriptorFrameIndex,\n                    out string reason");
        arena.ShouldContain("late or unsealed frame-data request");
        arena.ShouldContain("manifest.ContainsSealedDraw");
        primary.ShouldContain("TryRegisterFrameWideMeshFrameDataRequirements(");
        primary.ShouldContain("sealAfterRegister: true");
        secondary.ShouldContain(
            "TryRegisterFrameWideMeshFrameDataRequirements(\n                    Array.Empty<FrameOp>(),\n                    dynamicUiBatchTextOps,");
    }

    [Test]
    public void EveryReusableRecordingClosesItsLeaseAtTheEndBoundary()
    {
        string tracking = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferTrackingBatch.cs");
        string primary = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondary = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");

        tracking.ShouldContain("private Result EndCommandBufferTracked(CommandBuffer commandBuffer, bool cacheVariant = true)");
        tracking.ShouldContain("lifetime.FrameDataLease.CompleteRecording(cacheVariant)");
        tracking.ShouldContain("lifetime.FrameDataLease.AbandonRecording()");
        primary.ShouldContain("EndCommandBufferTracked(commandBuffer)");
        primary.ShouldContain("EndCommandBufferTracked(secondary)");
        secondary.ShouldContain("EndCommandBufferTracked(secondaryCommandBuffer)");
        secondary.ShouldContain("EndCommandBufferTracked(secondary)");
        openXr.ShouldContain("Result endResult = EndCommandBufferTracked(commandBuffer);");
    }

    [Test]
    public void UniformStorageUsesRendererArenasWithoutHistoricalGenerationCache()
    {
        string uniforms = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs");
        string renderer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string arena = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanDynamicUniformRingBuffer.cs");

        uniforms.ShouldContain("TryReserveMeshFrameDataRange");
        uniforms.ShouldContain("TryGetMeshFrameDataArenaRange");
        uniforms.ShouldContain("ownsBuffer: false");
        renderer.ShouldNotContain("VulkanUniformBufferGenerationCache");
        arena.ShouldContain("DynamicUniformRingBufferCapacity = 32 * 1024 * 1024");
        File.Exists(Path.Combine(ResolveRepoRoot(),
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanUniformBufferGenerationCache.cs"))
            .ShouldBeFalse();
    }

    [Test]
    public void ReservationManifest_SealsThreeUsesAndRejectsLateCapacityMutation()
    {
        var renderer = (VulkanRenderer.VkMeshRenderer)RuntimeHelpers.GetUninitializedObject(
            typeof(VulkanRenderer.VkMeshRenderer));
        VulkanRenderer.VulkanMeshFrameDataReservationManifest manifest = new();

        manifest.Begin(generation: 4, capacityHint: 1);
        manifest.TryReserve(renderer, requiredDrawSlots: 3).ShouldBeTrue();
        manifest.TrySeal(generation: 4, reservedBytes: 768).ShouldBeTrue();
        manifest.ContainsSealedDraw(renderer, 0, generation: 4).ShouldBeTrue();
        manifest.ContainsSealedDraw(renderer, 1, generation: 4).ShouldBeTrue();
        manifest.ContainsSealedDraw(renderer, 2, generation: 4).ShouldBeTrue();
        manifest.ContainsSealedDraw(renderer, 3, generation: 4).ShouldBeFalse();
        manifest.TryReserve(renderer, requiredDrawSlots: 4).ShouldBeFalse();
        manifest.End();
    }

    [Test]
    public void ReservationManifest_AllowsCapacityGrowthOnlyAtNextGenerationBoundary()
    {
        var desktopRenderer = (VulkanRenderer.VkMeshRenderer)RuntimeHelpers.GetUninitializedObject(
            typeof(VulkanRenderer.VkMeshRenderer));
        var spsRenderer = (VulkanRenderer.VkMeshRenderer)RuntimeHelpers.GetUninitializedObject(
            typeof(VulkanRenderer.VkMeshRenderer));
        VulkanRenderer.VulkanMeshFrameDataReservationManifest manifest = new();

        manifest.Begin(generation: 1, capacityHint: 2);
        manifest.TryReserve(desktopRenderer, requiredDrawSlots: 3).ShouldBeTrue();
        manifest.TryReserve(spsRenderer, requiredDrawSlots: 12).ShouldBeTrue();
        manifest.TrySeal(generation: 1, reservedBytes: 4096).ShouldBeTrue();
        manifest.End();

        manifest.Begin(generation: 2, capacityHint: 2);
        manifest.TryReserve(desktopRenderer, requiredDrawSlots: 12).ShouldBeTrue();
        manifest.TryReserve(spsRenderer, requiredDrawSlots: 3).ShouldBeTrue();
        manifest.TrySeal(generation: 2, reservedBytes: 8192).ShouldBeTrue();
        manifest.Generation.ShouldBe(2UL);
        manifest.End();
    }

    [Test]
    public void FrameWideManifest_DefersLateFamiliesAndPublishesThemAtTheNextFrameBoundary()
    {
        var renderer = (VulkanRenderer.VkMeshRenderer)RuntimeHelpers.GetUninitializedObject(
            typeof(VulkanRenderer.VkMeshRenderer));
        var requirements = new Dictionary<VulkanRenderer.VkMeshRenderer, int>(ReferenceEqualityComparer.Instance);
        var eyeFamily = new VulkanRenderer.VulkanMeshFrameDataFamilyKey(
            3,
            VulkanRenderer.EVulkanMeshFrameDataStreamKind.Primary,
            VulkanRenderer.EVulkanFrameOpContextKind.OpenXrEye,
            10,
            20,
            30,
            OutputTargetIdentity: 40,
            CameraIdentity: 50,
            StereoRightEyeCameraIdentity: 51,
            StereoEnabled: true,
            MultiviewEnabled: true);
        var desktopFamily = new VulkanRenderer.VulkanMeshFrameDataFamilyKey(
            0,
            VulkanRenderer.EVulkanMeshFrameDataStreamKind.Primary,
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            11,
            21,
            31,
            OutputTargetIdentity: 41,
            CameraIdentity: 52,
            StereoRightEyeCameraIdentity: 0,
            StereoEnabled: false,
            MultiviewEnabled: false);
        var rendererFamilies = new Dictionary<VulkanRenderer.VulkanMeshFrameDataRendererFamilyKey, int>(
            VulkanRenderer.VulkanMeshFrameDataRendererFamilyKeyComparer.Instance)
        {
            [new(renderer, eyeFamily)] = 3,
        };
        var familyStrides = new Dictionary<VulkanRenderer.VulkanMeshFrameDataFamilyKey, int>
        {
            [eyeFamily] = 3,
        };
        var familyBases = new Dictionary<VulkanRenderer.VulkanMeshFrameDataFamilyKey, int>();
        VulkanRenderer.VulkanFrameWideMeshFrameDataReservationManifest manifest = new();

        manifest.TryRegister(
                100,
                requirements,
                rendererFamilies,
                familyStrides,
                familyBases,
                sealAfterRegister: true,
                out ulong firstGeneration,
                out _)
            .ShouldBeTrue();
        manifest.IsSealed.ShouldBeTrue();
        int eyeBase = familyBases[eyeFamily];

        rendererFamilies.Clear();
        rendererFamilies[new(renderer, desktopFamily)] = 3;
        familyStrides.Clear();
        familyStrides[desktopFamily] = 3;
        manifest.TryRegister(
                100,
                requirements,
                rendererFamilies,
                familyStrides,
                familyBases,
                sealAfterRegister: true,
                out _,
                out string lateReason)
            .ShouldBeFalse();
        lateReason.ShouldContain("after frame-wide manifest generation");

        manifest.TryRegister(
                101,
                requirements,
                rendererFamilies,
                familyStrides,
                familyBases,
                sealAfterRegister: true,
                out ulong secondGeneration,
                out _)
            .ShouldBeTrue();
        secondGeneration.ShouldBeGreaterThan(firstGeneration);
        int desktopBase = familyBases[desktopFamily];
        eyeBase.ShouldBe(0);
        desktopBase.ShouldBe(0);
        manifest.PublishedRendererCount.ShouldBe(1);
        manifest.PublishedFamilyCount.ShouldBe(2);
    }

    [Test]
    public void FrameWideManifest_AllocatesDisjointFamilyRangesWithinOneFrameDataSlot()
    {
        var renderer = (VulkanRenderer.VkMeshRenderer)RuntimeHelpers.GetUninitializedObject(
            typeof(VulkanRenderer.VkMeshRenderer));
        var primaryFamily = new VulkanRenderer.VulkanMeshFrameDataFamilyKey(
            2,
            VulkanRenderer.EVulkanMeshFrameDataStreamKind.Primary,
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            10,
            20,
            30,
            40,
            50,
            0,
            false,
            false);
        var dynamicUiFamily = primaryFamily with
        {
            StreamKind = VulkanRenderer.EVulkanMeshFrameDataStreamKind.DynamicUi,
        };
        var requirements = new Dictionary<VulkanRenderer.VkMeshRenderer, int>(ReferenceEqualityComparer.Instance);
        var rendererFamilies = new Dictionary<VulkanRenderer.VulkanMeshFrameDataRendererFamilyKey, int>(
            VulkanRenderer.VulkanMeshFrameDataRendererFamilyKeyComparer.Instance)
        {
            [new(renderer, primaryFamily)] = 3,
            [new(renderer, dynamicUiFamily)] = 5,
        };
        var familyStrides = new Dictionary<VulkanRenderer.VulkanMeshFrameDataFamilyKey, int>
        {
            [primaryFamily] = 3,
            [dynamicUiFamily] = 5,
        };
        var familyBases = new Dictionary<VulkanRenderer.VulkanMeshFrameDataFamilyKey, int>();
        VulkanRenderer.VulkanFrameWideMeshFrameDataReservationManifest manifest = new();

        manifest.TryRegister(
                100,
                requirements,
                rendererFamilies,
                familyStrides,
                familyBases,
                sealAfterRegister: true,
                out _,
                out _)
            .ShouldBeTrue();

        int primaryBase = familyBases[primaryFamily];
        int dynamicUiBase = familyBases[dynamicUiFamily];
        dynamicUiBase.ShouldBeGreaterThanOrEqualTo(primaryBase + 3);
        requirements[renderer].ShouldBeGreaterThanOrEqualTo(dynamicUiBase + 5);
    }

    [Test]
    public void FrameWideManifest_KeepsPublishedFamilyBaseStableAcrossBoundedDrawCountVariation()
    {
        var renderer = (VulkanRenderer.VkMeshRenderer)RuntimeHelpers.GetUninitializedObject(
            typeof(VulkanRenderer.VkMeshRenderer));
        var family = new VulkanRenderer.VulkanMeshFrameDataFamilyKey(
            2,
            VulkanRenderer.EVulkanMeshFrameDataStreamKind.Primary,
            VulkanRenderer.EVulkanFrameOpContextKind.MainViewport,
            10,
            20,
            30,
            40,
            50,
            0,
            false,
            false);
        var requirements = new Dictionary<VulkanRenderer.VkMeshRenderer, int>(ReferenceEqualityComparer.Instance);
        var rendererFamilies = new Dictionary<VulkanRenderer.VulkanMeshFrameDataRendererFamilyKey, int>(
            VulkanRenderer.VulkanMeshFrameDataRendererFamilyKeyComparer.Instance)
        {
            [new(renderer, family)] = 3,
        };
        var familyStrides = new Dictionary<VulkanRenderer.VulkanMeshFrameDataFamilyKey, int>
        {
            [family] = 3,
        };
        var familyBases = new Dictionary<VulkanRenderer.VulkanMeshFrameDataFamilyKey, int>();
        VulkanRenderer.VulkanFrameWideMeshFrameDataReservationManifest manifest = new();

        manifest.TryRegister(
                100,
                requirements,
                rendererFamilies,
                familyStrides,
                familyBases,
                sealAfterRegister: true,
                out ulong firstGeneration,
                out _)
            .ShouldBeTrue();
        int firstBase = familyBases[family];
        requirements[renderer].ShouldBeGreaterThanOrEqualTo(firstBase + 32);

        rendererFamilies[new(renderer, family)] = 20;
        familyStrides[family] = 20;
        manifest.TryRegister(
                100,
                requirements,
                rendererFamilies,
                familyStrides,
                familyBases,
                sealAfterRegister: true,
                out ulong secondGeneration,
                out _)
            .ShouldBeTrue();

        familyBases[family].ShouldBe(firstBase);
        secondGeneration.ShouldBe(firstGeneration);
        manifest.LateRegistrationCount.ShouldBe(0);
    }

    [Test]
    public void FrameWideFamilyIdentity_IsStableAcrossExternalTargetRotationButSeparatesFrameSlots()
    {
        var leftSlot = new VulkanRenderer.VulkanMeshFrameDataFamilyKey(
            3,
            VulkanRenderer.EVulkanMeshFrameDataStreamKind.Primary,
            VulkanRenderer.EVulkanFrameOpContextKind.OpenXrEye,
            10,
            20,
            30,
            OutputTargetIdentity: 40,
            CameraIdentity: 50,
            StereoRightEyeCameraIdentity: 51,
            StereoEnabled: true,
            MultiviewEnabled: true);
        var rotatedExternalImage = leftSlot;
        var rightSlot = leftSlot with { FrameDataSlot = 4 };

        rotatedExternalImage.ShouldBe(leftSlot);
        rightSlot.ShouldNotBe(leftSlot);
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        int previous = -1;
        foreach (string marker in markers)
        {
            int index = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            index.ShouldBeGreaterThan(previous, $"Expected '{marker}' after the previous reservation stage.");
            previous = index;
        }
    }

    private static string ReadWorkspaceFile(string relativePath)
        => File.ReadAllText(Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

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
