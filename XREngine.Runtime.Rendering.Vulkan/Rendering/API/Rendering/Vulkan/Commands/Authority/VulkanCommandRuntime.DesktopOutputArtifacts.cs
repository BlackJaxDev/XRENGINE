using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns command artifacts whose cardinality is defined by desktop swapchain images.</summary>
internal sealed partial class VulkanCommandRuntime
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
        // Cached chains own their secondary artifacts independently of the
        // desktop primary buffers. Retire them while the cache is still
        // enumerable so worker arenas can detach every artifact before their
        // command pools are retired.
        DestroyIndexedCommandChainCaches();
        CommandPool pool = Pools.PrimaryGraphics;

        ulong outputIdentity = CommandBuffers.DesktopOutputNativeDependencyIdentity;
        if (outputIdentity != 0)
        {
            _ = resources.NativeDependencies.Retire(
                EVulkanNativeDependencyOwner.Output,
                outputIdentity,
                "Swapchain.Output.Retirement");
            // Consume the exact Output -> CommandArtifact edges while the
            // primary-owner manifest still owns these artifacts. Retiring the
            // artifacts themselves follows immediately, so this intentionally
            // marks reuse state dirty without scheduling a reset of a buffer
            // that is about to be freed by this lifecycle.
            DrainNativeCommandArtifactDependencyInvalidations(resources);
            CommandBuffers.DesktopOutputNativeDependencyIdentity = 0;
        }

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

        void RetireArtifacts(CommandBuffer[]? artifacts, string owner)
        {
            if (artifacts is null)
                return;
            for (int index = 0; index < artifacts.Length; index++)
            {
                CommandBuffer artifact = artifacts[index];
                if (artifact.Handle != 0)
                    _ = resources.NativeDependencies.Retire(
                        EVulkanNativeDependencyOwner.CommandArtifact,
                        unchecked((ulong)artifact.Handle),
                        $"{owner}[{index}].Retirement");
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
        int imageCount,
        ulong outputNativeHandle)
    {
        if (imageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageCount));
        if (Pools.PrimaryGraphics.Handle == 0)
            throw new InvalidOperationException("The primary graphics command pool must exist before desktop output artifacts are created.");
        if (outputNativeHandle == 0)
            throw new InvalidOperationException("Desktop command artifacts require the live swapchain native handle as their output publisher identity.");
        if (CommandBuffers.DesktopOutputNativeDependencyIdentity != 0)
            throw new InvalidOperationException("Desktop command artifacts were recreated before their prior output publisher retired.");

        CommandBuffer[] primary = Allocate(CommandBufferLevel.Primary, "Swapchain.Primary");
        CommandBuffer[] dynamicUiSecondary = Allocate(CommandBufferLevel.Secondary, "Swapchain.DynamicUiSecondary");
        CommandBuffer[] dynamicUiOverlay = Allocate(CommandBufferLevel.Primary, "Swapchain.DynamicUiOverlay");
        CommandBuffer[] imguiOverlay = Allocate(CommandBufferLevel.Primary, "Swapchain.ImGuiOverlay");
        VulkanNativeDependencyGraph dependencies = resources.NativeDependencies;
        VulkanNativeDependencyHandle output = dependencies.Register(
            EVulkanNativeDependencyOwner.Output,
            outputNativeHandle);
        if (!output.IsValid)
            throw new InvalidOperationException("Failed to register the desktop output publisher identity.");
        CommandBuffers.DesktopOutputNativeDependencyIdentity = outputNativeHandle;
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
            RegisterOutputArtifact(primary[index], "primary");
            RegisterOutputArtifact(dynamicUiSecondary[index], "dynamic-ui-secondary");
            RegisterOutputArtifact(dynamicUiOverlay[index], "dynamic-ui-overlay");
            RegisterOutputArtifact(imguiOverlay[index], "imgui-overlay");
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

        void RegisterOutputArtifact(CommandBuffer artifact, string role)
        {
            ulong artifactIdentity = unchecked((ulong)artifact.Handle);
            VulkanNativeDependencyHandle commandArtifact = dependencies.Register(
                EVulkanNativeDependencyOwner.CommandArtifact,
                artifactIdentity);
            if (!commandArtifact.IsValid ||
                !dependencies.Link(
                    EVulkanNativeDependencyOwner.Output,
                    output,
                    EVulkanNativeDependencyOwner.CommandArtifact,
                    commandArtifact))
            {
                throw new InvalidOperationException(
                    $"Failed to publish desktop {role} command artifact 0x{artifactIdentity:X} under output 0x{outputNativeHandle:X}.");
            }
        }

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
