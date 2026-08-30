using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedRenderPipelineResourceStateContractTests
{
    [Test]
    public void OwnershipContract_DefinesEveryAllocationAndLifetimeClass()
    {
        IReadOnlyList<AdvancedRenderResourceOwnershipDescriptor> definitions =
            AdvancedRenderResourceOwnershipContract.Ordered;

        definitions.Select(static item => item.Ownership).ShouldBe(
        [
            EAdvancedRenderResourceOwnership.PipelinePersistent,
            EAdvancedRenderResourceOwnership.FrameSlotTransient,
            EAdvancedRenderResourceOwnership.TemporalHistory,
            EAdvancedRenderResourceOwnership.Imported,
            EAdvancedRenderResourceOwnership.External,
        ]);

        AdvancedRenderResourceOwnershipDescriptor persistent =
            AdvancedRenderResourceOwnershipContract.Get(
                EAdvancedRenderResourceOwnership.PipelinePersistent);
        persistent.RuntimeLifetime.ShouldBe(RenderResourceLifetime.Persistent);
        persistent.PipelineAllocates.ShouldBeTrue();
        persistent.PipelineDisposes.ShouldBeTrue();
        persistent.ReplicatedPerFrameSlot.ShouldBeFalse();

        AdvancedRenderResourceOwnershipDescriptor frameSlot =
            AdvancedRenderResourceOwnershipContract.Get(
                EAdvancedRenderResourceOwnership.FrameSlotTransient);
        frameSlot.RuntimeLifetime.ShouldBe(RenderResourceLifetime.Transient);
        frameSlot.ReplicatedPerFrameSlot.ShouldBeTrue();
        frameSlot.RequiresOwnerSynchronization.ShouldBeTrue();

        AdvancedRenderResourceOwnershipDescriptor history =
            AdvancedRenderResourceOwnershipContract.Get(
                EAdvancedRenderResourceOwnership.TemporalHistory);
        history.RuntimeLifetime.ShouldBe(RenderResourceLifetime.Persistent);
        history.RotatesHistory.ShouldBeTrue();
        history.RequiresOwnerSynchronization.ShouldBeTrue();

        foreach (EAdvancedRenderResourceOwnership ownership in new[]
                 {
                     EAdvancedRenderResourceOwnership.Imported,
                     EAdvancedRenderResourceOwnership.External,
                 })
        {
            AdvancedRenderResourceOwnershipDescriptor external =
                AdvancedRenderResourceOwnershipContract.Get(ownership);
            external.RuntimeLifetime.ShouldBe(RenderResourceLifetime.External);
            external.PipelineAllocates.ShouldBeFalse();
            external.PipelineDisposes.ShouldBeFalse();
            external.RequiresExplicitBinding.ShouldBeTrue();
            external.RequiresOwnerSynchronization.ShouldBeTrue();
        }
    }

    [Test]
    public void VisibilityPipeline_CapturesCapabilityEncodingsAndPhase04Capacities()
    {
        AdvancedRenderPipelineCapabilityResult capabilityResult =
            AdvancedRenderPipelineCapabilityResolver.Resolve(
                AdvancedRenderPipelineCapabilityTests.SupportedCapabilities,
                stereo: false);
        AdvancedRenderPipeline pipeline = new(
            stereo: false,
            capabilityResult,
            visibilityFamilyReservation: default);
        RenderPipelineResourceProfile target = CreateTargetProfile();

        AdvancedRenderResourceProfile profile =
            pipeline.CaptureAdvancedResourceProfile(target);

        profile.ContractVersion.ShouldBe(
            AdvancedRenderResourceProfile.CurrentContractVersion);
        profile.Target.ShouldBe(target);
        profile.FrameSlotCount.ShouldBe(
            AdvancedFrameSlotContract.DefaultSlotCount);
        profile.VisibilityTargetEncoding.ShouldBe(
            EAdvancedVisibilityTargetEncoding.R32G32UInt);
        profile.IndirectSubmission.ShouldBe(
            EAdvancedIndirectSubmissionMode.MultiDrawIndirectCount);
        profile.TextureIndirection.ShouldBe(
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing);
        profile.Synchronization.ShouldBe(
            EAdvancedSynchronizationMode.VulkanSynchronization2);
        profile.ShaderFamily.ShouldBe(EAdvancedShaderFamily.VisibilityBuffer);
        profile.Capacities.ShouldBe(
            AdvancedRenderCapacityProfile.VisibilityBuffer);
        profile.ToGenerationKey().Profile.ShouldBe(profile);
    }

    [Test]
    public void ResourceGenerationKey_ContainsEveryLayoutAffectingProfileField()
    {
        RenderPipelineResourceProfile target = CreateTargetProfile();
        AdvancedRenderCapacityProfile capacities = new(
            DrawRecords: 1u,
            InstanceRecords: 2u,
            GeometryRecords: 3u,
            MaterialRecords: 4u,
            LightRecords: 5u,
            DecalRecords: 6u,
            DeformedVertices: 7u,
            VisiblePrimitives: 8u,
            MaterialWorkItems: 9u,
            Froxels: 10u,
            TransparencyNodes: 11u);
        AdvancedRenderResourceProfile profile = new(
            ContractVersion: 1u,
            target,
            FrameSlotCount: 3u,
            EAdvancedVisibilityTargetEncoding.R32G32UInt,
            EAdvancedIndirectSubmissionMode.MultiDrawIndirectCount,
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing,
            EAdvancedSynchronizationMode.VulkanSynchronization2,
            EAdvancedShaderFamily.VisibilityBuffer,
            capacities);
        AdvancedRenderResourceGenerationKey baseline = profile.ToGenerationKey();

        AdvancedRenderResourceProfile[] variations =
        [
            profile with { ContractVersion = 2u },
            profile with { Target = target with { DisplayWidth = target.DisplayWidth + 1u } },
            profile with { Target = target with { DisplayHeight = target.DisplayHeight + 1u } },
            profile with { Target = target with { InternalWidth = target.InternalWidth + 1u } },
            profile with { Target = target with { InternalHeight = target.InternalHeight + 1u } },
            profile with { Target = target with { OutputHDR = !target.OutputHDR } },
            profile with { Target = target with { AntiAliasingMode = EAntiAliasingMode.Msaa } },
            profile with { Target = target with { MsaaSampleCount = 4u } },
            profile with { Target = target with { Stereo = !target.Stereo } },
            profile with { Target = target with { FeatureMask = target.FeatureMask + 1UL } },
            profile with
            {
                Target = target with
                {
                    ExternalTargetKind = RenderPipelineExternalTargetKind.Window,
                },
            },
            profile with { Target = target with { ViewCount = target.ViewCount + 1u } },
            profile with { Target = target with { ViewIndex = target.ViewIndex + 1u } },
            profile with { FrameSlotCount = 4u },
            profile with
            {
                VisibilityTargetEncoding = EAdvancedVisibilityTargetEncoding.None,
            },
            profile with
            {
                IndirectSubmission = EAdvancedIndirectSubmissionMode.MultiDrawIndirect,
            },
            profile with
            {
                TextureIndirection = EAdvancedTextureIndirectionMode.TextureArray,
            },
            profile with
            {
                Synchronization = EAdvancedSynchronizationMode.VulkanLegacyBarriers,
            },
            profile with { ShaderFamily = EAdvancedShaderFamily.None },
            profile with
            {
                Capacities = capacities with { DrawRecords = capacities.DrawRecords + 100u },
            },
            profile with
            {
                Capacities = capacities with { InstanceRecords = capacities.InstanceRecords + 100u },
            },
            profile with
            {
                Capacities = capacities with { GeometryRecords = capacities.GeometryRecords + 100u },
            },
            profile with
            {
                Capacities = capacities with { MaterialRecords = capacities.MaterialRecords + 100u },
            },
            profile with
            {
                Capacities = capacities with { LightRecords = capacities.LightRecords + 100u },
            },
            profile with
            {
                Capacities = capacities with { DecalRecords = capacities.DecalRecords + 100u },
            },
            profile with
            {
                Capacities = capacities with { DeformedVertices = capacities.DeformedVertices + 100u },
            },
            profile with
            {
                Capacities = capacities with { VisiblePrimitives = capacities.VisiblePrimitives + 100u },
            },
            profile with
            {
                Capacities = capacities with { MaterialWorkItems = capacities.MaterialWorkItems + 100u },
            },
            profile with
            {
                Capacities = capacities with { Froxels = capacities.Froxels + 100u },
            },
            profile with
            {
                Capacities = capacities with { TransparencyNodes = capacities.TransparencyNodes + 100u },
            },
        ];

        foreach (AdvancedRenderResourceProfile variation in variations)
            variation.ToGenerationKey().ShouldNotBe(baseline);

        variations
            .Select(static variation => variation.ToGenerationKey())
            .Distinct()
            .Count()
            .ShouldBe(variations.Length);
    }

    [Test]
    public void FrameSlots_RotateCurrentAndPreviousWithoutAliasing()
    {
        AdvancedFrameSlotContract.Resolve(0UL, 3u)
            .ShouldBe(new AdvancedFrameSlotPair(Current: 0u, Previous: 2u));
        AdvancedFrameSlotContract.Resolve(1UL, 3u)
            .ShouldBe(new AdvancedFrameSlotPair(Current: 1u, Previous: 0u));
        AdvancedFrameSlotContract.Resolve(2UL, 3u)
            .ShouldBe(new AdvancedFrameSlotPair(Current: 2u, Previous: 1u));
        AdvancedFrameSlotContract.Resolve(3UL, 3u)
            .ShouldBe(new AdvancedFrameSlotPair(Current: 0u, Previous: 2u));

        Should.Throw<ArgumentOutOfRangeException>(
            () => AdvancedFrameSlotContract.Resolve(0UL, 1u));
    }

    [Test]
    public void FrameSlots_RequireFenceOrTimelineCompletionBeforeReuse()
    {
        AdvancedFrameSlotContract.CanReuse(
            lastSubmittedCompletionValue: 0UL,
            completedValue: 0UL).ShouldBeTrue();
        AdvancedFrameSlotContract.CanReuse(
            lastSubmittedCompletionValue: 9UL,
            completedValue: 8UL).ShouldBeFalse();
        AdvancedFrameSlotContract.CanReuse(
            lastSubmittedCompletionValue: 9UL,
            completedValue: 9UL).ShouldBeTrue();

        AdvancedRenderPipelineCapabilities openGl =
            AdvancedRenderPipelineCapabilityTests.SupportedCapabilities with
            {
                Backend = RuntimeGraphicsApiKind.OpenGL,
                Synchronization = EAdvancedSynchronizationMode.OpenGlMemoryBarrier,
                SupportsTimelineSemaphores = false,
            };
        AdvancedFrameSlotContract.ResolveCompletionMode(openGl)
            .ShouldBe(EAdvancedFrameSlotCompletionMode.OpenGlFence);

        AdvancedFrameSlotContract.ResolveCompletionMode(
                AdvancedRenderPipelineCapabilityTests.SupportedCapabilities)
            .ShouldBe(EAdvancedFrameSlotCompletionMode.VulkanTimelineSemaphore);

        AdvancedRenderPipelineCapabilities vulkanFence =
            AdvancedRenderPipelineCapabilityTests.SupportedCapabilities with
            {
                SupportsTimelineSemaphores = false,
            };
        AdvancedFrameSlotContract.ResolveCompletionMode(vulkanFence)
            .ShouldBe(EAdvancedFrameSlotCompletionMode.VulkanFence);

        AdvancedFrameSlotContract.ResolveCompletionMode(
                vulkanFence with { SupportsFrameSlotStorage = false })
            .ShouldBe(EAdvancedFrameSlotCompletionMode.None);
    }

    [Test]
    public void SynchronizationContract_CoversEveryCrossDomainBoundary()
    {
        IReadOnlyList<AdvancedSynchronizationBoundaryDescriptor> boundaries =
            AdvancedSynchronizationContract.Ordered;

        boundaries.Select(static item => item.Boundary).ShouldBe(
        [
            EAdvancedSynchronizationBoundary.ComputePreparationToVisibilityRaster,
            EAdvancedSynchronizationBoundary.VisibilityRasterToComputeShading,
            EAdvancedSynchronizationBoundary.ComputeShadingToLateGraphics,
            EAdvancedSynchronizationBoundary.LateGraphicsToPresentation,
        ]);

        AdvancedSynchronizationBoundaryDescriptor preparation = boundaries[0];
        preparation.ProducerStage.ShouldBe(
            EAdvancedRenderStage.VisibilityPreparation);
        preparation.ConsumerStage.ShouldBe(EAdvancedRenderStage.VisibilityRaster);
        preparation.ProducerState.StageMask.ShouldBe(
            RenderGraphStageMask.ComputeShader);
        preparation.ConsumerState.StageMask
            .ShouldBe(
                RenderGraphStageMask.DrawIndirect |
                RenderGraphStageMask.VertexInput |
                RenderGraphStageMask.VertexShader);
        preparation.OpenGlBarrierMask.ShouldBe(
            EAdvancedOpenGlMemoryBarrier.Command |
            EAdvancedOpenGlMemoryBarrier.VertexAttributeArray |
            EAdvancedOpenGlMemoryBarrier.ElementArray |
            EAdvancedOpenGlMemoryBarrier.ShaderStorage);

        AdvancedSynchronizationBoundaryDescriptor visibility = boundaries[1];
        visibility.ConsumerStage.ShouldBe(
            EAdvancedRenderStage.DepthPyramidAndLateVisibility);
        visibility.ProducerState.AccessMask
            .ShouldBe(
                RenderGraphAccessMask.ColorAttachmentWrite |
                RenderGraphAccessMask.DepthStencilWrite);
        visibility.ConsumerState.StageMask.ShouldBe(
            RenderGraphStageMask.ComputeShader);
        visibility.OpenGlBarrierMask
            .HasFlag(EAdvancedOpenGlMemoryBarrier.FrameBuffer)
            .ShouldBeTrue();

        AdvancedSynchronizationBoundaryDescriptor shading = boundaries[2];
        shading.ProducerStage.ShouldBe(
            EAdvancedRenderStage.NativeOpaqueShading);
        shading.ConsumerStage.ShouldBe(EAdvancedRenderStage.LatePasses);
        shading.OpenGlBarrierMask
            .HasFlag(EAdvancedOpenGlMemoryBarrier.ShaderImageAccess)
            .ShouldBeTrue();

        AdvancedSynchronizationBoundaryDescriptor presentation = boundaries[3];
        presentation.ProducerStage.ShouldBe(EAdvancedRenderStage.UserInterface);
        presentation.ConsumerStage.ShouldBeNull();
        presentation.ConsumerState.Layout.ShouldBe(RenderGraphImageLayout.Present);

        AdvancedSynchronizationContract.IsEncodingCompatible(
            RuntimeGraphicsApiKind.OpenGL,
            EAdvancedSynchronizationMode.OpenGlMemoryBarrier).ShouldBeTrue();
        AdvancedSynchronizationContract.IsEncodingCompatible(
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedSynchronizationMode.VulkanLegacyBarriers).ShouldBeTrue();
        AdvancedSynchronizationContract.IsEncodingCompatible(
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedSynchronizationMode.VulkanSynchronization2).ShouldBeTrue();
        AdvancedSynchronizationContract.IsEncodingCompatible(
            RuntimeGraphicsApiKind.OpenGL,
            EAdvancedSynchronizationMode.VulkanSynchronization2).ShouldBeFalse();
        AdvancedSynchronizationContract.IsEncodingCompatible(
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedSynchronizationMode.OpenGlMemoryBarrier).ShouldBeFalse();
    }

    [Test]
    public void CommandPacketReuse_InvalidatesOnlyTheFiveStructuralGenerations()
    {
        AdvancedCommandPacketGeneration command = new(
            Topology: 1UL,
            Capacity: 2UL,
            Binding: 3UL,
            Shader: 4UL,
            Resource: 5UL);
        AdvancedCommandPacketState recorded = new(
            command,
            new AdvancedFrameDataGeneration(
                Counts: 10UL,
                Visibility: 11UL,
                Transforms: 12UL,
                Materials: 13UL));

        var structuralChanges = new[]
        {
            (
                command with { Topology = command.Topology + 1UL },
                EAdvancedCommandPacketInvalidation.Topology),
            (
                command with { Capacity = command.Capacity + 1UL },
                EAdvancedCommandPacketInvalidation.Capacity),
            (
                command with { Binding = command.Binding + 1UL },
                EAdvancedCommandPacketInvalidation.Binding),
            (
                command with { Shader = command.Shader + 1UL },
                EAdvancedCommandPacketInvalidation.Shader),
            (
                command with { Resource = command.Resource + 1UL },
                EAdvancedCommandPacketInvalidation.Resource),
        };

        foreach (var change in structuralChanges)
        {
            AdvancedCommandPacketState current = recorded with
            {
                CommandPacket = change.Item1,
            };
            AdvancedCommandPacketReuseContract.GetInvalidation(recorded, current)
                .ShouldBe(change.Item2);
            AdvancedCommandPacketReuseContract.CanReuse(recorded, current)
                .ShouldBeFalse();
        }
    }

    [Test]
    public void CommandPacketReuse_IgnoresGpuWrittenCountsVisibilityTransformsAndMaterials()
    {
        AdvancedCommandPacketState recorded = new(
            new AdvancedCommandPacketGeneration(
                Topology: 1UL,
                Capacity: 2UL,
                Binding: 3UL,
                Shader: 4UL,
                Resource: 5UL),
            new AdvancedFrameDataGeneration(
                Counts: 10UL,
                Visibility: 11UL,
                Transforms: 12UL,
                Materials: 13UL));
        AdvancedCommandPacketState current = recorded with
        {
            FrameData = new AdvancedFrameDataGeneration(
                Counts: 100UL,
                Visibility: 101UL,
                Transforms: 102UL,
                Materials: 103UL),
        };

        AdvancedCommandPacketReuseContract.GetInvalidation(recorded, current)
            .ShouldBe(EAdvancedCommandPacketInvalidation.None);
        AdvancedCommandPacketReuseContract.CanReuse(recorded, current)
            .ShouldBeTrue();
    }

    private static RenderPipelineResourceProfile CreateTargetProfile()
        => new(
            DisplayWidth: 1920u,
            DisplayHeight: 1080u,
            InternalWidth: 1440u,
            InternalHeight: 810u,
            OutputHDR: true,
            AntiAliasingMode: EAntiAliasingMode.None,
            MsaaSampleCount: 1u,
            Stereo: false,
            FeatureMask: 42UL,
            ExternalTargetKind: RenderPipelineExternalTargetKind.None,
            ViewCount: 1u,
            ViewIndex: 0u);
}
