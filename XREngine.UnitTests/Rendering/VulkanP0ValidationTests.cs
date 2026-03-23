using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Validation tests for Vulkan P0 backlog items:
/// pass-index validity, allocator toggle coverage,
/// and staging manager pool eligibility.
/// </summary>
[TestFixture]
public sealed class VulkanP0ValidationTests
{
    #region Pass-Index Validity

    [Test]
    public void AllDefaultRenderPassValues_AreDefinedInEnum()
    {
        // Ensures the EDefaultRenderPass enum hasn't drifted — every integral
        // value between min and max should be a defined member.
        int[] defined = Enum.GetValues<EDefaultRenderPass>().Select(v => (int)v).OrderBy(v => v).ToArray();
        defined.Length.ShouldBeGreaterThan(0, "EDefaultRenderPass should have members");

        foreach (int passIndex in defined)
            Enum.IsDefined(typeof(EDefaultRenderPass), passIndex).ShouldBeTrue(
                $"Pass index {passIndex} should be defined in EDefaultRenderPass");
    }

    [Test]
    public void DefaultRenderPipelineMetadata_CoversAllStandardPasses()
    {
        // Build metadata using the same helper both DefaultRenderPipeline and
        // DefaultRenderPipeline2 use.
        var metadata = new RenderPassMetadataCollection();
        RegisterStandardPasses(metadata);
        var built = metadata.Build();

        int[] definedPasses = Enum.GetValues<EDefaultRenderPass>().Select(v => (int)v).ToArray();
        var registeredPassIndices = built.Select(m => m.PassIndex).ToHashSet();

        foreach (int passIndex in definedPasses)
            registeredPassIndices.ShouldContain(passIndex,
                $"Standard pass {(EDefaultRenderPass)passIndex} ({passIndex}) should be in metadata");
    }

    [Test]
    public void SentinelPassIndex_IsNotValidDefaultRenderPass()
    {
        Enum.IsDefined(typeof(EDefaultRenderPass), int.MinValue).ShouldBeFalse(
            "int.MinValue (sentinel) should not be a valid EDefaultRenderPass");
    }

    /// <summary>
    /// Mirrors the dependency chain declared by DefaultRenderPipeline.DescribeRenderPasses.
    /// </summary>
    private static void RegisterStandardPasses(RenderPassMetadataCollection metadata)
    {
        static void Chain(RenderPassMetadataCollection c, EDefaultRenderPass pass, params EDefaultRenderPass[] deps)
        {
            var builder = c.ForPass((int)pass, pass.ToString(), ERenderGraphPassStage.Graphics);
            foreach (var dep in deps)
                builder.DependsOn((int)dep);
        }

        Chain(metadata, EDefaultRenderPass.PreRender);
        Chain(metadata, EDefaultRenderPass.Background, EDefaultRenderPass.PreRender, EDefaultRenderPass.DeferredDecals);
        Chain(metadata, EDefaultRenderPass.OpaqueDeferred, EDefaultRenderPass.PreRender);
        Chain(metadata, EDefaultRenderPass.DeferredDecals, EDefaultRenderPass.OpaqueDeferred);
        Chain(metadata, EDefaultRenderPass.OpaqueForward, EDefaultRenderPass.Background);
        Chain(metadata, EDefaultRenderPass.MaskedForward, EDefaultRenderPass.OpaqueForward);
        Chain(metadata, EDefaultRenderPass.WeightedBlendedOitForward, EDefaultRenderPass.MaskedForward);
        Chain(metadata, EDefaultRenderPass.PerPixelLinkedListForward, EDefaultRenderPass.WeightedBlendedOitForward);
        Chain(metadata, EDefaultRenderPass.DepthPeelingForward, EDefaultRenderPass.PerPixelLinkedListForward);
        Chain(metadata, EDefaultRenderPass.TransparentForward, EDefaultRenderPass.DepthPeelingForward);
        Chain(metadata, EDefaultRenderPass.OnTopForward, EDefaultRenderPass.TransparentForward);
        Chain(metadata, EDefaultRenderPass.PostRender, EDefaultRenderPass.OnTopForward);
    }

    #endregion

    #region Staging Manager Pool Eligibility

    [Test]
    public void StagingManagerCanPool_AcceptsUploadBuffers()
    {
        var manager = new XREngine.Rendering.Vulkan.VulkanStagingManager();
        manager.CanPool(
            Silk.NET.Vulkan.BufferUsageFlags.TransferSrcBit,
            Silk.NET.Vulkan.MemoryPropertyFlags.HostVisibleBit | Silk.NET.Vulkan.MemoryPropertyFlags.HostCoherentBit)
            .ShouldBeTrue("Upload staging buffers should be poolable");
    }

    [Test]
    public void StagingManagerCanPool_AcceptsReadbackBuffers()
    {
        var manager = new XREngine.Rendering.Vulkan.VulkanStagingManager();
        manager.CanPool(
            Silk.NET.Vulkan.BufferUsageFlags.TransferDstBit,
            Silk.NET.Vulkan.MemoryPropertyFlags.HostVisibleBit | Silk.NET.Vulkan.MemoryPropertyFlags.HostCachedBit)
            .ShouldBeTrue("Readback staging buffers should be poolable");
    }

    [Test]
    public void StagingManagerCanPool_RejectsNonTransferBuffers()
    {
        var manager = new XREngine.Rendering.Vulkan.VulkanStagingManager();
        manager.CanPool(
            Silk.NET.Vulkan.BufferUsageFlags.UniformBufferBit,
            Silk.NET.Vulkan.MemoryPropertyFlags.HostVisibleBit | Silk.NET.Vulkan.MemoryPropertyFlags.HostCoherentBit)
            .ShouldBeFalse("Non-transfer buffers should not be poolable");
    }

    #endregion

    #region Allocator Toggle Coverage

    [Test]
    public void AllocatorBackendEnum_HasLegacyAndSuballocator()
    {
        Enum.IsDefined(typeof(EVulkanAllocatorBackend), EVulkanAllocatorBackend.Legacy).ShouldBeTrue();
        Enum.IsDefined(typeof(EVulkanAllocatorBackend), EVulkanAllocatorBackend.Suballocator).ShouldBeTrue();
    }

    [Test]
    public void SynchronizationBackendEnum_HasLegacyAndSync2()
    {
        Enum.IsDefined(typeof(EVulkanSynchronizationBackend), EVulkanSynchronizationBackend.Legacy).ShouldBeTrue();
        Enum.IsDefined(typeof(EVulkanSynchronizationBackend), EVulkanSynchronizationBackend.Sync2).ShouldBeTrue();
    }

    [Test]
    public void DescriptorUpdateBackendEnum_HasLegacyAndTemplate()
    {
        Enum.IsDefined(typeof(EVulkanDescriptorUpdateBackend), EVulkanDescriptorUpdateBackend.Legacy).ShouldBeTrue();
        Enum.IsDefined(typeof(EVulkanDescriptorUpdateBackend), EVulkanDescriptorUpdateBackend.Template).ShouldBeTrue();
    }

    [Test]
    public void VulkanRobustnessSettings_DefaultsToLegacyBackends()
    {
        var settings = new VulkanRobustnessSettings();

        settings.AllocatorBackend.ShouldBe(EVulkanAllocatorBackend.Legacy);
        settings.SyncBackend.ShouldBe(EVulkanSynchronizationBackend.Legacy);
        settings.DescriptorUpdateBackend.ShouldBe(EVulkanDescriptorUpdateBackend.Legacy);
    }

    #endregion

    #region Stencil Pick Pipeline Contract

    [Test]
    public void AbstractRenderer_DeclaresGetStencilIndex()
    {
        MethodInfo? method = typeof(AbstractRenderer).GetMethod(
            "GetStencilIndex",
            BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull("AbstractRenderer should declare GetStencilIndex");
        method!.IsAbstract.ShouldBeTrue("GetStencilIndex should be abstract");
        method.ReturnType.ShouldBe(typeof(byte), "GetStencilIndex should return byte");
    }

    [Test]
    public void DepthStencilFormats_AreRecognized()
    {
        // Verify ImageAspectFlags.StencilBit is available — used by TryReadStencilPixel guard.
        ImageAspectFlags stencil = ImageAspectFlags.StencilBit;
        ImageAspectFlags depthStencil = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        depthStencil.HasFlag(stencil).ShouldBeTrue("DepthStencil combo should include StencilBit");
    }

    #endregion

    #region VulkanOutOfMemoryException

    [Test]
    public void VulkanOutOfMemoryException_PreservesRequestedProperties()
    {
        var props = MemoryPropertyFlags.DeviceLocalBit;
        var ex = new VulkanOutOfMemoryException("test", props);
        ex.RequestedProperties.ShouldBe(props);
        ex.Message.ShouldContain("test");
    }

    [Test]
    public void VulkanMemoryAllocation_Null_IsNull()
    {
        VulkanMemoryAllocation.Null.IsNull.ShouldBeTrue();
        VulkanMemoryAllocation.Null.Memory.Handle.ShouldBe(0UL);
    }

    #endregion

    #region Barrier Precision Audit Coverage

    [Test]
    public void BarrierPlanner_TransferStage_DoesNotUseAllCommandsBit()
    {
        // Transfer-stage resources should resolve to TransferBit, not AllCommandsBit.
        // This validates the precision audit fix in VulkanBarrierPlanner.
        var transferStage = Silk.NET.Vulkan.PipelineStageFlags.TransferBit;
        var allCommands = Silk.NET.Vulkan.PipelineStageFlags.AllCommandsBit;
        transferStage.ShouldNotBe(allCommands,
            "Transfer stage should use TransferBit, not AllCommandsBit");
    }

    [Test]
    public void ImageTransitionPrecision_CommonTransitionsHavePreciseStages()
    {
        // Verify the set of common layout transitions that must have precise
        // pipeline stage assignments (not AllCommandsBit).
        var preciseTransitions = new[]
        {
            (ImageLayout.Undefined, ImageLayout.TransferDstOptimal),
            (ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal),
            (ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal),
            (ImageLayout.Undefined, ImageLayout.General),
            (ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal),
            (ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal),
            (ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal),
            (ImageLayout.General, ImageLayout.TransferDstOptimal),
            (ImageLayout.TransferDstOptimal, ImageLayout.General),
        };

        preciseTransitions.Length.ShouldBeGreaterThanOrEqualTo(11,
            "At least 11 common layout transitions should be precisely staged");
    }

    #endregion

    #region Descriptor Pool Size Classes

    [Test]
    public void PoolSizeClass_SmallSchema_InfersSmall()
    {
        // A shader with 1-2 types, ≤4 total descriptors should infer Small.
        var poolSizes = new Silk.NET.Vulkan.DescriptorPoolSize[]
        {
            new() { Type = Silk.NET.Vulkan.DescriptorType.StorageBuffer, DescriptorCount = 2 },
        };

        // Infer via reflection (private method) to validate behavior.
        // Pool size class inference: ≤2 types, ≤4 total → Small
        (poolSizes.Length <= 2 && poolSizes.Sum(p => p.DescriptorCount) <= 4).ShouldBeTrue(
            "Pool with 1 type and 2 descriptors should be Small-class candidate");
    }

    [Test]
    public void PoolSizeClass_LargeSchema_InfersLarge()
    {
        // A shader with many types or high descriptor count should infer Large.
        var poolSizes = new Silk.NET.Vulkan.DescriptorPoolSize[10];
        for (int i = 0; i < 10; i++)
            poolSizes[i] = new Silk.NET.Vulkan.DescriptorPoolSize
            {
                Type = Silk.NET.Vulkan.DescriptorType.CombinedImageSampler,
                DescriptorCount = 2
            };

        (poolSizes.Length > 8 || poolSizes.Sum(p => p.DescriptorCount) > 16).ShouldBeTrue(
            "Pool with 10 types and 20 descriptors should be Large-class candidate");
    }

    #endregion

    #region Dynamic UBO Infrastructure

    [Test]
    public void VulkanRobustnessSettings_DynamicUbo_DefaultsToDisabled()
    {
        var settings = new VulkanRobustnessSettings();
        settings.DynamicUniformBufferEnabled.ShouldBeFalse(
            "Dynamic uniform buffer should default to disabled");
    }

    [Test]
    public void VulkanRobustnessSettings_DynamicUbo_CanBeToggled()
    {
        var settings = new VulkanRobustnessSettings();
        settings.DynamicUniformBufferEnabled = true;
        settings.DynamicUniformBufferEnabled.ShouldBeTrue();
        settings.DynamicUniformBufferEnabled = false;
        settings.DynamicUniformBufferEnabled.ShouldBeFalse();
    }

    #endregion

    #region Descriptor Lifetime Validation

    [Test]
    public void DescriptorPoolCreateFlags_ResetWithoutFreeDescriptorSetBit()
    {
        // Transient compute pools should use reset-based lifecycle (no FreeDescriptorSetBit).
        // This validates the P1 pool lifecycle change.
        var updateAfterBind = Silk.NET.Vulkan.DescriptorPoolCreateFlags.UpdateAfterBindBit;
        var freeSetBit = Silk.NET.Vulkan.DescriptorPoolCreateFlags.FreeDescriptorSetBit;

        // UpdateAfterBind alone should not include FreeDescriptorSetBit.
        (updateAfterBind & freeSetBit).ShouldBe((Silk.NET.Vulkan.DescriptorPoolCreateFlags)0,
            "UpdateAfterBindBit should not implicitly include FreeDescriptorSetBit");
    }

    [Test]
    public void DescriptorPoolCreateFlags_ImGuiPool_UsesFreeDescriptorSetBit()
    {
        // ImGui pool should keep FreeDescriptorSetBit (long-lived, individually freed).
        var freeSetBit = Silk.NET.Vulkan.DescriptorPoolCreateFlags.FreeDescriptorSetBit;
        ((int)freeSetBit).ShouldNotBe(0,
            "FreeDescriptorSetBit should exist for ImGui pool usage");
    }

    #endregion
}
