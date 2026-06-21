using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
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
        VulkanRenderer.MeshDrawOp leftOp = CreateMeshDrawOp(default(VulkanRenderer.PendingMeshDraw) with { Camera = leftCamera });
        VulkanRenderer.MeshDrawOp rightOp = CreateMeshDrawOp(default(VulkanRenderer.PendingMeshDraw) with { Camera = rightCamera });

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
        VulkanRenderer.MeshDrawOp op = CreateMeshDrawOp(default(VulkanRenderer.PendingMeshDraw) with { IsStereoPass = true });

        RenderViewKey key = VulkanRenderer.BuildRenderViewKey(op, dynamicOverlay: false);

        key.Kind.ShouldBe(RenderViewKind.VREye);
        key.ViewIndex.ShouldBe(VulkanRenderer.CommandChainStereoMultiviewViewIndex);
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
        VulkanRenderer.MeshDrawOp op = CreateMeshDrawOp(
            default(VulkanRenderer.PendingMeshDraw) with { ShadowUniformState = shadowState },
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
        CommandChainKey slotZero = new(0, view, 3, 4, 5);
        CommandChainKey slotOne = slotZero with { FrameSlot = 1 };
        CommandChainKey differentOrdinal = slotZero with { ChainOrdinal = 6 };

        slotZero.ShouldNotBe(slotOne);
        slotZero.ShouldNotBe(differentOrdinal);
        slotZero.ShouldBe(new CommandChainKey(0, view, 3, 4, 5));
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
        VulkanRenderer.FrameOpContext context = CreateFrameOpContext();
        VulkanRenderer.ClearOp clear = new(
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
        VulkanRenderer.MemoryBarrierOp barrier = new(
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
        VulkanRenderer.FrameOpContext context = CreateFrameOpContext(passMetadata: [overlayPass]);
        VulkanRenderer.ClearOp clear = new(
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
        VulkanRenderer.ClearOp clear = new(
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
            .ShouldBe(CommandChainDirtyReason.ResourcePlan);
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
            .ShouldBe(CommandChainDirtyReason.Structure);
    }

    [Test]
    public void CommandChainDirtyReason_UnrecordedChain_DirtiesStructure()
    {
        CommandChain chain = new(new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 3, 4, 5));
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
                recordedResourcePlanRevision: schedule.ResourcePlanRevision,
                recordedProfilerActive: false,
                recordedProfilerFrameSlot: -1,
                currentProfilerActive: false,
                currentProfilerFrameSlot: 0)
            .ShouldBe(PrimaryCommandBufferDirtyReason.None);
    }

    [Test]
    public void PrimaryCommandBufferDirtyReason_SeparatesScheduleResourceAndProfilerChanges()
    {
        CommandChainSchedule schedule = CreateSchedule(dynamicOverlay: false, chainCount: 2);

        VulkanRenderer.EvaluatePrimaryCommandBufferDirtyReason(
                schedule,
                recordedScheduleSignature: schedule.StructuralSignature + 1,
                recordedGroupSignature: VulkanRenderer.ComputePrimaryCommandBufferGroupSignature(schedule) + 1,
                recordedGroupCount: schedule.Groups.Length + 1,
                recordedResourcePlanRevision: schedule.ResourcePlanRevision + 1,
                recordedProfilerActive: false,
                recordedProfilerFrameSlot: -1,
                currentProfilerActive: true,
                currentProfilerFrameSlot: 0)
            .ShouldBe(
                PrimaryCommandBufferDirtyReason.ScheduleStructure |
                PrimaryCommandBufferDirtyReason.GroupStructure |
                PrimaryCommandBufferDirtyReason.ResourcePlan |
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
    public void ValidatePrimaryCommandChainSchedule_RequiresStaticGroupsBeforeOverlayGroups()
    {
        VulkanRenderer.ClearOp firstStatic = CreateClearOp(passIndex: 0);
        VulkanRenderer.ClearOp secondStatic = CreateClearOp(passIndex: 0);
        CommandChainSchedule valid = new(
            structuralSignature: 0x100,
            resourcePlanRevision: 0x200,
            groups: new[]
            {
                CreateGroup(passIndex: 0, targetIdentity: 0, dynamicOverlay: false, chainCount: 2),
                CreateGroup(passIndex: 10, targetIdentity: 0, dynamicOverlay: true, chainCount: 1),
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
        CommandChainSchedule validVr = new(
            structuralSignature: 0x100,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, leftEye, 0, 0, 0), new CommandChainKey(0, rightEye, 0, 0, 1)) });
        CommandChainSchedule invalidVr = new(
            structuralSignature: 0x101,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, rightEye, 0, 0, 0), new CommandChainKey(0, leftEye, 0, 0, 1)) });
        RenderViewKey invalidShadow = new(1, 2, 0, RenderViewKind.Shadow, 0, -1);
        CommandChainSchedule invalidShadowSchedule = new(
            structuralSignature: 0x102,
            resourcePlanRevision: 0x200,
            groups: new[] { CreateGroupForKeys(new CommandChainKey(0, invalidShadow, 0, 0, 0)) });

        Should.NotThrow(() => VulkanRenderer.ValidateCommandChainViewSpecialization(validVr));
        Should.Throw<InvalidOperationException>(() => VulkanRenderer.ValidateCommandChainViewSpecialization(invalidVr))
            .Message.ShouldContain("left eye before right eye");
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
            new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 0, 3, 0),
            new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 0, 3, 1));
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

    private static CommandChain CreateRecordedChain(RenderPacket packet)
    {
        CommandChain chain = new(new CommandChainKey(0, new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1), 3, 4, 5))
        {
            State = CommandChainState.Recorded,
            StructuralSignature = packet.StructuralSignature,
            FrameDataSignature = packet.FrameDataSignature,
            ResourcePlanRevision = packet.ResourcePlanSnapshot.Revision,
            PhysicalImageSignature = packet.ResourcePlanSnapshot.PhysicalImageSignature,
            FramebufferSignature = packet.ResourcePlanSnapshot.FramebufferSignature,
            DescriptorGeneration = packet.DescriptorSnapshot.DescriptorGeneration,
            PipelineGeneration = packet.ResourcePlanSnapshot.PipelineGeneration,
            DrawCount = packet.Draws.Length,
            DispatchCount = packet.Dispatches.Length,
            InstanceCountSignature = VulkanRenderer.ComputePacketInstanceCountSignature(packet),
            DescriptorSetCount = packet.DescriptorSnapshot.DescriptorSetCount,
            DescriptorSetSignature = packet.DescriptorSnapshot.DescriptorSetSignature,
        };

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
        DrawPacket[]? draws = null)
        => new(
            viewKey: new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1),
            passIndex: 3,
            targetIdentity: 4,
            targetName: "Target",
            volatility,
            draws: draws is null ? ReadOnlyMemory<DrawPacket>.Empty : new ReadOnlyMemory<DrawPacket>(draws),
            dispatches: ReadOnlyMemory<DispatchPacket>.Empty,
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
            keys[i] = new CommandChainKey(0, viewKey, passIndex, targetIdentity, i);

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

    private static VulkanRenderer.MeshDrawOp CreateMeshDrawOp(
        VulkanRenderer.PendingMeshDraw draw,
        int passIndex = 0,
        VulkanRenderer.FrameOpContext? context = null)
        => new(
            passIndex,
            Target: null,
            draw,
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

    private static VulkanRenderer.ClearOp CreateClearOp(int passIndex)
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

    private static VulkanRenderer.FrameOpContext CreateFrameOpContext(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
        => new(
            PipelineIdentity: 1,
            ViewportIdentity: 2,
            PipelineInstance: null,
            ResourceRegistry: null,
            PassMetadata: passMetadata,
            DisplayWidth: 1920,
            DisplayHeight: 1080,
            InternalWidth: 1920,
            InternalHeight: 1080);
}
