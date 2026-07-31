using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCommandChainDataModelTests
{
    [Test]
    public void PreparedFrameRecording_FreezesOrderedFrameSlotStorage()
    {
        VulkanPreparedFrameRecording recording = new();
        recording.Begin(frameSlot: 2, generation: 41);
        FrameOpContext primaryContext =
            CreateFrameOpContext(outputTargetIdentity: 10);
        FrameOp[] primaryOperations =
        [
            new ClearOp(
                PassIndex: 5,
                Target: null,
                ClearColor: true,
                ClearDepth: false,
                ClearStencil: false,
                Color: default,
                Depth: 1f,
                Stencil: 0,
                Rect: default,
                primaryContext),
            new MemoryBarrierOp(
                PassIndex: 5,
                EMemoryBarrierMask.All,
                primaryContext),
        ];
        VulkanPrimaryCommandPlan primaryPlan = new();
        primaryPlan.Build(primaryOperations);
        recording.AddPrimaryPlan(primaryPlan);

        recording.AddMeshDraw(
            new VkPreparedMeshDraw(
                SourceOpIndex: 0,
                Viewport: default,
                Scissor: default,
                IndexedViewports: null,
                IndexedScissors: null,
                ViewportScissorCount: 1,
                Context: CreateFrameOpContext(outputTargetIdentity: 10),
                UniformSlot: 3)).ShouldBe(0);
        recording.AddMeshDraw(
            new VkPreparedMeshDraw(
                SourceOpIndex: 1,
                Viewport: default,
                Scissor: default,
                IndexedViewports: null,
                IndexedScissors: null,
                ViewportScissorCount: 1,
                Context: CreateFrameOpContext(outputTargetIdentity: 10),
                UniformSlot: 4)).ShouldBe(1);
        recording.IsFrozen.ShouldBeFalse();
        recording.ContainsMeshDrawRangeForOwnerValidation(0, 2).ShouldBeTrue();
        recording.ContainsMeshDrawRangeForOwnerValidation(1, 2).ShouldBeFalse();
        CommandChainKey chainKey = new(
            FrameSlot: 2,
            ViewKey: default,
            PassIndex: 5,
            TargetIdentity: 10,
            DynamicOverlay: false,
            ChainOrdinal: 0);
        CommandChain chain = new(chainKey)
        {
            SourceStartIndex = 0,
            SourceCount = 2,
        };
        chain.RecordedArtifact.AssignNativeBuffer(
            new CommandBuffer { Handle = 101 },
            new CommandPool { Handle = 202 },
            ownsPool: false);
        VulkanRecordedCommandArtifactReference artifact =
            chain.RecordedArtifact.CreateReference();
        recording.AddCommandChain(
            new VulkanPreparedCommandChain(
                chainKey,
                SourceStartIndex: 0,
                SourceCount: 2,
                PreparedDrawStartIndex: 0,
                Inheritance: default,
                DependencySignature: default,
                WritableArtifact: artifact,
                WorkerEligibility:
                    EVulkanCommandChainWorkerEligibility.Eligible))
            .ShouldBe(0);
        recording.Freeze();

        recording.IsFrozen.ShouldBeTrue();
        recording.FrameSlot.ShouldBe(2);
        recording.Generation.ShouldBe(41UL);
        recording.HasPrimaryPlan.ShouldBeTrue();
        recording.PrimaryPlanNodeCount.ShouldBe(3);
        recording.PrimaryPlanIdentity.ShouldBe(primaryPlan.Identity);
        recording.MeshDrawCount.ShouldBe(2);
        recording.CommandChainCount.ShouldBe(1);
        recording.GetPrimaryPlanNode(0).Kind.ShouldBe(
            EVulkanPrimaryPlanNodeKind.Clear);
        recording.GetPrimaryPlanNode(1).Kind.ShouldBe(
            EVulkanPrimaryPlanNodeKind.MemoryBarrier);
        recording.GetPrimaryPlanNode(2).Kind.ShouldBe(
            EVulkanPrimaryPlanNodeKind.EndRendering);
        recording.GetPrimaryPlanNode(2).Operation.ShouldBeNull();
        VkPreparedMeshDraw first = recording.GetMeshDraw(0);
        VkPreparedMeshDraw second = recording.GetMeshDraw(1);
        VulkanPreparedCommandChain preparedChain =
            recording.GetCommandChain(0);
        first.SourceOpIndex.ShouldBe(0);
        first.UniformSlot.ShouldBe(3);
        second.SourceOpIndex.ShouldBe(1);
        second.UniformSlot.ShouldBe(4);
        preparedChain.Matches(chain).ShouldBeTrue();
        preparedChain.WorkerEligibility.ShouldBe(
            EVulkanCommandChainWorkerEligibility.Eligible);
        Should.Throw<InvalidOperationException>(
            () => recording.AddMeshDraw(default));
        Should.Throw<InvalidOperationException>(
            () => recording.AddPrimaryPlan(primaryPlan));
        Should.Throw<InvalidOperationException>(
            () => recording.AddCommandChain(default));
    }

    [Test]
    public void PreparedFrameRecording_ResetRemovesPublishedFrameOwnership()
    {
        VulkanPreparedFrameRecording recording = new();
        recording.Begin(frameSlot: 1, generation: 3);
        recording.AddMeshDraw(default);
        recording.Freeze();

        recording.Reset();

        recording.IsFrozen.ShouldBeFalse();
        recording.FrameSlot.ShouldBe(-1);
        recording.Generation.ShouldBe(0UL);
        recording.HasPrimaryPlan.ShouldBeFalse();
        recording.PrimaryPlanNodeCount.ShouldBe(0);
        recording.PrimaryPlanIdentity.ShouldBe(0UL);
        recording.MeshDrawCount.ShouldBe(0);
        recording.CommandChainCount.ShouldBe(0);
        Should.Throw<InvalidOperationException>(
            () => _ = recording.GetMeshDraw(0));
        Should.Throw<InvalidOperationException>(
            () => _ = recording.GetPrimaryPlanNode(0));
        Should.Throw<InvalidOperationException>(
            () => _ = recording.GetCommandChain(0));
    }

    [Test]
    public void PreparedFrameRecording_ReusesWarmStorageWithoutAllocating()
    {
        VulkanPreparedFrameRecording recording = new();
        const int drawCount = 32;

        recording.Begin(frameSlot: 0, generation: 1);
        for (int drawIndex = 0; drawIndex < drawCount; drawIndex++)
            recording.AddMeshDraw(default);
        recording.Freeze();
        recording.Reset();

        int visitedDraws = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong generation = 2; generation < 1_002; generation++)
        {
            recording.Begin(frameSlot: 0, generation);
            for (int drawIndex = 0; drawIndex < drawCount; drawIndex++)
                recording.AddMeshDraw(default);
            recording.Freeze();
            for (int drawIndex = 0; drawIndex < drawCount; drawIndex++)
            {
                _ = recording.GetMeshDraw(drawIndex);
                visitedDraws++;
            }
            recording.Reset();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        visitedDraws.ShouldBe(32_000);
        allocated.ShouldBe(0);
    }

    [TestCase(0u, 1u)]
    [TestCase(1u, 1u)]
    [TestCase(3u, 2u)]
    [TestCase(10u, 2u)]
    [TestCase(0x80000000u, 1u)]
    public void OcclusionQueryViewSlots_MatchActiveMultiviewViewCount(uint viewMask, uint expectedSlots)
    {
        var resolver = typeof(VkRenderQuery).GetMethod(
            "ResolveOcclusionQueryViewSlotCount",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        resolver.ShouldNotBeNull();
        resolver.Invoke(null, [viewMask]).ShouldBe(expectedSlots);
    }

    [Test]
    public void RenderViewKey_EqualityAndHash_AreStable()
    {
        RenderViewKey a = new(
            PipelineIdentity: 10,
            ViewportIdentity: 20,
            ViewIndex: 1,
            Kind: RenderViewKind.VREye,
            LightIdentity: 30,
            CascadeIndex: 2);
        RenderViewKey b = new(
            PipelineIdentity: 10,
            ViewportIdentity: 20,
            ViewIndex: 1,
            Kind: RenderViewKind.VREye,
            LightIdentity: 30,
            CascadeIndex: 2);
        RenderViewKey differentKind = a with { Kind = RenderViewKind.Shadow };

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        differentKind.ShouldNotBe(a);
    }

    [Test]
    public void BuildRenderViewKey_UsesExplicitStereoEyeIndices()
    {
        XRCamera leftCamera = new(new Transform(), new XROVRCameraParameters(true, 0.1f, 1000.0f));
        XRCamera rightCamera = new(new Transform(), new XROVRCameraParameters(false, 0.1f, 1000.0f));
        MeshDrawOp leftOp = CreateMeshDrawOp(default(PendingMeshDraw) with { Camera = leftCamera });
        MeshDrawOp rightOp = CreateMeshDrawOp(default(PendingMeshDraw) with { Camera = rightCamera });

        RenderViewKey leftKey = VulkanRenderer.BuildRenderViewKey(leftOp, dynamicOverlay: false);
        RenderViewKey rightKey = VulkanRenderer.BuildRenderViewKey(rightOp, dynamicOverlay: false);

        leftKey.Kind.ShouldBe(RenderViewKind.VREye);
        leftKey.ViewIndex.ShouldBe(VulkanRenderer.CommandChainLeftEyeViewIndex);
        rightKey.Kind.ShouldBe(RenderViewKind.VREye);
        rightKey.ViewIndex.ShouldBe(VulkanRenderer.CommandChainRightEyeViewIndex);
        leftKey.ShouldNotBe(rightKey);
    }

    [Test]
    public void BuildRenderViewKey_SinglePassStereoUsesMultiviewSentinel()
    {
        MeshDrawOp op = CreateMeshDrawOp(default(PendingMeshDraw) with { IsStereoPass = true });

        RenderViewKey key = VulkanRenderer.BuildRenderViewKey(op, dynamicOverlay: false);

        key.Kind.ShouldBe(RenderViewKind.VREye);
        key.ViewIndex.ShouldBe(VulkanRenderer.CommandChainStereoMultiviewViewIndex);
    }

    [Test]
    public void BuildRenderViewKey_SinglePassStereoPrefersMultiviewSentinelOverEyeCamera()
    {
        XRCamera leftCamera = new(new Transform(), new XROVRCameraParameters(true, 0.1f, 1000.0f));
        MeshDrawOp op = CreateMeshDrawOp(default(PendingMeshDraw) with
        {
            Camera = leftCamera,
            IsStereoPass = true
        });

        RenderViewKey key = VulkanRenderer.BuildRenderViewKey(op, dynamicOverlay: false);

        key.Kind.ShouldBe(RenderViewKind.VREye);
        key.ViewIndex.ShouldBe(VulkanRenderer.CommandChainStereoMultiviewViewIndex);
    }

    [Test]
    public void OpenXrEyeRenderTargetContext_SeparatesLeftAndRightTargetIdentity()
    {
        Extent2D extent = new(2160, 2160);
        VulkanRenderer.OpenXrEyeRenderTargetContext left = new(
            OpenXrViewIndex: 0u,
            OpenXrImageIndex: 4u,
            Image: new Image(0x1001UL),
            ImageView: new ImageView(0x1002UL),
            ImageFormat: Format.B8G8R8A8Srgb,
            Extent: extent,
            DepthImage: new Image(0x1003UL),
            DepthMemory: new DeviceMemory(0x1004UL),
            DepthView: new ImageView(0x1005UL),
            DepthFormat: Format.D32Sfloat,
            DepthAspect: ImageAspectFlags.DepthBit,
            ExternalTargetRegion: new BoundingRectangle(0, 0, 2160, 2160),
            CommandChainImageKey: 1_000_010u,
            FrameDataSlotIndex: 3u,
            ResourcePlannerStateIndex: 0,
            FoveationResourceKey: 0xF001UL,
            FoveationAttachmentKind: EVrFoveationAttachmentKind.VulkanFragmentShadingRate,
            FoveationAttachmentOwnedByResourcePlanner: true);
        VulkanRenderer.OpenXrEyeRenderTargetContext right = new(
            OpenXrViewIndex: 1u,
            OpenXrImageIndex: 4u,
            Image: new Image(0x2001UL),
            ImageView: new ImageView(0x2002UL),
            ImageFormat: Format.B8G8R8A8Srgb,
            Extent: extent,
            DepthImage: new Image(0x2003UL),
            DepthMemory: new DeviceMemory(0x2004UL),
            DepthView: new ImageView(0x2005UL),
            DepthFormat: Format.D32Sfloat,
            DepthAspect: ImageAspectFlags.DepthBit,
            ExternalTargetRegion: new BoundingRectangle(0, 0, 2160, 2160),
            CommandChainImageKey: 1_000_020u,
            FrameDataSlotIndex: 4u,
            ResourcePlannerStateIndex: 1,
            FoveationResourceKey: 0xF002UL,
            FoveationAttachmentKind: EVrFoveationAttachmentKind.VulkanFragmentDensityMap,
            FoveationAttachmentOwnedByResourcePlanner: true);

        left.IsValid.ShouldBeTrue();
        right.IsValid.ShouldBeTrue();
        left.Image.ShouldNotBe(right.Image);
        left.ImageView.ShouldNotBe(right.ImageView);
        left.DepthImage.ShouldNotBe(right.DepthImage);
        left.DepthView.ShouldNotBe(right.DepthView);
        left.CommandChainImageKey.ShouldNotBe(right.CommandChainImageKey);
        left.FrameDataSlotIndex.ShouldNotBe(right.FrameDataSlotIndex);
        left.ResourcePlannerStateIndex.ShouldNotBe(right.ResourcePlannerStateIndex);
        left.FoveationResourceKey.ShouldNotBe(right.FoveationResourceKey);
        left.FoveationAttachmentKind.ShouldNotBe(right.FoveationAttachmentKind);
        VulkanOpenXrViewResourcePlannerContextKey.FromTarget(left)
            .ShouldNotBe(VulkanOpenXrViewResourcePlannerContextKey.FromTarget(right));

        ulong leftKey = VulkanRenderer.BuildOpenXrPrimaryCommandBufferCacheKey(left.CommandChainImageKey, left);
        ulong rightKey = VulkanRenderer.BuildOpenXrPrimaryCommandBufferCacheKey(right.CommandChainImageKey, right);
        leftKey.ShouldNotBe(rightKey);
    }

    [Test]
    public void OpenXrPlannerIdentity_IsStableAcrossAcquiredImageRotation_WhileCommandVariantsRemainImageSpecific()
    {
        VulkanRenderer.OpenXrEyeRenderTargetContext firstImage = CreateTarget(
            openXrImageIndex: 0u,
            imageHandle: 0x1001UL,
            commandChainImageKey: 1_000_010u,
            frameDataSlotIndex: 3u);
        VulkanRenderer.OpenXrEyeRenderTargetContext secondImage = CreateTarget(
            openXrImageIndex: 1u,
            imageHandle: 0x2001UL,
            commandChainImageKey: 1_000_011u,
            frameDataSlotIndex: 4u);

        VulkanRenderer.BuildOpenXrExternalSwapchainPlannerTargetIdentity(firstImage.OpenXrViewIndex)
            .ShouldBe(VulkanRenderer.BuildOpenXrExternalSwapchainPlannerTargetIdentity(secondImage.OpenXrViewIndex));
        VulkanOpenXrViewResourcePlannerContextKey.FromTarget(firstImage)
            .ShouldBe(VulkanOpenXrViewResourcePlannerContextKey.FromTarget(secondImage));

        VulkanRenderer.BuildOpenXrPrimaryCommandBufferCacheKey(firstImage.CommandChainImageKey, firstImage)
            .ShouldNotBe(VulkanRenderer.BuildOpenXrPrimaryCommandBufferCacheKey(secondImage.CommandChainImageKey, secondImage));

        static VulkanRenderer.OpenXrEyeRenderTargetContext CreateTarget(
            uint openXrImageIndex,
            ulong imageHandle,
            uint commandChainImageKey,
            uint frameDataSlotIndex)
            => new(
                OpenXrViewIndex: 0u,
                OpenXrImageIndex: openXrImageIndex,
                Image: new Image(imageHandle),
                ImageView: new ImageView(imageHandle + 1u),
                ImageFormat: Format.B8G8R8A8Srgb,
                Extent: new Extent2D(2160, 2160),
                DepthImage: new Image(0x3001UL),
                DepthMemory: new DeviceMemory(0x3002UL),
                DepthView: new ImageView(0x3003UL),
                DepthFormat: Format.D32Sfloat,
                DepthAspect: ImageAspectFlags.DepthBit,
                ExternalTargetRegion: new BoundingRectangle(0, 0, 2160, 2160),
                CommandChainImageKey: commandChainImageKey,
                FrameDataSlotIndex: frameDataSlotIndex,
                ResourcePlannerStateIndex: 0,
                FoveationResourceKey: 0UL,
                FoveationAttachmentKind: EVrFoveationAttachmentKind.None,
                FoveationAttachmentOwnedByResourcePlanner: false);
    }

    [Test]
    public void NullSwapchainFrameOps_UseExternalOutputTargetIdentity()
    {
        FrameOpContext leftContext = CreateFrameOpContext(
            outputTargetIdentity: 101,
            outputTargetName: "<left-eye>");
        FrameOpContext rightContext = CreateFrameOpContext(
            outputTargetIdentity: 202,
            outputTargetName: "<right-eye>");
        ClearOp left = CreateClearOp(0) with { Context = leftContext };
        ClearOp right = CreateClearOp(0) with { Context = rightContext };

        VulkanRenderer.ResolveCommandChainTargetIdentity(left).ShouldBe(101);
        VulkanRenderer.ResolveCommandChainTargetIdentity(right).ShouldBe(202);
        VulkanRenderer.ResolveCommandChainTargetName(left).ShouldBe("<left-eye>");
        VulkanRenderer.ResolveCommandChainTargetName(right).ShouldBe("<right-eye>");
        left.Context.SchedulingIdentity.ShouldNotBe(right.Context.SchedulingIdentity);
    }

    [Test]
    public void OpenXrExternalSwapchainTargets_DoNotForceCommandChains()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        source.ShouldContain("!IsRenderingExternalSwapchainTarget &&");
        source.ShouldNotContain("IsRenderingExternalSwapchainTarget ||");
    }

    [Test]
    public void OpenXrEyePrimaryRecording_PassesTargetContextIntoCommandBufferRecording()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        commandBufferSource.ShouldContain("OpenXrEyeRenderTargetContext? openXrTargetContext = null");
        commandBufferSource.ShouldContain("IsRenderingExternalSwapchainTarget && openXrTargetContext is null");
        commandBufferSource.ShouldContain(": ResolveSwapchainRecordingTarget(imageIndex, openXrTargetContext);");
        commandBufferSource.ShouldContain("CreateSwapchainDynamicRenderingFormatSignature(swapchainTarget.ImageFormat, swapchainTarget.DepthFormat)");
        commandBufferSource.ShouldContain("openXrTarget.Image");
        commandBufferSource.ShouldContain("openXrTarget.ImageView");
        commandBufferSource.ShouldContain("openXrTarget.DepthImage");
        commandBufferSource.ShouldContain("openXrTarget.DepthView");
        openXrSource.ShouldContain("openXrTargetContext: targetContext");
        openXrSource.ShouldNotContain("ApplyOpenXrEyeRenderTargetContext");
    }

    [Test]
    public void SwapchainBlits_UseActiveCommandBufferRecordingTarget()
    {
        string blitSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        blitSource.ShouldContain("ResolveSwapchainBlitImage(swapchainImageIndex, wantColor, wantDepth, wantStencil, in swapchainTarget)");
        blitSource.ShouldContain("recordingTarget.Image");
        blitSource.ShouldContain("recordingTarget.DepthImage");
        commandBufferSource.ShouldContain("RecordBlitOp(commandBuffer, imageIndex, blit, in swapchainTarget);");
        commandBufferSource.ShouldContain("TryResolveBlitImage(op.OutFbo, imageIndex, EReadBufferMode.ColorAttachment0, wantColor: true, wantDepth: false, wantStencil: false, out var colorDestination, isSource: false, in swapchainTarget)");
    }

    [Test]
    public void OpenXrExternalSwapchainBlits_AreNormalizedAndValidatedAsFullEyeWriters()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");

        openXrSource.ShouldContain("NormalizeOpenXrExternalSwapchainFrameOps(ops, request.Extent)");
        openXrSource.ShouldContain("NormalizeOpenXrExternalSwapchainFrameOps(ops, extent)");
        openXrSource.ShouldContain("case BlitOp { OutFbo: null } blitOp:");
        openXrSource.ShouldContain("ExpectedDestination=(0,0");
        openXrSource.ShouldContain("IsFullOpenXrBlitDestination");
    }

    [Test]
    public void OpenXrResourcePlannerState_IsKeyedByViewTargetAndFoveationContext()
    {
        string openXrSource = ReadOpenXrVulkanRendererSources();
        string openXrBackendSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");
        string contextKeySource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrViewResourcePlannerContextKey.cs");

        openXrSource.ShouldContain("private Dictionary<VulkanOpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> OpenXrResourcePlannerStates");
        openXrSource.ShouldContain("_openXrBackend.GetResourcePlannerStates<VulkanOpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState>()");
        openXrBackendSource.ShouldContain("internal readonly object ResourcePlannerStatesLock = new();");
        openXrSource.ShouldContain("EnterOpenXrResourcePlannerThreadScope(VulkanOpenXrViewResourcePlannerContextKey.FromTarget(in targetContext))");
        openXrSource.ShouldContain("EVulkanOpenXrResourcePlannerPurpose purpose");
        openXrSource.ShouldContain("CreateLegacyOpenXrResourcePlannerContextKey(stateIndex, purpose)");
        openXrSource.ShouldContain("purpose={key.Purpose}");
        contextKeySource.ShouldContain("target.FoveationResourceKey");
        contextKeySource.ShouldContain("target.FoveationAttachmentKind");
        contextKeySource.ShouldContain("target.FoveationAttachmentOwnedByResourcePlanner");
        openXrSource.ShouldContain("DescribeOpenXrResourcePlannerContextKey");
        openXrSource.ShouldContain("OpenXrResourcePlannerStates.TryGetValue(_contextKey");
        openXrSource.ShouldContain("OpenXrResourcePlannerStates[_contextKey] = state;");
        openXrSource.ShouldNotContain("EnterOpenXrResourcePlannerScope");
        openXrSource.ShouldNotContain("private sealed class OpenXrResourcePlannerScope");
        openXrSource.ShouldNotContain("renderer.RestoreResourcePlannerRuntimeState(openXrState)");
        openXrSource.ShouldNotContain("private readonly ResourcePlannerRuntimeState[] _openXrResourcePlannerStates");
        openXrSource.ShouldNotContain("_hasOpenXrResourcePlannerStates");
    }

    [Test]
    public void FrameOpResourcePlannerSwitchingState_IsScopedWithOpenXrThreadPlannerContext()
    {
        string stateTrackingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.StateTracking.cs");
        string openXrSource = ReadOpenXrVulkanRendererSources();
        string resourcePlannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string commandChainSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        stateTrackingSource.ShouldContain("private sealed class FrameOpResourcePlannerSwitchingState");
        stateTrackingSource.ShouldContain("public VulkanRenderer? FrameOpResourcePlannerSwitchingStateOwner;");
        stateTrackingSource.ShouldContain("public FrameOpResourcePlannerSwitchingState? FrameOpResourcePlannerSwitchingState;");
        stateTrackingSource.ShouldContain("private FrameOpResourcePlannerSwitchingState ActiveFrameOpResourcePlannerSwitchingState");
        stateTrackingSource.ShouldContain("EnterThreadFrameOpResourcePlannerSwitchingStateScope");
        stateTrackingSource.ShouldContain("CommandThreadContext.FrameOpResourcePlannerSwitchingStateOwner");
        openXrSource.ShouldContain("private readonly ThreadFrameOpResourcePlannerSwitchingStateScope _frameOpThreadScope;");
        openXrSource.ShouldContain("openXrState.FrameOpResourcePlannerSwitchingState ??= new FrameOpResourcePlannerSwitchingState();");
        openXrSource.ShouldContain("state.FrameOpResourcePlannerSwitchingState = _frameOpThreadScope.CaptureCurrent(_renderer);");
        resourcePlannerSource.ShouldContain("FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;");
        commandChainSource.ShouldContain("FrameOpResourcePlannerSwitchingState frameOpSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;");
        commandBufferSource.ShouldContain("VulkanFrameOpPlannerStateKey packetPlannerKey =");
        commandBufferSource.ShouldContain("packetRequest.PlannerKey;");
        commandBufferSource.ShouldContain("using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(packetContext);");
        commandBufferSource.ShouldNotContain("if (ActiveFrameOpResourcePlannerSwitchingState.SwitchingActive)\n                return false;");
        stateTrackingSource.ShouldNotContain("private bool _frameOpResourcePlannerSwitchingActive;");
        stateTrackingSource.ShouldNotContain("private bool _frameOpResourcePlannerRecordingScopeActive;");
        stateTrackingSource.ShouldNotContain("private bool _hasActiveFrameOpResourcePlannerStateKey;");
        stateTrackingSource.ShouldNotContain("private VulkanFrameOpPlannerStateKey _activeFrameOpResourcePlannerStateKey;");
    }

    [Test]
    public void OpenXrExternalTargetAndUploadBlockState_AreThreadScopedForEyeWorkers()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");
        string externalScopeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXrExternalSwapchainRenderScope.cs");
        string uploadBlockScopeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.SynchronousResourceUploadBlockScope.cs");

        openXrSource.ShouldNotContain("[ThreadStatic]");
        openXrBackendSource.ShouldContain("ThreadLocal<VulkanOpenXrThreadExecutionState>");
        openXrSource.ShouldContain("_openXrBackend.CurrentThreadExecutionState");
        openXrSource.ShouldContain("public override bool IsRenderingExternalSwapchainTarget => IsThreadOpenXrExternalSwapchainTarget;");
        openXrSource.ShouldContain("private bool IsThreadOpenXrExternalSwapchainTarget");
        openXrSource.ShouldContain("executionState.FrameContext.TargetRegion");
        openXrSource.ShouldContain("using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(");
        openXrSource.ShouldNotContain("_openXrExternalSwapchainRenderDepth++;");
        openXrSource.ShouldNotContain("_openXrExternalSwapchainRenderDepth--;");

        externalScopeSource.ShouldContain("_threadState = renderer._openXrBackend.CurrentThreadExecutionState;");
        externalScopeSource.ShouldContain("_threadState.FrameContext = frameContext;");
        externalScopeSource.ShouldContain("Interlocked.Increment(ref renderer._openXrBackend.ExternalSwapchainRenderDepth);");
        externalScopeSource.ShouldContain("Interlocked.Decrement(ref _renderer._openXrBackend.ExternalSwapchainRenderDepth)");

        openXrSource.ShouldContain("private bool IsThreadSynchronousResourceUploadBlocked");
        openXrSource.ShouldContain("=> !IsThreadSynchronousResourceUploadBlocked &&");
        uploadBlockScopeSource.ShouldContain("_threadState = renderer._openXrBackend.CurrentThreadExecutionState;");
        uploadBlockScopeSource.ShouldContain("_threadState.SynchronousUploadBlockDepth = _previousThreadDepth + 1;");
        uploadBlockScopeSource.ShouldContain("Interlocked.Increment(ref renderer._openXrBackend.SynchronousResourceUploadBlockDepth);");
        uploadBlockScopeSource.ShouldContain("Interlocked.Decrement(ref _renderer._openXrBackend.SynchronousResourceUploadBlockDepth)");
    }

    [Test]
    public void AbstractRendererCurrent_IsThreadScopedForOpenXrEyeWorkers()
    {
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs");
        string workerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.EyeRecordWorkers.cs");

        rendererSource.ShouldContain("[ThreadStatic]\n        private static AbstractRenderer? _threadCurrent;");
        rendererSource.ShouldContain("[ThreadStatic]\n        private static bool _hasThreadCurrentOverride;");
        rendererSource.ShouldContain("private static AbstractRenderer? _globalCurrent;");
        rendererSource.ShouldContain("get => _hasThreadCurrentOverride ? _threadCurrent : _globalCurrent;");
        rendererSource.ShouldContain("internal static IDisposable PushThreadCurrent(AbstractRenderer? renderer)");
        rendererSource.ShouldContain("private readonly struct ThreadCurrentScope : IDisposable");
        workerSource.ShouldContain("using IDisposable currentRendererScope = AbstractRenderer.PushThreadCurrent(this);");
        workerSource.ShouldContain("TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in prepared, out recorded)");
    }

    [Test]
    public void OpenXrEyePrimaryCommandBuffers_UseEyeOwnedCommandPools()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferCacheVariant.cs");

        stateSource.ShouldContain("public CommandPool PrimaryCommandPool { get; }");
        stateSource.ShouldContain("public CommandPool DynamicUiSecondaryCommandPool { get; }");
        openXrBackendSource.ShouldContain("internal readonly CommandPool[] EyeCommandPools = new CommandPool[EyeResourcePlannerStateCount];");
        openXrSource.ShouldContain("GetOrCreateOpenXrEyeCommandPool(targetContext.OpenXrViewIndex)");
        openXrSource.ShouldContain("OpenXR eye primary command buffer variant eye=");
        openXrSource.ShouldContain("DestroyOpenXrEyeCommandPools();");
        openXrSource.ShouldContain("variant.PrimaryCommandPool.Handle != 0");
        openXrSource.ShouldContain("FreeVulkanCommandBufferTracked(ownerPool, ref primary, \"OpenXR.PrimaryCache\");");
    }

    [Test]
    public void OpenXrPrimaryCommandBufferCache_AccessIsLockedWithoutLockingWholeRecordPath()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");

        openXrBackendSource.ShouldContain("internal readonly object PrimaryCommandBufferVariantsLock = new();");
        openXrSource.ShouldContain("lock (_openXrBackend.PrimaryCommandBufferVariantsLock)");
        openXrSource.ShouldContain("MarkOpenXrPrimaryCommandBufferVariantsDirty()");
        openXrSource.ShouldContain("GetOrCreateOpenXrPrimaryCommandBufferVariant(");
        openXrSource.ShouldContain("TryReuseOpenXrPrimaryCommandBuffer(");
        openXrSource.ShouldContain("TryReuseOpenXrMirrorPrimaryCommandBuffer(");
        openXrSource.ShouldContain("DestroyOpenXrPrimaryCommandBufferCache()");
        openXrSource.ShouldContain("RecordOpenXrPrimaryCommandBuffer(");
        openXrSource.ShouldNotContain("lock (_openXrPrimaryCommandBufferVariantsLock)\r\n        {\r\n            ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey");
    }

    [Test]
    public void PrimaryCommandBufferRecording_UsesThreadLocalScratchForParallelEyeSafety()
    {
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecordingScratch.cs");
        string recordingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondarySource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");

        stateSource.ShouldContain("ThreadLocal<CommandBufferRecordingScratch> _commandBufferRecordingScratch");
        stateSource.ShouldContain("private sealed class CommandBufferRecordingScratch");
        recordingSource.ShouldContain("CommandBufferRecordingScratch recordingScratch = _commandBufferRecordingScratch.Value!;");
        recordingSource.ShouldContain("recordingScratch.ExecutedCommandChainSecondaryHandles");
        recordingSource.ShouldContain("recordingScratch.SwapchainWritesByPipeline");
        recordingSource.ShouldContain("recordingScratch.FboLayoutTracking");
        secondarySource.ShouldContain("HashSet<nint> executedCommandChainSecondaryHandles");
        secondarySource.ShouldNotContain("_executedCommandChainSecondaryHandlesScratch");
        stateSource.ShouldNotContain("_swapchainWritesByPipelineScratch");
        stateSource.ShouldNotContain("_recordMeshDrawSlotsByRendererScratch");
        stateSource.ShouldNotContain("_fboLayoutTrackingScratch");
    }

    [Test]
    public void OpenXrSubmitDiagnostics_ReportFrameSlotsUploadsAndRetirementFlushes()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");

        openXrSource.ShouldContain("uint FrameDataSlotIndex");
        openXrSource.ShouldContain("CountOpenXrEyeRecordedTextureUploads()");
        openXrSource.ShouldContain("queueSubmitMs={1:F3} fenceWaitMs={2:F3}");
        openXrSource.ShouldContain("eye batch submit completed leftFrameSlot={0} rightFrameSlot={1} publishedUploads={2} retiredFlushSlots={3}");
        openXrSource.ShouldContain("eye batch submit did not complete leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}");
        openXrSource.ShouldContain("eye batch submit failed leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}");
        openXrSource.ShouldContain("MAX_FRAMES_IN_FLIGHT");
    }

    [Test]
    public void OpenXrEyeUploadPublicationBuffers_AreEyeScopedBeforeMergedSubmit()
    {
        VulkanRenderer.ResolveOpenXrEyeUploadPublicationBufferIndex(0u).ShouldBe(0);
        VulkanRenderer.ResolveOpenXrEyeUploadPublicationBufferIndex(1u).ShouldBe(1);
        VulkanRenderer.ResolveOpenXrEyeUploadPublicationBufferIndex(99u).ShouldBe(1);

        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");
        openXrBackendSource.ShouldContain("internal readonly List<VulkanImportedTexturePendingUpload>[] EyeRecordedTextureUploadsForSubmit = [new(), new()];");
        openXrSource.ShouldContain("MoveRecordedTextureUploadsForSubmitTo(eyeUploads);");
        openXrSource.ShouldContain("PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit(\"OpenXR eye batch\")");
        openXrSource.ShouldContain("CancelOpenXrEyeRecordedTextureUploads(\"OpenXR eye batch command buffer submit failed\")");
    }

    [Test]
    public void OpenXrEyeUploadPublicationBuffers_HandleRecordSubmitAndDeviceLostFailures()
    {
        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");

        openXrSource.ShouldContain("ClearOpenXrEyeRecordedTextureUploads();");
        openXrSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out firstPrepared)");
        openXrSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out secondPrepared)");
        openXrSource.ShouldContain("hasFirst = TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in firstPrepared, out firstRecorded);");
        openXrSource.ShouldContain("if (!hasFirst)");
        openXrSource.ShouldContain("hasSecond = TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in secondPrepared, out secondRecorded);");
        openXrSource.ShouldContain("if (!hasSecond)");
        openXrSource.ShouldContain("PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit(\"OpenXR eye batch\")");
        openXrSource.ShouldContain("CancelOpenXrEyeRecordedTextureUploads(\"OpenXR eye batch command buffers did not complete\")");
        openXrSource.ShouldContain("if (!submitted && !commandBuffersCompleted && !IsDeviceLost)");
        openXrSource.ShouldContain("FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);");
        openXrSource.ShouldContain("FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);");
    }

    [Test]
    public void AllocatorBackedTextures_CacheViewsPerPhysicalImageContext()
    {
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string viewLifetimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Textures/VulkanRenderer.ImageViewLifetime.cs");
        string resourceLifetimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        textureSource.ShouldContain("private readonly List<PhysicalImageViewCacheEntry> _physicalImageViewCache = [];");
        textureSource.ShouldContain("SaveCurrentPhysicalImageViewCache();");
        textureSource.ShouldContain("if (_physicalGroup.IsAllocated)");
        textureSource.ShouldContain("else\n\t\t\t\t\tDestroyCurrentViews(removeActiveCacheEntry: true);");
        textureSource.ShouldContain("if (!TryRestorePhysicalImageViewCache(_physicalGroup, current))");
        textureSource.ShouldContain("private sealed class PhysicalImageViewCacheEntry");
        textureSource.ShouldContain("DestroyCurrentViews(removeActiveCacheEntry: true);");
        viewLifetimeSource.ShouldContain("private void RetireImageViewsForBackingImage(ulong imageHandle)");
        viewLifetimeSource.ShouldContain("foreach (KeyValuePair<ulong, ImageViewCreateInfo> pair in _descriptorHeapImageViewCreateInfos)");
        viewLifetimeSource.ShouldContain("pair.Value.Image.Handle != imageHandle");
        resourceLifetimeSource.ShouldContain("RetireImageViewsForBackingImage(handle);");
    }

    [Test]
    public void DescriptorPrewarmReuseAndIndirectPreparationUseTheFrameOpPlannerContext()
    {
        string recordingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondarySource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");

        recordingSource.ShouldContain(
            "private bool TryRefreshReusableCommandBufferFrameData(\n            uint imageIndex,\n            ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests,");
        recordingSource.ShouldContain("VulkanFrameOpPlannerStateKey packetPlannerKey =");
        recordingSource.ShouldContain("packetRequest.PlannerKey;");
        recordingSource.ShouldContain("using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(packetContext);");
        recordingSource.ShouldNotContain("using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(op.Context);");
        recordingSource.ShouldContain("case IndirectDrawOp indirectDrawOp:");
        recordingSource.ShouldContain("renderer = indirectDrawOp.MeshRenderer;");
        recordingSource.ShouldContain("request.MeshRenderer!");

        recordingSource.ShouldContain("using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(indirectOp.Context);");
        secondarySource.ShouldContain("using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(runOp.Context);");
    }

    [Test]
    public void FrameBuffers_CacheAttachmentVariantsForSerialViewContextSwitches()
    {
        string frameBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        frameBufferSource.ShouldContain("private readonly List<CachedFrameBufferState> _cachedFrameBufferStates = [];");
        frameBufferSource.ShouldContain("if (TryActivateCachedFrameBufferState(attachments, fbWidth, fbHeight))");
        frameBufferSource.ShouldContain("CachedFrameBufferState state = CreateFrameBufferState(attachments, fbWidth, fbHeight);");
        frameBufferSource.ShouldContain("private sealed class CachedFrameBufferState");
        frameBufferSource.ShouldNotContain("Rebuilding framebuffer");
    }

    [Test]
    public void DescriptorImageInfoAndAllocationKeys_AreScopedToActivePhysicalPlannerContext()
    {
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string descriptorKeySource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/Structs/VkMeshRenderer.DescriptorAllocationKey.cs");
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");

        textureSource.ShouldContain("public DescriptorImageInfo CreateImageInfo()");
        textureSource.ShouldContain("RefreshPhysicalGroupImageIfStale();");
        textureSource.ShouldContain("ImageView = _view");
        textureSource.ShouldContain("Sampler = _sampler");
        textureSource.ShouldContain("if (!TryRestorePhysicalImageViewCache(_physicalGroup, current))");
        descriptorKeySource.ShouldContain("ulong LayoutFingerprint");
        descriptorKeySource.ShouldContain("uint ProgramBindingId");
        descriptorKeySource.ShouldContain("ulong BindingIdentityFingerprint");
        descriptorKeySource.ShouldContain("ulong ImmutableResourceFingerprint");
        descriptorKeySource.ShouldContain("int DescriptorFrameSlotCount");
        descriptorSource.ShouldContain("hash.Add(info.ImageView.Handle);");
        descriptorSource.ShouldContain("hash.Add(info.Sampler.Handle);");
        descriptorSource.ShouldContain("hash.Add((int)info.ImageLayout);");
        descriptorSource.ShouldContain("AppendComponent(builder, \"resourceAllocator\", unchecked((ulong)Renderer.ResourceAllocatorIdentity));");
        descriptorSource.ShouldContain("DescriptorAllocationKey allocationKey = new(");
        descriptorSource.ShouldContain("_program.BindingId,");
        descriptorSource.ShouldContain("DescriptorAllocationMatchesProgram(cachedAllocation)");
        descriptorSource.ShouldContain("DescriptorAllocationMatchesProgram(activeAllocation)");
        descriptorSource.ShouldContain("ReferenceEquals(allocation.Program, _program)");
        descriptorSource.ShouldContain("viewFamilyIdentity,");
        descriptorSource.ShouldContain("immutableResourceFingerprint);");
        descriptorSource.ShouldContain("EnsureDescriptorSlotReady(cachedAllocation, material, bindings, frameIndex, drawUniformSlot, resourceFingerprint, bindingSnapshot)");
    }

    [Test]
    public void MeshImageDescriptors_UseCoherentSourceSnapshotsForParallelViewRecording()
    {
        string descriptorInterfaceSource = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "internal readonly record struct VkImageDescriptorSnapshot",
            "interface IVkImageDescriptorSource");
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string textureViewSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTextureView.cs");
        string descriptorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");

        descriptorInterfaceSource.ShouldContain("internal readonly record struct VkImageDescriptorSnapshot");
        descriptorInterfaceSource.ShouldContain("bool TryGetDescriptorSnapshot(");
        descriptorInterfaceSource.ShouldContain("ImageAspectFlags? requestedAspectMask");
        textureSource.ShouldContain("private readonly object _imageStateLock = new();");
        textureSource.ShouldContain("TryBuildDescriptorSnapshotNoLock(requestedViewType, requestedAspectMask, out snapshot)");
        textureViewSource.ShouldContain("bool IVkImageDescriptorSource.TryGetDescriptorSnapshot(");

        int start = descriptorSource.IndexOf("private bool TryResolveImage(", StringComparison.Ordinal);
        int end = descriptorSource.IndexOf("private bool TryUsePlaceholderDescriptor(", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        end.ShouldBeGreaterThan(start);
        string tryResolveImageBody = descriptorSource[start..end];

        int samplerStart = descriptorSource.IndexOf("private bool TryResolveDescriptorSampler(", StringComparison.Ordinal);
        int samplerEnd = descriptorSource.IndexOf("private void LogPostProcessDescriptor(", StringComparison.Ordinal);
        samplerStart.ShouldBeGreaterThanOrEqualTo(0);
        samplerEnd.ShouldBeGreaterThan(samplerStart);
        string tryResolveDescriptorSamplerBody = descriptorSource[samplerStart..samplerEnd];

        tryResolveImageBody.ShouldContain("source.TryGetDescriptorSnapshot(");
        tryResolveImageBody.ShouldContain("descriptorSnapshot.Usage");
        tryResolveImageBody.ShouldContain("descriptorSnapshot.Format");
        tryResolveImageBody.ShouldContain("descriptorSnapshot.Aspect");
        tryResolveImageBody.ShouldContain("descriptorSnapshot.View");
        tryResolveImageBody.ShouldContain("TryResolveDescriptorSampler(binding, descriptorType, in descriptorSnapshot");
        tryResolveImageBody.ShouldContain("Renderer.ResolveDescriptorImageLayout(source, in descriptorSnapshot, descriptorType)");
        tryResolveImageBody.ShouldNotContain("source.TryEnsureDescriptorReadyForUse");
        tryResolveImageBody.ShouldNotContain("source.DescriptorUsage");
        tryResolveImageBody.ShouldNotContain("source.DescriptorFormat");
        tryResolveImageBody.ShouldNotContain("source.DescriptorAspect");
        tryResolveImageBody.ShouldNotContain("source.DescriptorView");
        tryResolveImageBody.ShouldNotContain("source.DescriptorSampler");
        tryResolveImageBody.ShouldNotContain("source.GetDepthOnlyDescriptorView()");
        tryResolveImageBody.ShouldNotContain("source.GetStencilOnlyDescriptorView()");
        tryResolveDescriptorSamplerBody.ShouldContain("in VkImageDescriptorSnapshot snapshot");
        tryResolveDescriptorSamplerBody.ShouldContain("snapshot.Sampler");
    }

    [Test]
    public void ImageBackedTextureAttachmentViewCache_IsImageStateLockedForParallelEyeRecording()
    {
        string textureSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");

        textureSource.ShouldContain("private readonly object _imageStateLock = new();");
        textureSource.ShouldContain("private bool RefreshPhysicalGroupImageIfStaleNoLock()");
        textureSource.ShouldContain("lock (_imageStateLock)");
        textureSource.ShouldContain("return RefreshPhysicalGroupImageIfStaleNoLock();");

        int attachmentViewStart = textureSource.IndexOf("public ImageView GetAttachmentView(", StringComparison.Ordinal);
        int attachmentExtentStart = textureSource.IndexOf("bool IVkFrameBufferAttachmentSource.TryGetAttachmentExtent(", StringComparison.Ordinal);
        int descriptorInfoStart = textureSource.IndexOf("public DescriptorImageInfo CreateImageInfo()", StringComparison.Ordinal);
        int descriptorInfoEnd = textureSource.IndexOf("#endregion", descriptorInfoStart, StringComparison.Ordinal);
        attachmentViewStart.ShouldBeGreaterThanOrEqualTo(0);
        attachmentExtentStart.ShouldBeGreaterThan(attachmentViewStart);
        descriptorInfoStart.ShouldBeGreaterThan(attachmentExtentStart);
        descriptorInfoEnd.ShouldBeGreaterThan(descriptorInfoStart);

        string attachmentViewBody = textureSource[attachmentViewStart..attachmentExtentStart];
        string descriptorInfoBody = textureSource[descriptorInfoStart..descriptorInfoEnd];

        attachmentViewBody.ShouldContain("lock (_imageStateLock)");
        attachmentViewBody.ShouldContain("RefreshPhysicalGroupImageIfStaleNoLock();");
        attachmentViewBody.ShouldContain("_attachmentViews.TryGetValue");
        attachmentViewBody.ShouldContain("_attachmentViews[key] = cached;");
        descriptorInfoBody.ShouldContain("lock (_imageStateLock)");
        descriptorInfoBody.ShouldContain("RefreshPhysicalGroupImageIfStaleNoLock();");
    }

    [Test]
    public void CommandAndFramebufferCacheKeys_IncludeViewPlannerAndFoveationIdentity()
    {
        CommandChainKey left = new(
            FrameSlot: 3,
            ViewKey: new RenderViewKey(10, 20, VulkanRenderer.CommandChainLeftEyeViewIndex, RenderViewKind.VREye, 0, -1),
            PassIndex: 4,
            TargetIdentity: 5,
            DynamicOverlay: false,
            ChainOrdinal: 0);
        CommandChainKey right = left with
        {
            ViewKey = left.ViewKey with { ViewIndex = VulkanRenderer.CommandChainRightEyeViewIndex },
            FrameSlot = 4,
        };

        left.ShouldNotBe(right);

        string openXrSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string openXrBackendSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrBackend.cs");
        string frameBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");

        openXrSource.ShouldContain("hash.Add(targetContext.Image.Handle);");
        openXrSource.ShouldContain("hash.Add(targetContext.ImageView.Handle);");
        openXrSource.ShouldContain("hash.Add(targetContext.DepthImage.Handle);");
        openXrSource.ShouldContain("hash.Add(targetContext.DepthView.Handle);");
        openXrSource.ShouldContain("hash.Add(targetContext.OpenXrViewIndex);");
        openXrSource.ShouldContain("hash.Add(targetContext.OpenXrImageIndex);");
        openXrSource.ShouldContain("hash.Add(targetContext.FrameDataSlotIndex);");
        openXrSource.ShouldContain("hash.Add(targetContext.ResourcePlannerStateIndex);");
        openXrSource.ShouldContain("hash.Add(targetContext.FoveationResourceKey);");
        openXrSource.ShouldContain("hash.Add((int)targetContext.FoveationAttachmentKind);");
        openXrSource.ShouldContain("hash.Add(targetContext.FoveationAttachmentOwnedByResourcePlanner);");

        frameBufferSource.ShouldContain("if (AttachmentViews[i].Handle != attachments[i].View.Handle)");
        frameBufferSource.ShouldContain("if (!AttachmentSignature[i].Equals(attachments[i].Signature))");
        frameBufferSource.ShouldContain("if (!AttachmentTargets[i].Equals(attachments[i].TargetInfo))");
    }

    [Test]
    public void OpenXrParallelEyePreparation_UsesDistinctImmutablePlannerContextsBeforeWorkerRecord()
    {
        string openXrSource = ReadOpenXrVulkanRendererSources();
        string workerSource = openXrSource;

        workerSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out OpenXrPreparedEyeCommandBufferInput preparedFirstEye)");
        workerSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out OpenXrPreparedEyeCommandBufferInput preparedSecondEye)");
        workerSource.ShouldContain("DispatchOpenXrEyeRecordWorkers(preparedFirstEye, preparedSecondEye)");
        workerSource.ShouldContain("private OpenXrPreparedEyeCommandBufferInput _prepared;");
        workerSource.ShouldNotContain("Task.Run");

        openXrSource.ShouldContain("private readonly record struct OpenXrPreparedEyeCommandBufferInput");
        openXrSource.ShouldContain("OpenXrEyeRenderTargetContext TargetContext");
        openXrSource.ShouldContain("FrameOp[] Ops");
        openXrSource.ShouldContain("FrameOpContext PlannerContext");
        openXrSource.ShouldContain("plannerContext,");
        openXrSource.ShouldContain("CommandChainSchedule? CommandChainSchedule");
        openXrSource.ShouldContain("EnterOpenXrResourcePlannerThreadScope(VulkanOpenXrViewResourcePlannerContextKey.FromTarget(in targetContext))");
        openXrSource.ShouldContain("ResetDynamicUniformRingBuffer(recordImageIndex);");
        openXrSource.ShouldNotContain("renderer.RestoreResourcePlannerRuntimeState(openXrState)");
    }

    [Test]
    public void OpenXrEyeBatch_PreparesBothContextsBeforeRecordingEitherCommandBuffer()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        int methodStart = source.IndexOf("internal bool TryRenderOpenXrEyeSwapchains(", StringComparison.Ordinal);
        int methodEnd = source.IndexOf("internal bool TryRenderOpenXrEyeSwapchainsSinglePassStereo(", methodStart, StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);

        string method = source[methodStart..methodEnd];
        int prepareFirst = method.IndexOf("TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out firstPrepared)", StringComparison.Ordinal);
        int prepareSecond = method.IndexOf("TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out secondPrepared)", StringComparison.Ordinal);
        int recordFirst = method.IndexOf("TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in firstPrepared, out firstRecorded)", StringComparison.Ordinal);
        int recordSecond = method.IndexOf("TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in secondPrepared, out secondRecorded)", StringComparison.Ordinal);

        prepareFirst.ShouldBeGreaterThanOrEqualTo(0);
        prepareSecond.ShouldBeGreaterThan(prepareFirst);
        recordFirst.ShouldBeGreaterThan(prepareSecond);
        recordSecond.ShouldBeGreaterThan(recordFirst);
    }

    [Test]
    public void OpenXrVulkanViewRenderModes_DispatchToDistinctRendererPaths()
    {
        string openXrApiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs");
        string rendererSource = ReadOpenXrVulkanRendererSources();
        string workerSource = rendererSource;

        openXrApiSource.ShouldContain("TryRenderVulkanEyeSinglePassStereoToSwapchains");
        openXrApiSource.ShouldContain("TryRenderVulkanEyeParallelCommandBufferRecordingToSwapchains");
        openXrApiSource.ShouldContain("EVrViewRenderMode.SinglePassStereo => renderer.TryRenderOpenXrEyeSwapchainsSinglePassStereo");
        openXrApiSource.ShouldContain("EVrViewRenderMode.ParallelCommandBufferRecording => renderer.TryRenderOpenXrEyeSwapchainsParallelCommandBufferRecording");
        rendererSource.ShouldContain("internal bool TryRenderOpenXrEyeSwapchainsSinglePassStereo");
        rendererSource.ShouldContain("internal bool TryRenderOpenXrEyeSwapchainsParallelCommandBufferRecording");
        rendererSource.ShouldContain("TryRenderOpenXrEyeSwapchainsWithParallelEyeWorkers(leftEye, rightEye)");
        workerSource.ShouldContain("private sealed class OpenXrEyeRecordWorkerScheduler");
        workerSource.ShouldContain("private sealed class OpenXrEyeRecordWorker");
    }

    [Test]
    public void VulkanUpscaleSidecarQueueSubmits_UseItsPerDeviceTerminalGateway()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.cs");

        source.ShouldContain("private Result SubmitToGraphicsQueue(ref SubmitInfo submitInfo, Fence fence)");
        source.ShouldContain("VulkanQueueOperationLease.TryEnter(");
        source.ShouldContain("_graphicsQueueOperationGate");
        source.ShouldContain("_deviceState");
        source.ShouldContain("ObserveDeviceResult(result);");
        source.Split("_api.QueueSubmit(", StringSplitOptions.None).Length.ShouldBe(2);
    }

    [Test]
    public void OpenXrExternalSwapchainTargets_DisableHistoryBasedAaAndTsrScaling()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        string advancedPipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.cs").Replace("\r\n", "\n");
        string postProcessSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        string postProcess2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.PostProcessing.cs").Replace("\r\n", "\n");
        string temporalSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs").Replace("\r\n", "\n");

        foreach (string source in new[] { pipelineSource, advancedPipelineSource })
        {
            source.ShouldContain("if (mode == EAntiAliasingMode.Tsr && DisableHistoryBasedVrEffects())\n            return null;");
            source.ShouldContain("&& !DisableHistoryBasedVrEffects()\n        && ResolveAntiAliasingMode() == EAntiAliasingMode.Tsr;");
            source.ShouldContain("private static bool RuntimeNeedsTemporalAaVelocityBuffer");
            source.ShouldContain("=> !DisableHistoryBasedVrEffects()\n        && ResolveAntiAliasingMode() is EAntiAliasingMode.Taa or EAntiAliasingMode.Dlaa;");
            source.ShouldContain("|| RuntimeNeedsTemporalAaVelocityBuffer");
        }

        foreach (string source in new[] { postProcessSource, postProcess2Source })
        {
            source.ShouldContain("private static bool DisableHistoryBasedVrEffects()\n        => !VPRC_TemporalAccumulationPass.TryUseHistoryBasedVrEffects(out _, out _);");
        }

        temporalSource.ShouldContain("ShouldDisableHistoryBasedVrAntiAliasing()");
        temporalSource.ShouldContain("internal static EVrTemporalHistoryPolicy ResolveHistoryIsolationPolicy(out string reason)");
        temporalSource.ShouldContain("EVrTemporalHistoryPolicy.StereoArrayLayer => \"true single-pass stereo array-layer history\"");
        temporalSource.ShouldContain("EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain => \"external per-eye swapchain targets\"");
        temporalSource.ShouldContain("VrViewRenderModeResolver.Resolve");
    }

    [Test]
    public void OpenXrStereoTemporalHistory_UsesPerViewStateAndArrayShaders()
    {
        string temporalSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs").Replace("\r\n", "\n");
        string texturesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.Textures.cs").Replace("\r\n", "\n");
        string textures2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.Textures.cs").Replace("\r\n", "\n");
        string fboSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.FBOs.cs").Replace("\r\n", "\n");
        string fbo2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.FBOs.cs").Replace("\r\n", "\n");
        string temporalStereoShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/TemporalAccumulationStereo.fs").Replace("\r\n", "\n");
        string tsrStereoShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/TemporalSuperResolutionStereo.fs").Replace("\r\n", "\n");
        string motionVectorStereoShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/MotionVectorsStereo.fs").Replace("\r\n", "\n");
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs").Replace("\r\n", "\n");
        string meshUniformsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs").Replace("\r\n", "\n");

        temporalSource.ShouldContain("internal readonly record struct TemporalViewKey");
        temporalSource.ShouldContain("private static readonly ConcurrentDictionary<TemporalViewKey, TemporalState> TemporalStates");
        temporalSource.ShouldContain("public TemporalEyeState LeftEye { get; } = new();");
        temporalSource.ShouldContain("public TemporalEyeState RightEye { get; } = new();");
        temporalSource.ShouldContain("RightEyePrevViewProjectionUnjittered");
        temporalSource.ShouldContain("TemporalStatesByPipelineInstance");
        temporalSource.ShouldContain("state.UniformSnapshot.TryRead(out data)");
        temporalSource.ShouldContain("PublishTemporalUniformData(state)");
        temporalSource.ShouldNotContain("lock (TemporalStatesLock)");
        temporalSource.ShouldContain("ActiveRightEyeJitterHandle");
        temporalSource.ShouldContain("rightEyeCamera.PushProjectionJitter");
        temporalSource.ShouldNotContain("ConditionalWeakTable<XRCamera, TemporalState>");

        foreach (string source in new[] { texturesSource, textures2Source })
        {
            source.ShouldContain("XRTexture2DArray stereoTexture = XRTexture2DArray.CreateFrameBufferTexture");
            source.ShouldContain("stereoTexture.SamplerName = textureName;");
            source.ShouldContain("stereoTexture.OVRMultiViewParameters = new(0, 2u);");
        }

        foreach (string source in new[] { fboSource, fbo2Source })
        {
            source.ShouldContain("Stereo ? \"TemporalSuperResolutionStereo.fs\" : \"TemporalSuperResolution.fs\"");
            source.ShouldContain("Stereo ? \"TemporalAccumulationStereo.fs\" : \"TemporalAccumulation.fs\"");
        }

        temporalStereoShader.ShouldContain("uniform sampler2DArray TemporalColorInput;");
        temporalStereoShader.ShouldContain("uniform sampler2DArray HistoryColor;");
        temporalStereoShader.ShouldContain("gl_ViewID_OVR");
        tsrStereoShader.ShouldContain("uniform sampler2DArray TsrHistoryColor;");
        tsrStereoShader.ShouldContain("uniform usampler2DArray StencilView;");
        tsrStereoShader.ShouldContain("gl_ViewID_OVR");
        tsrStereoShader.ShouldContain("PreviousJitterUv - CurrentJitterUv");
        motionVectorStereoShader.ShouldContain("uniform mat4 CurrViewProjectionStereo[2];");
        motionVectorStereoShader.ShouldContain("uniform mat4 PrevViewProjectionStereo[2];");
        motionVectorStereoShader.ShouldContain("int eyeIndex = int(gl_ViewID_OVR);");

        meshSource.ShouldContain("Matrix4x4 PreviousRightEyeViewMatrix");
        meshSource.ShouldContain("Matrix4x4 ViewProjectionMatrixUnjittered");
        meshSource.ShouldContain("Matrix4x4 RightEyeViewProjectionMatrixUnjittered");
        meshSource.ShouldContain("previousRightEyeProjectionMatrixSnapshot = temporalData.RightEyePrevProjection;");
        meshUniformsSource.ShouldContain("case EEngineUniform.PrevRightEyeViewMatrix:");
        meshUniformsSource.ShouldContain("value = draw.PreviousRightEyeViewMatrix;");
        meshUniformsSource.ShouldContain("case nameof(EEngineUniform.PrevRightEyeProjMatrix):");
        meshUniformsSource.ShouldContain("return UploadUniform(buffer, draw.PreviousRightEyeProjectionMatrix);");
        meshUniformsSource.ShouldContain("TryWriteTemporalViewProjectionUniform(data, member, draw, out wrote)");
        meshUniformsSource.ShouldContain("draw.RightEyeViewProjectionMatrixUnjittered");
        meshUniformsSource.ShouldContain("draw.PreviousRightEyeViewProjectionMatrixUnjittered");

        fboSource.ShouldContain("Stereo ? \"MotionVectorsStereo.fs\" : \"MotionVectors.fs\"");
        fboSource.ShouldNotContain("MotionVectorsMaterial_SettingUniforms");
        fbo2Source.ShouldContain("Stereo ? \"MotionVectorsStereo.fs\" : \"MotionVectors.fs\"");
        fbo2Source.ShouldNotContain("ApplyMotionVectorsProgramBindings");
    }

    [Test]
    public void VulkanStereoVariantSelection_DoesNotUseNvStereoSemantics()
    {
        string meshSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs");
        string uiBatchSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs");
        string defaultGeneratorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Shaders/Generator/DefaultVertexShaderGenerator.cs");
        string deformGeneratorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Shaders/Generator/MeshDeformVertexShaderGenerator.cs");
        string shaderCompilerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs");

        meshSource.ShouldContain("bool allowNvStereo = !RuntimeEngine.Rendering.State.IsVulkan;");
        meshSource.ShouldContain("bool preferNV = allowNvStereo && RuntimeEngine.Rendering.Settings.PreferNVStereo;");
        meshSource.ShouldContain("stereoPass && allowNvStereo && hasNvMaterialVertexShader");
        uiBatchSource.ShouldContain("if (!RuntimeEngine.Rendering.State.IsVulkan");

        defaultGeneratorSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan");
        defaultGeneratorSource.ShouldContain("Line(\"#extension GL_EXT_multiview : require\");");
        defaultGeneratorSource.ShouldContain("Line(\"#extension GL_NV_stereo_view_rendering : require\");");
        defaultGeneratorSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan ? \"gl_ViewIndex\" : \"gl_ViewID_OVR\"");
        deformGeneratorSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan");
        deformGeneratorSource.ShouldContain("Line(\"#extension GL_EXT_multiview : require\");");
        deformGeneratorSource.ShouldContain("Line(\"#extension GL_NV_stereo_view_rendering : require\");");
        deformGeneratorSource.ShouldContain("RuntimeEngine.Rendering.State.IsVulkan ? \"gl_ViewIndex\" : \"gl_ViewID_OVR\"");
        shaderCompilerSource.ShouldContain("LogVulkanStereoRewrite(shaderName, \"OVR_multiview/gl_ViewID_OVR\", \"GL_EXT_multiview/gl_ViewIndex\")");
        shaderCompilerSource.ShouldContain("LogVulkanStereoRewrite(shaderName, \"NV_stereo_view_rendering\", \"GL_EXT_multiview-compatible shader\")");
        shaderCompilerSource.ShouldContain("[VulkanShaderCompiler] Rewrote stereo shader");
    }

    [Test]
    public void VulkanDynamicRenderingMultiviewContracts_PropagateViewMaskAcrossBeginInheritanceAndPipeline()
    {
        string renderTargetModeSource = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "struct DynamicRenderingFormatSignature",
            "VulkanDynamicRenderingUtilities.ResolveLayerCount",
            "viewMask=0x{signature.ViewMask:X}");
        string framebufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondarySource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string openXrApiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs");

        renderTargetModeSource.ShouldContain("public uint ViewMask { get; }");
        renderTargetModeSource.ShouldContain("public uint LayerCount { get; }");
        renderTargetModeSource.ShouldContain("VulkanDynamicRenderingUtilities.ResolveLayerCount(layerCount, viewMask)");
        renderTargetModeSource.ShouldContain("viewMask=0x{signature.ViewMask:X}");
        renderTargetModeSource.ShouldContain("layers={signature.LayerCount}");

        framebufferSource.ShouldContain("public uint MultiviewViewMask { get; private set; }");
        framebufferSource.ShouldContain("ResolveFramebufferMultiviewViewMask(attachments)");
        framebufferSource.ShouldContain("return multiview is { NumViews: > 1u };");
        framebufferSource.ShouldContain("BuildMultiviewViewMask(ovr.Offset, ovr.NumViews, layerCount)");
        framebufferSource.ShouldContain("MultiviewViewMask = state.MultiviewViewMask;");

        commandBufferSource.ShouldContain("DynamicRenderingFormatSignature targetDynamicRenderingFormats = CreateDynamicRenderingFormatSignature(");
        commandBufferSource.ShouldContain("fboLayerCount);");
        commandBufferSource.ShouldContain("ViewMask = plan.ViewMask");
        commandBufferSource.ShouldContain("LayerCount = plan.LayerCount");
        commandBufferSource.ShouldContain("ViewMask = inheritedDynamicRenderingFormats.ViewMask");
        commandBufferSource.ShouldContain("VulkanDynamicRenderingUtilities.ResolveLayerCount(vkFrameBuffer.FramebufferLayers, fboViewMask)");
        commandBufferSource.ShouldContain("viewMask=0x{9:X}");

        secondarySource.ShouldContain("ViewMask = dynamicRenderingFormats.ViewMask");
        pipelineSource.ShouldContain("ViewMask = request.DynamicRenderingFormats.ViewMask");
        openXrApiSource.ShouldContain("Vulkan dynamic rendering is required for OpenXR true single-pass stereo multiview");
        openXrApiSource.ShouldContain("dynamicRendering={(Window?.Renderer is VulkanRenderer renderer && renderer.UseDynamicRenderingRenderTargets)}");
    }

    [Test]
    public void OpenXrParallelEyeRecording_UsesBoundedWorkersAndDeterministicFailureHandling()
    {
        string openXrSource = ReadOpenXrVulkanRendererSources();
        string openXrApiSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/OpenXR/VulkanXrGraphicsBinding.Implementation.cs");
        string workerSource = openXrSource;
        string commandBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string textureUploadStateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Transfers/VulkanTextureUploadPublicationState.cs");

        workerSource.ShouldContain("private OpenXrEyeRecordWorkerScheduler? OpenXrEyeWorkerSchedulerInstance");
        workerSource.ShouldContain("OpenXrEyeRecordWorkerScheduler scheduler = EnsureOpenXrEyeRecordWorkerScheduler();");
        workerSource.ShouldContain("_left.Start(renderer, leftEye);");
        workerSource.ShouldContain("_right.Start(renderer, rightEye);");
        workerSource.ShouldContain("OpenXrEyeRecordWorkerResult left = _left.Wait();");
        workerSource.ShouldContain("OpenXrEyeRecordWorkerResult right = _right.Wait();");
        workerSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out OpenXrPreparedEyeCommandBufferInput preparedFirstEye)");
        workerSource.ShouldContain("TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out OpenXrPreparedEyeCommandBufferInput preparedSecondEye)");
        workerSource.ShouldContain("DispatchOpenXrEyeRecordWorkers(preparedFirstEye, preparedSecondEye)");
        workerSource.ShouldContain("private OpenXrPreparedEyeCommandBufferInput _prepared;");
        openXrSource.ShouldContain("RefreshFrameOpResourceWrappers(");
        openXrSource.ShouldContain("PrewarmOpenXrFrameOpResources(");
        openXrSource.ShouldContain("TryRegisterFrameWideMeshFrameDataRequirements(");
        openXrSource.ShouldContain("int rendererCount = meshDrawSlotsByRenderer.Count;");
        openXrSource.ShouldContain("meshDrawSlotsByRendererFamily.Clear();");
        commandBufferSource.ShouldContain("case IndirectDrawOp indirectDrawOp:");
        openXrSource.ShouldContain("TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in prepared, out recorded)");
        openXrSource.ShouldContain("using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(");
        openXrSource.ShouldContain("CreateOpenXrEyeRenderStateTracker(in targetContext)");
        openXrSource.ShouldContain("EnterOpenXrResourcePlannerThreadScope(VulkanOpenXrViewResourcePlannerContextKey.FromTarget(in targetContext))");
        workerSource.ShouldContain("TryRecordOpenXrEyeSwapchainCommandBufferFromWorker");
        workerSource.ShouldContain("thread-scoped prepared primary record");
        workerSource.ShouldNotContain("ParallelEyePrimaryRecordSharedStateLock");
        workerSource.ShouldContain("return TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in prepared, out recorded);");
        workerSource.ShouldContain("ComputeOpenXrEyeRecordOverlap(");
        workerSource.ShouldContain("overlapMs={7:F3}");
        textureUploadStateSource.ShouldContain("ThreadLocal<List<VulkanImportedTexturePendingUpload>>");
        textureUploadStateSource.ShouldContain("Each persistent Vulkan recording worker owns one reusable list");
        workerSource.ShouldContain("leftSuccess={0} rightSuccess={1}");
        workerSource.ShouldContain("if (!hasFirst || !hasSecond)");
        workerSource.ShouldContain("LogOpenXrEyeRecordWorkerFailure(workerBatch);");
        workerSource.ShouldContain("SubmitAndWaitOpenXrCommandBuffers(");
        workerSource.ShouldContain("DestroyOpenXrEyeRecordWorkers()");
        openXrSource.ShouldContain("DestroyOpenXrEyeRecordWorkers();");
        openXrApiSource.ShouldContain("concurrent native Vulkan recording and command-buffer-local image state");
        workerSource.ShouldNotContain("Task.Run");
    }

    [Test]
    public void OpenXrParallelEyeRecording_ComputesNativeRecordSpanAndOverlap()
    {
        TimeSpan span = VulkanRenderer.ComputeOpenXrEyeRecordSpan(
            leftStart: 10,
            leftEnd: 110,
            rightStart: 40,
            rightEnd: 140);
        TimeSpan overlap = VulkanRenderer.ComputeOpenXrEyeRecordOverlap(
            leftStart: 10,
            leftEnd: 110,
            rightStart: 40,
            rightEnd: 140);

        span.ShouldBe(System.Diagnostics.Stopwatch.GetElapsedTime(10, 140));
        overlap.ShouldBe(System.Diagnostics.Stopwatch.GetElapsedTime(40, 110));
        VulkanRenderer.ComputeOpenXrEyeRecordOverlap(10, 20, 20, 30)
            .ShouldBe(TimeSpan.Zero);
        VulkanRenderer.ComputeOpenXrEyeRecordSpan(10, 10, 20, 30)
            .ShouldBe(TimeSpan.Zero);
    }

    [Test]
    public void RepeatedRendererReservation_StrictSpsCommandPinsReferencedGenerationsUntilCompletion()
    {
        var renderer = (VkMeshRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(VkMeshRenderer));
        System.Reflection.FieldInfo capacityField = typeof(VkMeshRenderer).GetField(
            "_uniformDrawSlotCapacity",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).ShouldNotBeNull();
        capacityField.SetValue(renderer, 4);

        PendingMeshDraw draw = default(PendingMeshDraw) with { Renderer = renderer };
        FrameOpContext context = CreateFrameOpContext();
        FrameOp[] ops =
        [
            new MeshDrawOp(0, null, draw, context),
            new MeshDrawOp(0, null, draw, context),
            new IndirectDrawOp(
                0,
                null,
                null!,
                null,
                renderer,
                draw,
                DrawCount: 3,
                Stride: 0,
                ByteOffset: 0,
                CountByteOffset: 0,
                UseCount: false,
                BindlessMaterialTextures: null,
                context),
        ];
        var rendererFamilies = new Dictionary<VulkanMeshFrameDataRendererFamilyKey, int>(
            VulkanMeshFrameDataRendererFamilyKeyComparer.Instance);
        var familyStrides = new Dictionary<VulkanMeshFrameDataFamilyKey, int>();

        VulkanRenderer.CollectMeshFrameDataRequirementsForRecording(
            ops,
            4,
            EVulkanMeshFrameDataStreamKind.Primary,
            rendererFamilies,
            familyStrides);
        rendererFamilies.Single().Value.ShouldBe(3);
        familyStrides.Count.ShouldBe(1);
        familyStrides.Single().Value.ShouldBe(3);
        capacityField.GetValue(renderer).ShouldBe(4);

        // A second reservation for the identical strict-SPS stream is steady
        // state: it neither grows capacity nor invokes descriptor/buffer teardown.
        VulkanRenderer.CollectMeshFrameDataRequirementsForRecording(
            ops,
            4,
            EVulkanMeshFrameDataStreamKind.Primary,
            rendererFamilies,
            familyStrides);
        rendererFamilies.Single().Value.ShouldBe(3);
        capacityField.GetValue(renderer).ShouldBe(4);

        ResourcePlanSnapshot physicalPlanGeneration = new(
            Revision: 23,
            PhysicalImageSignature: 0xA100,
            FramebufferSignature: 0xB200,
            PipelineGeneration: 17);
        VulkanRenderer.VulkanResourceLifetimeRecord[] resources =
        [
            CreateLifetimeRecord(ObjectType.Buffer, 0x1001, 101, "StrictSps.Mesh.UniformBuffer"),
            CreateLifetimeRecord(ObjectType.DescriptorSet, 0x1002, 102, "StrictSps.Mesh.DescriptorSet"),
            CreateLifetimeRecord(ObjectType.ImageView, 0x1003, 103, "StrictSps.Color.ImageView"),
            CreateLifetimeRecord(ObjectType.Framebuffer, 0x1004, 104, "StrictSps.Multiview.Framebuffer"),
            CreateLifetimeRecord(
                ObjectType.Image,
                0x1005,
                105,
                $"StrictSps.PhysicalPlan.r{physicalPlanGeneration.Revision}.ColorImage"),
        ];

        // Model the exact production command-buffer dependency set. Repeating a
        // renderer in one command must not multiply the command-level pin, but
        // every referenced generation must be present before publication.
        var commandLifetime = new VulkanRenderer.VulkanCommandBufferLifetimeRecord();
        for (int useIndex = 0; useIndex < 3; useIndex++)
        {
            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                VulkanRenderer.AddVulkanRecordedGenerationPin(commandLifetime, resources[resourceIndex])
                    .ShouldBe(useIndex == 0);
            }
        }

        commandLifetime.RefreshTouchedDependencies();
        commandLifetime.Dependencies.Count.ShouldBe(resources.Length);
        commandLifetime.TouchedDependencies.Count.ShouldBe(resources.Length);
        resources.ShouldAllBe(static resource => resource.Pins.RecordedReferenceCount == 1);
        AssertGenerationSetRetirementReady(resources, completedGraphicsSequence: ulong.MaxValue, expected: false);

        // The validation-to-dispatch gateway adds a separate queue pin without
        // dropping the recorded dependency.
        for (int i = 0; i < resources.Length; i++)
            VulkanRenderer.AddVulkanQueuedGenerationPin_NoLock(resources[i]);
        resources.ShouldAllBe(static resource => resource.Pins.RecordedReferenceCount == 1);
        resources.ShouldAllBe(static resource => resource.Pins.QueuedReferenceCount == 1);
        AssertGenerationSetRetirementReady(resources, completedGraphicsSequence: ulong.MaxValue, expected: false);

        // Successful submit transfers queue protection to an exact completion
        // sequence. Releasing the recorded command later must still leave every
        // physical-plan generation pinned until that sequence completes.
        for (int i = 0; i < resources.Length; i++)
        {
            VulkanRenderer.MarkVulkanResourceSubmitted_NoLock(
                resources[i],
                VulkanRenderer.EVulkanLifetimeQueueDomain.Graphics,
                queueSequence: 7,
                submissionSerial: 31,
                frameOpContextId: 41,
                frameOpKind: "OpenXR.TrueSinglePassStereo");
            VulkanRenderer.ReleaseVulkanQueuedGenerationPin_NoLock(resources[i]);
            VulkanRenderer.ReleaseVulkanRecordedGenerationPin(resources[i]);
        }

        resources.ShouldAllBe(static resource => resource.Pins.RecordedReferenceCount == 0);
        resources.ShouldAllBe(static resource => resource.Pins.QueuedReferenceCount == 0);
        resources.ShouldAllBe(static resource => resource.Pins.LastGraphicsSequence == 7);
        AssertGenerationSetRetirementReady(resources, completedGraphicsSequence: 6, expected: false);
        AssertGenerationSetRetirementReady(resources, completedGraphicsSequence: 7, expected: true);
    }

    [Test]
    public void BuildRenderViewKey_ShadowPassIncludesLightAndCascadeIdentity()
    {
        RenderPassMetadata shadowPass = new(5, "DirectionalShadowCascade", ERenderGraphPassStage.Graphics);
        LayeredShadowUniformState shadowState = new()
        {
            IsShadowPass = true,
            DirectionalCascadeInstancedLayeredShadowPass = true,
            DirectionalCascadeShadowLayerCount = 4,
        };
        MeshDrawOp op = CreateMeshDrawOp(
            default(PendingMeshDraw) with { ShadowUniformState = shadowState },
            passIndex: 5,
            context: CreateFrameOpContext(passMetadata: [shadowPass]));

        RenderViewKey key = VulkanRenderer.BuildRenderViewKey(op, dynamicOverlay: false);

        key.Kind.ShouldBe(RenderViewKind.Shadow);
        key.LightIdentity.ShouldNotBe(0);
        key.CascadeIndex.ShouldBe(3);
        key.ViewIndex.ShouldBe(3);
    }

    [Test]
    public void ShadowCommandChainStructuralSignature_ChangesForAtlasPackingState()
    {
        LayeredShadowUniformState fourCascadeState = new()
        {
            IsShadowPass = true,
            DirectionalCascadeInstancedLayeredShadowPass = true,
            DirectionalCascadeShadowLayerCount = 4,
        };
        LayeredShadowUniformState twoCascadeState = fourCascadeState;
        twoCascadeState.DirectionalCascadeShadowLayerCount = 2;

        VulkanRenderer.ComputeShadowCommandChainStructuralSignature(fourCascadeState)
            .ShouldNotBe(VulkanRenderer.ComputeShadowCommandChainStructuralSignature(twoCascadeState));
    }

    [Test]
    public void ValidateCommandChainShadowFallbackMode_AllowsOnlyExplicitReusableShadowFallbacks()
    {
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.None, shadowTileResident: true));
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.StaleTile, shadowTileResident: true));
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.Lit, shadowTileResident: false));
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.Legacy, shadowTileResident: true))
            .Message.ShouldContain("fallback mode");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainShadowFallbackMode(ShadowFallbackMode.None, shadowTileResident: false))
            .Message.ShouldContain("explicit fallback");
    }

    [Test]
    public void CommandChainKey_IncludesFrameSlotAndOrdinal()
    {
        RenderViewKey view = new(1, 2, 0, RenderViewKind.Main, 0, -1);
        CommandChainKey slotZero = new(0, view, 3, 4, false, 5);
        CommandChainKey slotOne = slotZero with { FrameSlot = 1 };
        CommandChainKey differentOrdinal = slotZero with { ChainOrdinal = 6 };
        CommandChainKey dynamicOverlay = slotZero with { DynamicOverlay = true };

        slotZero.ShouldNotBe(slotOne);
        slotZero.ShouldNotBe(differentOrdinal);
        slotZero.ShouldNotBe(dynamicOverlay);
        slotZero.ShouldBe(new CommandChainKey(0, view, 3, 4, false, 5));
    }

    [Test]
    public void RenderPacketVolatility_Order_IsIntentionalForDiagnostics()
    {
        ((int)RenderPacketVolatility.StaticStructural).ShouldBe(0);
        ((int)RenderPacketVolatility.FrameDataOnly).ShouldBe(1);
        ((int)RenderPacketVolatility.DynamicCommand).ShouldBe(2);
        ((int)RenderPacketVolatility.StructuralDirty).ShouldBe(3);
    }

    [Test]
    public void ClassifyRenderPacketVolatility_StaticClearAndBarrier_AreStaticStructural()
    {
        FrameOpContext context = CreateFrameOpContext();
        ClearOp clear = new(
            PassIndex: 0,
            Target: null,
            ClearColor: true,
            ClearDepth: true,
            ClearStencil: false,
            Color: default,
            Depth: 1.0f,
            Stencil: 0,
            Rect: default,
            Context: context);
        MemoryBarrierOp barrier = new(
            PassIndex: 0,
            Mask: EMemoryBarrierMask.TextureFetch,
            Context: context);

        VulkanRenderer.ClassifyRenderPacketVolatility(clear, dynamicOverlay: false)
            .ShouldBe(RenderPacketVolatility.StaticStructural);
        VulkanRenderer.ClassifyRenderPacketVolatility(barrier, dynamicOverlay: false)
            .ShouldBe(RenderPacketVolatility.StaticStructural);
    }

    [Test]
    public void ClassifyRenderPacketVolatility_OverlayPassMetadata_IsDynamicCommand()
    {
        RenderPassMetadata overlayPass = new(7, "ProfilerOverlay", ERenderGraphPassStage.Graphics);
        FrameOpContext context = CreateFrameOpContext(passMetadata: [overlayPass]);
        ClearOp clear = new(
            PassIndex: 7,
            Target: null,
            ClearColor: true,
            ClearDepth: false,
            ClearStencil: false,
            Color: default,
            Depth: 1.0f,
            Stencil: 0,
            Rect: default,
            Context: context);

        VulkanRenderer.ClassifyRenderPacketVolatility(clear, dynamicOverlay: false)
            .ShouldBe(RenderPacketVolatility.DynamicCommand);
    }

    [Test]
    public void ClassifyRenderPacketVolatility_DynamicOverlayFlag_OverridesStaticOp()
    {
        ClearOp clear = new(
            PassIndex: 0,
            Target: null,
            ClearColor: true,
            ClearDepth: true,
            ClearStencil: false,
            Color: default,
            Depth: 1.0f,
            Stencil: 0,
            Rect: default,
            Context: CreateFrameOpContext());

        VulkanRenderer.ClassifyRenderPacketVolatility(clear, dynamicOverlay: true)
            .ShouldBe(RenderPacketVolatility.DynamicCommand);
    }

    [Test]
    public void ClassifyRenderPacketVolatility_ComputeDispatch_IsFrameDataOnlyUnlessOverlay()
    {
        ComputeDispatchOp compute = CreateComputeDispatchOp();

        VulkanRenderer.ClassifyRenderPacketVolatility(compute, dynamicOverlay: false)
            .ShouldBe(RenderPacketVolatility.FrameDataOnly);
        VulkanRenderer.ClassifyRenderPacketVolatility(compute, dynamicOverlay: true)
            .ShouldBe(RenderPacketVolatility.DynamicCommand);
    }

    [Test]
    public void CommandChainDirtyReason_FrameDataOnlyChange_RemainsReusable()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature + 1,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.None);
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_UpdatesFrameDataSignature()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature + 1,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, packet)
            .ShouldBeTrue();
        chain.FrameDataSignature.ShouldBe(packet.FrameDataSignature);
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_RejectsDescriptorContentChange()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x600,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature + 1,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration + 1,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature + 1,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(
                CommandChainDirtyReason.Structure |
                CommandChainDirtyReason.ResourcePlan |
                CommandChainDirtyReason.DescriptorGeneration);
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, packet)
            .ShouldBeFalse();
        chain.FrameDataSignature.ShouldBe(baseline.FrameDataSignature);
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_AllowsUniformOnlyFrameDataChange()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x600,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature + 1,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.None);
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, packet)
            .ShouldBeTrue();
        chain.FrameDataSignature.ShouldBe(packet.FrameDataSignature);
        chain.FrameDataRefreshTouchedDescriptors.ShouldBeFalse();
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_ComputeDispatchRequiresMatchingDescriptors()
    {
        DispatchPacket[] dispatches =
        [
            new DispatchPacket(0, ProgramIdentity: 1, GroupsX: 1, GroupsY: 1, GroupsZ: 1, StructuralSignature: 0x100, FrameDataSignature: 0x200),
        ];
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x401,
            dispatches: dispatches);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket descriptorChanged = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x201,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x401,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x402,
            dispatches: dispatches);

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, descriptorChanged)
            .ShouldBeFalse();

        RenderPacket uniformOnlyChanged = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x202,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x401,
            dispatches: dispatches);

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, uniformOnlyChanged)
            .ShouldBeTrue();
        chain.FrameDataRefreshTouchedDescriptors.ShouldBeFalse();
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_RejectsDescriptorPublication()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x600,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature + 1,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration + 1,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.ResourcePlan | CommandChainDirtyReason.DescriptorGeneration);
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, packet)
            .ShouldBeFalse();
        chain.FrameDataSignature.ShouldBe(baseline.FrameDataSignature);
        chain.DescriptorGeneration.ShouldBe(baseline.DescriptorSnapshot.DescriptorGeneration);
        chain.FrameDataRefreshTouchedDescriptors.ShouldBeFalse();
    }

    [Test]
    public void P04ResourceScenarios_InvalidateOnlyTheirExactPacketClass()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x600,
            physicalImageSignature: 0x700,
            framebufferSignature: 0x800,
            volatility: RenderPacketVolatility.FrameDataOnly);

        CommandChain materialEditChain = CreateRecordedChain(baseline);
        RenderPacket materialEdit = CreatePacket(
            baseline.StructuralSignature,
            baseline.FrameDataSignature + 1,
            baseline.ResourcePlanSnapshot.Revision,
            baseline.DescriptorSnapshot.DescriptorGeneration,
            baseline.ResourcePlanSnapshot.PipelineGeneration,
            RenderPacketVolatility.FrameDataOnly,
            baseline.DescriptorSnapshot.DescriptorSetCount,
            baseline.DescriptorSnapshot.DescriptorSetSignature,
            baseline.ResourcePlanSnapshot.PhysicalImageSignature,
            baseline.ResourcePlanSnapshot.FramebufferSignature);
        VulkanRenderer.EvaluateCommandChainDirtyReason(materialEditChain, materialEdit)
            .ShouldBe(CommandChainDirtyReason.None);

        CommandChain texturePublicationChain = CreateRecordedChain(baseline);
        RenderPacket texturePublication = CreatePacket(
            baseline.StructuralSignature,
            baseline.FrameDataSignature + 1,
            baseline.ResourcePlanSnapshot.Revision,
            baseline.DescriptorSnapshot.DescriptorGeneration + 1,
            baseline.ResourcePlanSnapshot.PipelineGeneration,
            RenderPacketVolatility.FrameDataOnly,
            baseline.DescriptorSnapshot.DescriptorSetCount,
            baseline.DescriptorSnapshot.DescriptorSetSignature,
            baseline.ResourcePlanSnapshot.PhysicalImageSignature,
            baseline.ResourcePlanSnapshot.FramebufferSignature);
        VulkanRenderer.EvaluateCommandChainDirtyReason(texturePublicationChain, texturePublication)
            .ShouldBe(CommandChainDirtyReason.ResourcePlan | CommandChainDirtyReason.DescriptorGeneration);
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(texturePublicationChain, texturePublication)
            .ShouldBeFalse();

        CommandChain resizeChain = CreateRecordedChain(baseline);
        RenderPacket resize = CreatePacket(
            baseline.StructuralSignature,
            baseline.FrameDataSignature,
            baseline.ResourcePlanSnapshot.Revision + 1,
            baseline.DescriptorSnapshot.DescriptorGeneration,
            baseline.ResourcePlanSnapshot.PipelineGeneration,
            RenderPacketVolatility.FrameDataOnly,
            baseline.DescriptorSnapshot.DescriptorSetCount,
            baseline.DescriptorSnapshot.DescriptorSetSignature,
            baseline.ResourcePlanSnapshot.PhysicalImageSignature + 1,
            baseline.ResourcePlanSnapshot.FramebufferSignature + 1);
        VulkanRenderer.EvaluateCommandChainDirtyReason(resizeChain, resize)
            .ShouldBe(CommandChainDirtyReason.Structure | CommandChainDirtyReason.ResourcePlan);

        CommandChain hotReloadChain = CreateRecordedChain(baseline);
        RenderPacket hotReload = CreatePacket(
            baseline.StructuralSignature,
            baseline.FrameDataSignature,
            baseline.ResourcePlanSnapshot.Revision,
            baseline.DescriptorSnapshot.DescriptorGeneration,
            baseline.ResourcePlanSnapshot.PipelineGeneration + 1,
            RenderPacketVolatility.FrameDataOnly,
            baseline.DescriptorSnapshot.DescriptorSetCount,
            baseline.DescriptorSnapshot.DescriptorSetSignature,
            baseline.ResourcePlanSnapshot.PhysicalImageSignature,
            baseline.ResourcePlanSnapshot.FramebufferSignature);
        VulkanRenderer.EvaluateCommandChainDirtyReason(hotReloadChain, hotReload)
            .ShouldBe(CommandChainDirtyReason.Structure | CommandChainDirtyReason.PipelineGeneration);
    }

    [Test]
    public void P04SwapchainRotation_RequiresRerecordBeforeEachSlotPublication()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            descriptorSetCount: 1,
            descriptorSetSignature: 0x600,
            volatility: RenderPacketVolatility.FrameDataOnly);
        RenderPacket publication = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature + 1,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration + 1,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);
        CommandChain[] slots =
        [
            CreateRecordedChain(baseline, frameSlot: 0),
            CreateRecordedChain(baseline, frameSlot: 1),
            CreateRecordedChain(baseline, frameSlot: 2),
        ];

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(slots[0], publication).ShouldBeFalse();
        slots[0].DescriptorGeneration.ShouldBe(baseline.DescriptorSnapshot.DescriptorGeneration);
        slots[1].DescriptorGeneration.ShouldBe(baseline.DescriptorSnapshot.DescriptorGeneration);
        slots[2].DescriptorGeneration.ShouldBe(baseline.DescriptorSnapshot.DescriptorGeneration);

        VulkanRenderer.VulkanResourceLifetimeRecord retiredImage =
            CreateLifetimeRecord(ObjectType.Image, 0x991, 73, "P04.StreamedTexture.OldImage");
        for (ulong completionSequence = 11; completionSequence <= 13; completionSequence++)
        {
            VulkanRenderer.MarkVulkanResourceSubmitted_NoLock(
                retiredImage,
                VulkanRenderer.EVulkanLifetimeQueueDomain.Graphics,
                completionSequence,
                submissionSerial: completionSequence,
                frameOpContextId: completionSequence,
                frameOpKind: "P04.ForcedPublicationDelay");
        }

        retiredImage.Pins.IsRetirementReady(12, 0, 0).ShouldBeFalse();
        retiredImage.Pins.IsRetirementReady(13, 0, 0).ShouldBeTrue();

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(slots[1], publication).ShouldBeFalse();
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(slots[2], publication).ShouldBeFalse();
        slots.ShouldAllBe(chain => chain.DescriptorGeneration == baseline.DescriptorSnapshot.DescriptorGeneration);
        slots.Select(static chain => chain.Key.FrameSlot).ShouldBe([0, 1, 2]);
    }

    [Test]
    public void TryRefreshReusableCommandChainFrameData_RejectsStaticAndStructurallyDirtyPackets()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket staticPacket = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature + 1,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            volatility: RenderPacketVolatility.StaticStructural);
        RenderPacket structurallyDirtyPacket = CreatePacket(
            structuralSignature: chain.StructuralSignature + 1,
            frameDataSignature: chain.FrameDataSignature + 2,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, staticPacket)
            .ShouldBeFalse();
        VulkanRenderer.TryRefreshReusableCommandChainFrameData(chain, structurallyDirtyPacket)
            .ShouldBeFalse();
        chain.FrameDataSignature.ShouldBe(0x200UL);
    }

    [Test]
    public void CommandChainDirtyReason_DetectsStructuralChange()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature + 1,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.Structure);
    }

    [Test]
    public void CommandChainDirtyReason_DetectsDescriptorResourceAndPipelineChanges()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision + 1,
            descriptorGeneration: chain.DescriptorGeneration + 1,
            pipelineGeneration: chain.PipelineGeneration + 1,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(
                CommandChainDirtyReason.Structure |
                CommandChainDirtyReason.ResourcePlan |
                CommandChainDirtyReason.DescriptorGeneration |
                CommandChainDirtyReason.PipelineGeneration);
    }

    [Test]
    public void CommandChainDirtyReason_DetectsPhysicalImageAndFramebufferChangesAsResourcePlan()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature + 1,
            framebufferSignature: chain.FramebufferSignature + 1,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.Structure | CommandChainDirtyReason.ResourcePlan);
    }

    [Test]
    public void ValidateReusableCommandChainReferences_AllowsCurrentSnapshots()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature + 1,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature,
            framebufferSignature: chain.FramebufferSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        Should.NotThrow(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, packet));
    }

    [Test]
    public void ValidateReusableCommandChainReferences_RejectsStaleDescriptorSets()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration + 1,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature,
            framebufferSignature: chain.FramebufferSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, packet))
            .Message.ShouldContain("stale descriptor-set");
    }

    [Test]
    public void ValidateReusableCommandChainReferences_RejectsStalePhysicalImagesAndFramebuffers()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket stalePhysicalImage = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature + 1,
            framebufferSignature: chain.FramebufferSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);
        RenderPacket staleFramebuffer = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature,
            framebufferSignature: chain.FramebufferSignature + 1,
            volatility: RenderPacketVolatility.FrameDataOnly);

        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, stalePhysicalImage))
            .Message.ShouldContain("stale physical-image");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, staleFramebuffer))
            .Message.ShouldContain("stale framebuffer");
    }

    [Test]
    public void ValidateReusableCommandChainReferences_RejectsStalePipelineHandles()
    {
        CommandChain chain = CreateRecordedChain();
        RenderPacket packet = CreatePacket(
            structuralSignature: chain.StructuralSignature,
            frameDataSignature: chain.FrameDataSignature,
            resourcePlanRevision: chain.ResourcePlanRevision,
            descriptorGeneration: chain.DescriptorGeneration,
            pipelineGeneration: chain.PipelineGeneration + 1,
            descriptorSetCount: chain.DescriptorSetCount,
            descriptorSetSignature: chain.DescriptorSetSignature,
            physicalImageSignature: chain.PhysicalImageSignature,
            framebufferSignature: chain.FramebufferSignature,
            volatility: RenderPacketVolatility.FrameDataOnly);

        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateReusableCommandChainReferences(chain, packet))
            .Message.ShouldContain("stale pipeline");
    }

    [Test]
    public void CommandChainDirtyReason_DetectsPacketShapeChangesAsStructure()
    {
        RenderPacket baseline = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly,
            draws: [CreateDrawPacket(instanceCount: 1)]);
        CommandChain chain = CreateRecordedChain(baseline);
        RenderPacket packet = CreatePacket(
            structuralSignature: baseline.StructuralSignature,
            frameDataSignature: baseline.FrameDataSignature,
            resourcePlanRevision: baseline.ResourcePlanSnapshot.Revision,
            descriptorGeneration: baseline.DescriptorSnapshot.DescriptorGeneration,
            pipelineGeneration: baseline.ResourcePlanSnapshot.PipelineGeneration,
            descriptorSetCount: baseline.DescriptorSnapshot.DescriptorSetCount + 1,
            descriptorSetSignature: baseline.DescriptorSnapshot.DescriptorSetSignature + 1,
            volatility: RenderPacketVolatility.FrameDataOnly,
            draws: [CreateDrawPacket(instanceCount: 2), CreateDrawPacket(instanceCount: 3)]);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.Structure | CommandChainDirtyReason.ResourcePlan);
    }

    [Test]
    public void CommandChainDirtyReason_UnrecordedChain_DirtiesStructure()
    {
        CommandChain chain = new(new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 3, 4, false, 5));
        RenderPacket packet = CreatePacket(
            structuralSignature: 10,
            frameDataSignature: 20,
            resourcePlanRevision: 30,
            descriptorGeneration: 40,
            pipelineGeneration: 50,
            volatility: RenderPacketVolatility.FrameDataOnly);

        VulkanRenderer.EvaluateCommandChainDirtyReason(chain, packet)
            .ShouldBe(CommandChainDirtyReason.Structure);
    }

    [Test]
    public void PrimaryCommandBufferDirtyReason_IsCleanForMatchingSchedule()
    {
        CommandChainSchedule schedule = CreateSchedule(dynamicOverlay: false, chainCount: 2);
        ulong groupSignature = VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(schedule);

        VulkanRenderer.EvaluatePrimaryCommandBufferDirtyReason(
                schedule,
                recordedScheduleSignature: schedule.StructuralSignature,
                recordedGroupSignature: groupSignature,
                recordedGroupCount: schedule.Groups.Length,
                recordedProfilerActive: false,
                recordedProfilerFrameSlot: -1,
                currentProfilerActive: false,
                currentProfilerFrameSlot: 0)
            .ShouldBe(PrimaryCommandBufferDirtyReason.None);
    }

    [Test]
    public void PrimaryCommandBufferDirtyReason_SeparatesScheduleAndProfilerChanges()
    {
        CommandChainSchedule schedule = CreateSchedule(dynamicOverlay: false, chainCount: 2);

        VulkanRenderer.EvaluatePrimaryCommandBufferDirtyReason(
                schedule,
                recordedScheduleSignature: schedule.StructuralSignature + 1,
                recordedGroupSignature: VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(schedule) + 1,
                recordedGroupCount: schedule.Groups.Length + 1,
                recordedProfilerActive: false,
                recordedProfilerFrameSlot: -1,
                currentProfilerActive: true,
                currentProfilerFrameSlot: 0)
            .ShouldBe(
                PrimaryCommandBufferDirtyReason.ScheduleStructure |
                PrimaryCommandBufferDirtyReason.GroupStructure |
                PrimaryCommandBufferDirtyReason.ProfilerMode);
    }

    [Test]
    public void PrimaryCommandBufferGroupSignature_ChangesWhenGroupShapeChanges()
    {
        CommandChainSchedule oneChain = CreateSchedule(dynamicOverlay: false, chainCount: 1);
        CommandChainSchedule twoChains = CreateSchedule(dynamicOverlay: false, chainCount: 2);
        CommandChainSchedule overlay = CreateSchedule(dynamicOverlay: true, chainCount: 1);

        VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(oneChain)
            .ShouldNotBe(VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(twoChains));
        VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(oneChain)
            .ShouldNotBe(VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(overlay));
    }

    [Test]
    public void PrimaryCommandBufferGroupSignature_IgnoresSecondaryPacketContent()
    {
        RenderPassChainGroup baselineGroup = CreateGroup(
            passIndex: 3,
            targetIdentity: 4,
            dynamicOverlay: false,
            chainCount: 2);
        RenderPassChainGroup changedPacketContentGroup = new(
            baselineGroup.PassIndex,
            baselineGroup.TargetIdentity,
            baselineGroup.TargetName,
            baselineGroup.ChainKeys,
            baselineGroup.StructuralSignature + 1UL,
            baselineGroup.SupportsSecondaryCommandBuffers,
            baselineGroup.DynamicOverlay);
        CommandChainSchedule baseline = new(
            structuralSignature: 0x100UL,
            resourcePlanRevision: 0x200UL,
            groups: new[] { baselineGroup });
        CommandChainSchedule changedPacketContent = new(
            structuralSignature: 0x101UL,
            resourcePlanRevision: 0x200UL,
            groups: new[] { changedPacketContentGroup });

        VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(baseline)
            .ShouldBe(VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(changedPacketContent));
    }

    [Test]
    public void PrimaryCommandBufferGroupSignature_ReferencesExactSecondaryArtifactGeneration()
    {
        CommandChainKey key = new(
            0,
            new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1),
            PassIndex: 3,
            TargetIdentity: 4,
            DynamicOverlay: false,
            ChainOrdinal: 5);
        RenderPassChainGroup group = new(
            passIndex: 3,
            targetIdentity: 4,
            targetName: "Target",
            chainKeys: new[] { key },
            structuralSignature: 0x100,
            supportsSecondaryCommandBuffers: true,
            dynamicOverlay: false);
        CommandChainSchedule schedule = new(
            structuralSignature: 0x200,
            resourcePlanRevision: 0x300,
            groups: new[] { group });
        CommandChain chain = new(key);
        chain.RecordedArtifact.AssignNativeBuffer(
            new CommandBuffer(0x404),
            new CommandPool(0x505),
            ownsPool: false);
        Dictionary<CommandChainKey, CommandChain> chains = new()
        {
            [key] = chain,
        };

        ulong allocatedIdentity =
            VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(
                schedule,
                chains);
        VulkanCommandIdentityComponents allocatedComponents =
            VulkanRenderer.ComputePrimaryCommandBufferGroupIdentity(
                schedule,
                chains);
        chain.RecordedArtifact.Invalidate(
            EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged);
        ulong invalidatedIdentity =
            VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(
                schedule,
                chains);
        VulkanCommandIdentityComponents invalidatedComponents =
            VulkanRenderer.ComputePrimaryCommandBufferGroupIdentity(
                schedule,
                chains);

        invalidatedIdentity.ShouldNotBe(allocatedIdentity);
        allocatedComponents.Compare(invalidatedComponents).Component.ShouldBe(
            EVulkanCommandIdentityComponent.NestedArtifacts);
        allocatedComponents.PrimaryOnly.ShouldBe(
            invalidatedComponents.PrimaryOnly);
        allocatedComponents.SecondaryOnly.ShouldBe(0UL);
        invalidatedComponents.SecondaryOnly.ShouldBe(0UL);
    }

    [Test]
    public void ValidatePrimaryCommandChainSchedule_RequiresStaticGroupsBeforeOverlayGroups()
    {
        MeshDrawOp firstStatic = CreateMeshDrawOp(default, passIndex: 0);
        MeshDrawOp secondStatic = CreateMeshDrawOp(default, passIndex: 0);
        CommandChainSchedule valid = new(
            structuralSignature: 0x100,
            resourcePlanRevision: 0x200,
            groups: new[]
            {
                CreateGroup(passIndex: 0, targetIdentity: 0, dynamicOverlay: false, chainCount: 2),
            });
        CommandChainSchedule invalid = new(
            structuralSignature: 0x101,
            resourcePlanRevision: 0x200,
            groups: new[]
            {
                CreateGroup(passIndex: 10, targetIdentity: 0, dynamicOverlay: true, chainCount: 1),
                CreateGroup(passIndex: 0, targetIdentity: 0, dynamicOverlay: false, chainCount: 2),
            });

        Should.NotThrow(() => VulkanRenderer.ValidatePrimaryCommandChainSchedule(valid, [firstStatic, secondStatic], dynamicOverlayOpCount: 1));
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidatePrimaryCommandChainSchedule(invalid, [firstStatic, secondStatic], dynamicOverlayOpCount: 1))
            .Message.ShouldContain("dynamic overlay group before");
    }

    [Test]
    public void ValidateCommandChainViewSpecialization_RequiresVrOrderingAndShadowIdentity()
    {
        RenderViewKey leftEye = new(1, 2, VulkanRenderer.CommandChainLeftEyeViewIndex, RenderViewKind.VREye, 0, -1);
        RenderViewKey rightEye = leftEye with { ViewIndex = VulkanRenderer.CommandChainRightEyeViewIndex };
        RenderViewKey multiviewEye = leftEye with { ViewIndex = VulkanRenderer.CommandChainStereoMultiviewViewIndex };
        CommandChainSchedule validVr = new(
            structuralSignature: 0x100,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, leftEye, 0, 0, false, 0), new CommandChainKey(0, rightEye, 0, 0, false, 1)) });
        CommandChainSchedule validMultiviewVr = new(
            structuralSignature: 0x101,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, multiviewEye, 0, 0, false, 0)) });
        CommandChainSchedule invalidVr = new(
            structuralSignature: 0x102,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, rightEye, 0, 0, false, 0), new CommandChainKey(0, leftEye, 0, 0, false, 1)) });
        CommandChainSchedule invalidMixedVr = new(
            structuralSignature: 0x103,
            resourcePlanRevision: 0x200,
            groups: new[]
            {
                CreateGroupForKeys(new CommandChainKey(0, multiviewEye, 0, 0, false, 0)),
                CreateGroupForKeys(new CommandChainKey(0, leftEye, 0, 0, false, 1), new CommandChainKey(0, rightEye, 0, 0, false, 2)),
            });
        RenderViewKey invalidShadow = new(1, 2, 0, RenderViewKind.Shadow, 0, -1);
        CommandChainSchedule invalidShadowSchedule = new(
            structuralSignature: 0x104,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, invalidShadow, 0, 0, false, 0)) });

        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainViewSpecialization(validVr));
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainViewSpecialization(validMultiviewVr));
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainViewSpecialization(invalidVr))
            .Message.ShouldContain("left eye before right eye");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainViewSpecialization(invalidMixedVr))
            .Message.ShouldContain("mixes");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainViewSpecialization(invalidShadowSchedule))
            .Message.ShouldContain("shadow key");
    }

    [Test]
    public void BuildCommandChainQueueSchedule_DefaultsToSingleGraphicsFallback()
    {
        CommandChainSchedule commandSchedule = CreateSchedule(dynamicOverlay: false, chainCount: 2);
        CommandChainQueueSchedule queueSchedule = VulkanRenderer.BuildCommandChainQueueSchedule(
            commandSchedule,
            multiQueueRequested: true,
            hasSecondaryGraphicsQueue: true,
            hasAsyncComputeQueue: true,
            hasTransferQueue: true);

        queueSchedule.MultiQueueEnabled.ShouldBeFalse();
        queueSchedule.SingleQueueFallbackAvailable.ShouldBeTrue();
        queueSchedule.Nodes.Length.ShouldBe(1);
        queueSchedule.Nodes.Span[0].QueueKind.ShouldBe(CommandChainQueueKind.Graphics);
        queueSchedule.Nodes.Span[0].GroupIndices.Length.ShouldBe(commandSchedule.Groups.Length);
        queueSchedule.Diagnostics.ShouldContain("graphics queue fallback");
    }

    [Test]
    public void IdentifyCommandChainQueueEligibility_FindsSidecarCandidatesWithoutEnablingThem()
    {
        RenderPassChainGroup computeGroup = CreateGroupForKeys(
            new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 0, 3, false, 0),
            new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 0, 3, false, 1));
        computeGroup = new RenderPassChainGroup(
            computeGroup.PassIndex,
            computeGroup.TargetIdentity,
            "SkinComputeTarget",
            computeGroup.ChainKeys,
            computeGroup.StructuralSignature,
            computeGroup.SupportsSecondaryCommandBuffers,
            computeGroup.DynamicOverlay);

        CommandChainQueueEligibility eligibility = VulkanRenderer.IdentifyCommandChainQueueEligibility(computeGroup);

        eligibility.HasFlag(CommandChainQueueEligibility.Graphics).ShouldBeTrue();
        eligibility.HasFlag(CommandChainQueueEligibility.SecondaryGraphics).ShouldBeTrue();
        eligibility.HasFlag(CommandChainQueueEligibility.Compute).ShouldBeTrue();
    }

    [Test]
    public void BuildCommandChainKeysByFrameOpIndex_UsesRecordedSourceIndices()
    {
        RenderViewKey viewKey = new(1, 2, 0, RenderViewKind.Main, 0, -1);
        CommandChainKey firstKey = new(0, viewKey, 3, 4, false, 0);
        CommandChainKey secondKey = new(0, viewKey, 3, 4, false, 1);
        CommandChainSchedule schedule = new(
            structuralSignature: 0x100,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(firstKey, secondKey) });
        Dictionary<CommandChainKey, CommandChain> chains = new()
        {
            [firstKey] = new CommandChain(firstKey) { SourceStartIndex = 2, SourceCount = 3 },
            [secondKey] = new CommandChain(secondKey) { SourceStartIndex = 5, SourceCount = 1 },
        };

        CommandChainKey[] keysByOp = VulkanRenderer.BuildCommandChainKeysByFrameOpIndex(schedule, chains, staticOpCount: 7);

        keysByOp[0].ChainOrdinal.ShouldBe(-1);
        keysByOp[1].ChainOrdinal.ShouldBe(-1);
        keysByOp[2].ShouldBe(firstKey);
        keysByOp[3].ShouldBe(firstKey);
        keysByOp[4].ShouldBe(firstKey);
        keysByOp[5].ShouldBe(secondKey);
        keysByOp[6].ChainOrdinal.ShouldBe(-1);
    }

    [Test]
    public void ValidateCommandChainQueueSchedule_RequiresFallbackAndSidecarTimelineDependencies()
    {
        CommandChainQueueNode graphics = new(
            CommandChainQueueKind.Graphics,
            CommandChainQueueEligibility.Graphics,
            new[] { 0 },
            timelineWaitValue: 0,
            timelineSignalValue: 0,
            diagnosticLabel: "graphics");
        CommandChainQueueNode computeMissingTimeline = new(
            CommandChainQueueKind.Compute,
            CommandChainQueueEligibility.Compute,
            new[] { 1 },
            timelineWaitValue: 0,
            timelineSignalValue: 0,
            diagnosticLabel: "compute");
        CommandChainQueueNode compute = new(
            CommandChainQueueKind.Compute,
            CommandChainQueueEligibility.Compute,
            new[] { 1 },
            timelineWaitValue: 1,
            timelineSignalValue: 2,
            diagnosticLabel: "compute");
        CommandChainQueueDependency dependency = new(
            SourceNodeIndex: 1,
            DestinationNodeIndex: 0,
            TimelineSignalValue: 2,
            RequiresQueueFamilyOwnershipTransfer: true);

        CommandChainQueueSchedule missingFallback = new(
            multiQueueEnabled: false,
            singleQueueFallbackAvailable: false,
            nodes: new[] { graphics },
            dependencies: ReadOnlyMemory<CommandChainQueueDependency>.Empty,
            diagnostics: "bad");
        CommandChainQueueSchedule missingTimeline = new(
            multiQueueEnabled: true,
            singleQueueFallbackAvailable: true,
            nodes: new[] { graphics, computeMissingTimeline },
            dependencies: new[] { dependency },
            diagnostics: "bad");
        CommandChainQueueSchedule valid = new(
            multiQueueEnabled: true,
            singleQueueFallbackAvailable: true,
            nodes: new[] { graphics, compute },
            dependencies: new[] { dependency },
            diagnostics: "ok");

        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainQueueSchedule(missingFallback))
            .Message.ShouldContain("single-queue fallback");
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainQueueSchedule(missingTimeline))
            .Message.ShouldContain("timeline semaphore");
        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainQueueSchedule(valid));
    }

    [Test]
    public void ResolveCommandChainRecordingWorkerCount_HonorsSingleThreadAndDisableFlags()
    {
        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 128,
                processorCount: 16,
                singleThread: true,
                parallelDisabled: false)
            .ShouldBe(1);

        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 128,
                processorCount: 16,
                singleThread: false,
                parallelDisabled: true)
            .ShouldBe(1);
    }

    [Test]
    public void ResolveCommandChainRecordingWorkerCount_IsBoundedAndLeavesProcessorForRenderThread()
    {
        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 128,
                processorCount: 16,
                singleThread: false,
                parallelDisabled: false)
            .ShouldBe(8);

        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 3,
                processorCount: 16,
                singleThread: false,
                parallelDisabled: false)
            .ShouldBe(3);

        VulkanRenderer.ResolveCommandChainRecordingWorkerCount(
                independentChainCount: 128,
                processorCount: 2,
                singleThread: false,
                parallelDisabled: false)
            .ShouldBe(1);
    }

    [TestCase(false, false, false, false)]
    [TestCase(false, true, false, true)]
    [TestCase(false, false, true, true)]
    [TestCase(true, false, false, true)]
    public void ResolveCommandChainNeedsRecording_IncludesBenchmarkForce(
        bool benchmarkForcedRerecord,
        bool secondaryNeedsRecording,
        bool uniformSlotMappingChanged,
        bool expected)
    {
        VulkanRenderer.ResolveCommandChainNeedsRecording(
                benchmarkForcedRerecord,
                secondaryNeedsRecording,
                uniformSlotMappingChanged)
            .ShouldBe(expected);
    }

    [TestCase(false, false, false, false, true)]
    [TestCase(true, false, false, false, false)]
    [TestCase(false, true, false, false, false)]
    [TestCase(false, false, true, false, false)]
    [TestCase(false, false, false, true, false)]
    public void CanReuseCachedCommandChainSchedule_RejectsDiagnosticAndExternalModes(
        bool benchmarkForcedRerecord,
        bool validationEnabled,
        bool traceEnabled,
        bool renderingExternalSwapchainTarget,
        bool expected)
    {
        VulkanRenderer.CanReuseCachedCommandChainSchedule(
                benchmarkForcedRerecord,
                validationEnabled,
                traceEnabled,
                renderingExternalSwapchainTarget)
            .ShouldBe(expected);
    }

    [TestCase(false, false, false, false, true)]
    [TestCase(true, false, false, false, false)]
    [TestCase(false, true, false, false, false)]
    [TestCase(false, false, true, false, false)]
    [TestCase(false, false, false, true, false)]
    public void ResolveCommandChainStabilityGuardEnabled_RejectsDiagnosticModes(
        bool traceEnabled,
        bool validationEnabled,
        bool benchmarkForcedRerecord,
        bool explicitlyDisabled,
        bool expected)
    {
        VulkanRenderer.ResolveCommandChainStabilityGuardEnabled(
                traceEnabled,
                validationEnabled,
                benchmarkForcedRerecord,
                explicitlyDisabled)
            .ShouldBe(expected);
    }

    [TestCase(1, 16, false, false, false, EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork)]
    [TestCase(2, 16, false, false, false, EVulkanCommandChainWorkerEligibility.Eligible)]
    [TestCase(128, 2, false, false, false, EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork)]
    [TestCase(128, 16, true, false, false, EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork)]
    [TestCase(128, 16, false, true, false, EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork)]
    [TestCase(128, 16, false, false, true, EVulkanCommandChainWorkerEligibility.WorkerQuarantined)]
    public void ParallelCommandChainRecordingPolicy_ReturnsTypedEligibility(
        int independentChainCount,
        int processorCount,
        bool singleThread,
        bool parallelDisabled,
        bool workerDomainFaulted,
        EVulkanCommandChainWorkerEligibility expected)
    {
        VulkanRenderer.EvaluateParallelCommandChainRecording(
                independentChainCount,
                processorCount,
                singleThread,
                parallelDisabled,
                workerDomainFaulted)
            .ShouldBe(expected);
    }

    [TestCase(EVulkanCommandChainWorkerEligibility.Eligible, false)]
    [TestCase(EVulkanCommandChainWorkerEligibility.UnsupportedOperation, true)]
    [TestCase(EVulkanCommandChainWorkerEligibility.UnsupportedInheritance, true)]
    [TestCase(EVulkanCommandChainWorkerEligibility.PrimaryOwnedIndirectStream, true)]
    [TestCase(EVulkanCommandChainWorkerEligibility.MutableRendererConflict, false)]
    [TestCase(EVulkanCommandChainWorkerEligibility.WorkerQuarantined, false)]
    public void CommandChainWorkerEligibility_DistinguishesPermanentAndTransientRejections(
        EVulkanCommandChainWorkerEligibility reason,
        bool expectedPermanent)
    {
        VulkanCommandChainWorkerEligibilityResult result = new(reason);

        result.IsPermanentRejection.ShouldBe(expectedPermanent);
        result.IsEligible.ShouldBeFalse();
    }

    [Test]
    public void PersistentCommandChainWorkers_UsePreparedMutationGuardAndNoTasks()
    {
        string workers = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainWorkers.cs");
        string secondary = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        string recording = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string guard = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.PreparedWorkerRecordingScope.cs");

        workers.ShouldContain("new Thread");
        workers.ShouldContain("CommandChainWorkerWaitTimeoutMilliseconds");
        workers.ShouldContain("VulkanWorkerSecondaryCommandArena");
        workers.ShouldContain("worker.Arena.GetPool");
        workers.ShouldNotContain("GraphicsCommandPoolsByFrameSlot");
        workers.ShouldNotContain("ParallelCommandChainWorkerRecordingSafe = false");
        secondary.ShouldContain("EnterPreparedCommandChainEncodingScope");
        secondary.ShouldNotContain("EnterThreadResourcePlannerRuntimeStateScope");
        workers.ShouldNotContain("PlannerStates");
        workers.ShouldNotContain("PlannerSwitchingState");
        guard.ShouldContain("CapturePreparedWorkerPlannerStamp");
        guard.ShouldContain("mutated global resource-planner state");
        secondary.ShouldNotContain("_frameOpResourcePlannerReadbackLock");
        secondary.ShouldNotContain("Task.Run");
        recording.ShouldNotContain("Task.Run(() => RecordSecondaryAt");
    }

    private static VulkanRenderer.VulkanResourceLifetimeRecord CreateLifetimeRecord(
        ObjectType type,
        ulong handle,
        ulong generation,
        string owner)
        => new()
        {
            Key = new VulkanRenderer.VulkanResourceLifetimeKey(type, handle),
            Generation = generation,
            Owner = owner,
            State = VulkanRenderer.EVulkanResourceLifetimeState.CpuOwned,
        };

    private static void AssertGenerationSetRetirementReady(
        IReadOnlyList<VulkanRenderer.VulkanResourceLifetimeRecord> resources,
        ulong completedGraphicsSequence,
        bool expected)
    {
        for (int i = 0; i < resources.Count; i++)
        {
            VulkanRenderer.VulkanResourceLifetimeRecord resource = resources[i];
            resource.Pins.IsRetirementReady(
                    completedGraphicsSequence,
                    completedTransferSequence: 0,
                    completedOtherSequence: 0)
                .ShouldBe(expected, $"retirement readiness mismatch for {resource.Owner} generation {resource.Generation}");
        }
    }

    private static CommandChain CreateRecordedChain()
    {
        RenderPacket packet = CreatePacket(
            structuralSignature: 0x100,
            frameDataSignature: 0x200,
            resourcePlanRevision: 0x300,
            descriptorGeneration: 0x400,
            pipelineGeneration: 0x500,
            volatility: RenderPacketVolatility.FrameDataOnly);
        return CreateRecordedChain(packet);
    }

    private static CommandChain CreateRecordedChain(RenderPacket packet, int frameSlot = 0)
    {
        CommandChain chain = new(new CommandChainKey(frameSlot, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 3, 4, false, 5))
        {
            State = CommandChainState.Recorded,
            StructuralSignature = packet.StructuralSignature,
            FrameDataSignature = packet.FrameDataSignature,
            ResourcePlanRevision = packet.ResourcePlanSnapshot.Revision,
            PhysicalImageSignature = packet.ResourcePlanSnapshot.PhysicalImageSignature,
            FramebufferSignature = packet.ResourcePlanSnapshot.FramebufferSignature,
            DescriptorGeneration = packet.DescriptorSnapshot.DescriptorGeneration,
            PipelineGeneration = packet.ResourcePlanSnapshot.PipelineGeneration,
            DrawCount = packet.DrawCount,
            DispatchCount = packet.DispatchCount,
            InstanceCountSignature = VulkanRenderer.ComputePacketInstanceCountSignature(packet),
            DescriptorSetCount = packet.DescriptorSnapshot.DescriptorSetCount,
            DescriptorSetSignature = packet.DescriptorSnapshot.DescriptorSetSignature,
            SourceStartIndex = packet.SourceStartIndex,
            SourceCount = packet.SourceCount,
        };
        chain.DependencySignature = VulkanRenderer.BuildCommandChainDependencySignature(packet, chain.Key);

        return chain;
    }

    private static RenderPacket CreatePacket(
        ulong structuralSignature,
        ulong frameDataSignature,
        ulong resourcePlanRevision,
        ulong descriptorGeneration,
        ulong pipelineGeneration,
        RenderPacketVolatility volatility,
        int? descriptorSetCount = null,
        ulong? descriptorSetSignature = null,
        ulong physicalImageSignature = 0x123,
        ulong framebufferSignature = 0x456,
        DrawPacket[]? draws = null,
        DispatchPacket[]? dispatches = null)
        => new(
            viewKey: new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1),
            passIndex: 3,
            targetIdentity: 4,
            targetName: "Target",
            volatility,
            draws: draws is null ? ReadOnlyMemory<DrawPacket>.Empty : new ReadOnlyMemory<DrawPacket>(draws),
            dispatches: dispatches is null ? ReadOnlyMemory<DispatchPacket>.Empty : new ReadOnlyMemory<DispatchPacket>(dispatches),
            descriptorSnapshot: new DescriptorBindingSnapshot(
                descriptorGeneration,
                descriptorSetCount ?? (descriptorGeneration == 0 ? 0 : 1),
                descriptorSetSignature ?? descriptorGeneration),
            resourcePlanSnapshot: new ResourcePlanSnapshot(resourcePlanRevision, physicalImageSignature, framebufferSignature, pipelineGeneration),
            structuralSignature,
            frameDataSignature,
            sourceStartIndex: 5,
            sourceCount: 1,
            dynamicOverlay: false);

    private static CommandChainSchedule CreateSchedule(bool dynamicOverlay, int chainCount)
        => new(
            structuralSignature: dynamicOverlay ? 0x101UL : 0x100UL,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroup(passIndex: dynamicOverlay ? 9 : 3, targetIdentity: 4, dynamicOverlay, chainCount) });

    private static RenderPassChainGroup CreateGroup(int passIndex, int targetIdentity, bool dynamicOverlay, int chainCount)
    {
        CommandChainKey[] keys = new CommandChainKey[chainCount];
        RenderViewKey viewKey = new(1, 2, 0, dynamicOverlay ? RenderViewKind.Overlay : RenderViewKind.Main, 0, -1);
        for (int i = 0; i < keys.Length; i++)
            keys[i] = new CommandChainKey(0, viewKey, passIndex, targetIdentity, dynamicOverlay, i);

        return new RenderPassChainGroup(
            passIndex,
            targetIdentity,
            targetIdentity == 0 ? "<swapchain>" : "Target",
            keys,
            structuralSignature: unchecked(0x500UL + (ulong)chainCount + (dynamicOverlay ? 0x1000UL : 0UL)),
            supportsSecondaryCommandBuffers: true,
            dynamicOverlay);
    }

    private static RenderPassChainGroup CreateGroupForKeys(params CommandChainKey[] keys)
        => new(
            keys.Length == 0 ? 0 : keys[0].PassIndex,
            keys.Length == 0 ? 0 : keys[0].TargetIdentity,
            keys.Length == 0 || keys[0].TargetIdentity == 0 ? "<swapchain>" : "Target",
            keys,
            structuralSignature: unchecked(0x600UL + (ulong)keys.Length),
            supportsSecondaryCommandBuffers: true,
            dynamicOverlay: false);

    private static MeshDrawOp CreateMeshDrawOp(
        PendingMeshDraw draw,
        int passIndex = 0,
        FrameOpContext? context = null)
        => new(
            passIndex,
            Target: null,
            draw,
            context ?? CreateFrameOpContext());

    private static ComputeDispatchOp CreateComputeDispatchOp(
        int passIndex = 0,
        FrameOpContext? context = null)
        => new(
            passIndex,
            Program: null!,
            GroupsX: 1,
            GroupsY: 1,
            GroupsZ: 1,
            Snapshot: new ComputeDispatchSnapshot(
                new Dictionary<string, ProgramUniformValue>(),
                new Dictionary<uint, XRTexture>(),
                new Dictionary<uint, string>(),
                new Dictionary<string, XRTexture>(),
                new Dictionary<uint, ProgramImageBinding>(),
                new Dictionary<uint, XRDataBuffer>()),
            context ?? CreateFrameOpContext());

    private static DrawPacket CreateDrawPacket(uint instanceCount)
        => new(
            OpIndex: 0,
            RendererIdentity: 1,
            MeshIdentity: 2,
            MaterialIdentity: 3,
            ProgramIdentity: 4,
            InstanceCount: instanceCount,
            Transparent: false,
            StructuralSignature: 0x10,
            FrameDataSignature: 0x20);

    private static ClearOp CreateClearOp(int passIndex)
        => new(
            PassIndex: passIndex,
            Target: null,
            ClearColor: true,
            ClearDepth: true,
            ClearStencil: false,
            Color: default,
            Depth: 1.0f,
            Stencil: 0,
            Rect: default,
            Context: CreateFrameOpContext());

    private static FrameOpContext CreateFrameOpContext(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null,
        int outputTargetIdentity = 0,
        string? outputTargetName = null)
        => new(
            PipelineIdentity: 1,
            ViewportIdentity: 2,
            PipelineInstance: null,
            ResourceRegistry: null,
            PassMetadata: passMetadata,
            DisplayWidth: 1920,
            DisplayHeight: 1080,
            InternalWidth: 1920,
            InternalHeight: 1080,
            OutputTargetIdentity: outputTargetIdentity,
            OutputTargetName: outputTargetName);

    private static string ReadOpenXrVulkanRendererSources()
    {
        const string relativeDirectory = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR";
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string platformPath = relativeDirectory.Replace('/', Path.DirectorySeparatorChar);

        while (dir is not null)
        {
            string fullPath = Path.Combine(dir.FullName, platformPath);
            if (Directory.Exists(fullPath))
            {
                return string.Join(
                    "\n",
                    Directory.GetFiles(fullPath, "VulkanRenderer*.cs")
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .Select(File.ReadAllText))
                    .Replace("\r\n", "\n");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve OpenXR Vulkan renderer sources from test base directory '{AppContext.BaseDirectory}'.");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadPartialType(relativePath);
}
