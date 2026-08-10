using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns command artifacts whose cardinality is defined by desktop swapchain images.</summary>
internal sealed unsafe partial class VulkanCommandRuntime
{
    /// <summary>Ensures command-owned per-frame storage can address desktop and OpenXR frame-data slots.</summary>
    internal void EnsureFrameDataSlotCapacity(int frameDataSlotCount)
    {
        if (frameDataSlotCount <= 0)
            return;
        if (CommandBuffers.ComputeTransientResources is null)
            CommandBuffers.ComputeTransientResources = new ComputeTransientResources[frameDataSlotCount];
        else if (CommandBuffers.ComputeTransientResources.Length < frameDataSlotCount)
            Array.Resize(ref CommandBuffers.ComputeTransientResources, frameDataSlotCount);

        if (CommandBuffers.DeferredSecondaries is null)
            CommandBuffers.DeferredSecondaries = new List<DeferredSecondaryCommandBuffer>[frameDataSlotCount];
        else if (CommandBuffers.DeferredSecondaries.Length < frameDataSlotCount)
            Array.Resize(ref CommandBuffers.DeferredSecondaries, frameDataSlotCount);
    }

    /// <summary>
    /// Retires every desktop-image-indexed command artifact.  The caller must
    /// already have admitted the output recreation and settled worker recording.
    /// </summary>
    internal void RetireDesktopOutputArtifacts(
        Vk api,
        VulkanDeviceContext device,
        VulkanResourceRuntime resources,
        int frameSlot,
        CommandBuffer[]? imguiOverlayCommandBuffers)
    {
        Workers.Idle.Wait();
        CommandPool pool = Pools.PrimaryGraphics;
        RetireArtifacts(CommandBuffers.Buffers, "Swapchain.Primary");
        RetireArtifacts(CommandBuffers.DynamicUiSecondaries, "Swapchain.DynamicUiSecondary");
        RetireArtifacts(CommandBuffers.DynamicUiOverlays, "Swapchain.DynamicUiOverlay");
        RetireArtifacts(imguiOverlayCommandBuffers, "Swapchain.ImGuiOverlay");

        CommandBuffers.Buffers = null;
        CommandBuffers.ActiveBuffers = null;
        CommandBuffers.PrimaryPlans = null;
        CommandBuffers.PrimaryOwners = null;
        CommandBuffers.DynamicUiSecondaries = null;
        CommandBuffers.DynamicUiOverlays = null;
        CommandBuffers.DynamicUiOpCounts = null;
        CommandBuffers.DynamicUiSignatures = null;
        CommandBuffers.DirtyFlags = null;
        CommandBuffers.FrameOpSignatures = null;
        CommandBuffers.PlannerRevisions = null;
        CommandBuffers.SignatureDebugParts = null;
        CommandChains.Caches = null;

        void RetireArtifacts(CommandBuffer[]? artifacts, string owner)
        {
            if (artifacts is null)
                return;
            for (int index = 0; index < artifacts.Length; index++)
            {
                CommandBuffer artifact = artifacts[index];
                FreeTrackedCommandBuffer(
                    api,
                    device.Device,
                    resources,
                    frameSlot,
                    pool,
                    ref artifact,
                    $"{owner}[{index}]");
            }
        }
    }

    /// <summary>Publishes a fresh set of image-indexed primary and overlay command artifacts.</summary>
    internal CommandBuffer[] CreateDesktopOutputArtifacts(
        Vk api,
        VulkanDeviceContext device,
        VulkanResourceRuntime resources,
        int imageCount)
    {
        if (imageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageCount));
        if (Pools.PrimaryGraphics.Handle == 0)
            throw new InvalidOperationException("The primary graphics command pool must exist before desktop output artifacts are created.");

        CommandBuffer[] primary = Allocate(CommandBufferLevel.Primary, "Swapchain.Primary");
        CommandBuffer[] dynamicUiSecondary = Allocate(CommandBufferLevel.Secondary, "Swapchain.DynamicUiSecondary");
        CommandBuffer[] dynamicUiOverlay = Allocate(CommandBufferLevel.Primary, "Swapchain.DynamicUiOverlay");
        CommandBuffer[] imguiOverlay = Allocate(CommandBufferLevel.Primary, "Swapchain.ImGuiOverlay");
        CommandBuffers.Buffers = primary;
        CommandBuffers.ActiveBuffers = (CommandBuffer[])primary.Clone();
        CommandBuffers.DynamicUiSecondaries = dynamicUiSecondary;
        CommandBuffers.DynamicUiOverlays = dynamicUiOverlay;
        CommandBuffers.DynamicUiOpCounts = Enumerable.Repeat(-1, imageCount).ToArray();
        CommandBuffers.DynamicUiSignatures = Enumerable.Repeat(ulong.MaxValue, imageCount).ToArray();
        CommandBuffers.DirtyFlags = Enumerable.Repeat(true, imageCount).ToArray();
        CommandBuffers.FrameOpSignatures = new ulong[imageCount];
        CommandBuffers.PlannerRevisions = new ulong[imageCount];
        CommandBuffers.PrimaryPlans = new VulkanPrimaryCommandPlan[imageCount];
        CommandBuffers.PrimaryOwners = new PrimaryCommandArtifactOwner[imageCount];

        for (int index = 0; index < imageCount; index++)
        {
            uint imageIndex = unchecked((uint)index);
            CommandBuffers.RegisterImageIndex(primary[index], imageIndex);
            CommandBuffers.RegisterImageIndex(dynamicUiSecondary[index], imageIndex);
            CommandBuffers.RegisterImageIndex(dynamicUiOverlay[index], imageIndex);
            CommandBuffers.RegisterImageIndex(imguiOverlay[index], imageIndex);
            CommandBuffers.PrimaryPlans[index] = new VulkanPrimaryCommandPlan();
            CommandBuffers.PrimaryOwners[index] = new PrimaryCommandArtifactOwner(
                primary[index],
                dynamicUiSecondary[index],
                Pools.PrimaryGraphics,
                Pools.PrimaryGraphics,
                ownsPrimaryCommandBuffer: false,
                ownsDynamicUiSecondaryCommandBuffer: false)
            {
                Dirty = true,
                DirtyReason = "desktop swapchain generation created",
            };
        }

        // ImGui overlays are output-owned because they are submitted after the
        // reusable scene primary, but their native allocation and tracking still
        // belong to this command authority. The output service publishes the
        // returned generation and retires it with the other image-indexed artifacts.
        return imguiOverlay;

        CommandBuffer[] Allocate(CommandBufferLevel level, string owner)
        {
            CommandBuffer[] result = new CommandBuffer[imageCount];
            for (int index = 0; index < result.Length; index++)
                result[index] = AllocateTrackedCommandBuffer(
                    api,
                    device,
                    resources,
                    Pools.PrimaryGraphics,
                    level,
                    $"{owner}[{index}]");
            return result;
        }
    }
}
