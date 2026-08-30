using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private bool TryEnsureCommandChainSecondaryCommandBuffer(
        CommandChain chain,
        uint imageIndex,
        out CommandBuffer secondary)
    {
        secondary = chain.SecondaryCommandBuffer;
        if (secondary.Handle != 0 && chain.SecondaryCommandPool.Handle != 0)
            return true;

        DestroyCommandChainSecondaryCommandBuffer(chain);

        QueueFamilyIndices queueFamilyIndices = _deviceContext.QueueFamilies;
        uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
            ?? throw new InvalidOperationException("Graphics queue family is not available.");
        CommandPool pool = CreateCommandPoolForFamily(
            graphicsFamily,
            transient: false,
            "CommandChain.SerialRetained");
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Secondary,
            CommandBufferCount = 1
        };

        Result allocateResult = AllocateVulkanCommandBufferTracked(ref allocInfo, out secondary, "CommandChain.Secondary");
        if (allocateResult != Result.Success || secondary.Handle == 0)
        {
            if (pool.Handle != 0)
                DestroyCommandPoolHostSynchronized(pool);

            secondary = default;
            return false;
        }

        chain.RecordedArtifact.AssignNativeBuffer(
            secondary,
            pool,
            ownsPool: true);
        TrackOwnedCommandChainSecondaryCommandBuffer(pool, secondary);
        RegisterCommandBufferImageIndex(secondary, imageIndex);
        SetSecondaryDebugObjectName(ObjectType.CommandPool, pool.Handle, BuildCommandChainSecondaryDebugName(chain, imageIndex, "Pool"));
        SetSecondaryDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)secondary.Handle), BuildCommandChainSecondaryDebugName(chain, imageIndex, "Secondary"));
        return true;
    }

    private bool TryEnsureMutableCommandChainSecondaryCommandBuffer(
        CommandChain chain,
        uint imageIndex,
        HashSet<nint> executedSecondaryHandles,
        out CommandBuffer secondary)
    {
        // The serial recorder must not reset or allocate from a worker-owned
        // pool. Mixed renderer-family chains can move here after an older build
        // recorded them on a worker, so migrate them to an owned serial pool.
        if (chain.SecondaryCommandBuffer.Handle != 0 && !chain.OwnsSecondaryCommandPool)
            DestroyCommandChainSecondaryCommandBuffer(chain);

        if (!TryEnsureCommandChainSecondaryCommandBuffer(chain, imageIndex, out secondary))
            return false;

        if (secondary.Handle != 0 &&
            !executedSecondaryHandles.Contains(secondary.Handle) &&
            CanResetSecondaryCommandBuffer(secondary))
            return true;

        CommandPool pool = chain.SecondaryCommandPool;
        if (pool.Handle == 0)
            return false;

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Secondary,
            CommandBufferCount = 1
        };

        Result allocateResult = AllocateVulkanCommandBufferTracked(ref allocInfo, out CommandBuffer replacement, "CommandChain.SecondaryReplacement");
        if (allocateResult != Result.Success || replacement.Handle == 0)
            return false;

        VulkanRecordedCommandArtifactRetirement retirement =
            chain.RecordedArtifact.CaptureRetirement();
        DeferRecordedCommandArtifactRetirement(imageIndex, retirement);
        TrackOwnedCommandChainSecondaryCommandBuffer(pool, replacement);
        RegisterCommandBufferImageIndex(replacement, imageIndex);
        SetSecondaryDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)replacement.Handle), BuildCommandChainSecondaryDebugName(chain, imageIndex, "Secondary"));
        chain.RecordedArtifact.AssignNativeBuffer(
            replacement,
            pool,
            ownsPool: true);

        secondary = replacement;
        return true;
    }

    private bool TryEnsureMutableCommandChainSecondaryCommandBufferFromWorkerPool(
        CommandChain chain,
        uint imageIndex,
        VulkanLaneCommandFamilyArena laneArena,
        HashSet<nint> executedSecondaryHandles,
        out CommandBuffer secondary)
    {
        using VulkanLaneCommandFamilyArena.RecordingLease arenaLease =
            VulkanLaneCommandFamilyArena.EnterRecording(laneArena);
        CommandPool workerPool = laneArena.RetainedPool;
        secondary = chain.SecondaryCommandBuffer;
        if (workerPool.Handle == 0)
            return false;

        if (secondary.Handle != 0 && chain.SecondaryCommandPool.Handle != workerPool.Handle)
        {
            DestroyCommandChainSecondaryCommandBuffer(chain);
            secondary = default;
        }

        if (secondary.Handle == 0)
        {
            CommandBufferAllocateInfo allocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = workerPool,
                Level = CommandBufferLevel.Secondary,
                CommandBufferCount = 1,
            };
            Result allocateResult = AllocateVulkanCommandBufferTracked(
                ref allocInfo,
                out secondary,
                "CommandChain.WorkerSecondary");
            if (allocateResult != Result.Success || secondary.Handle == 0)
            {
                secondary = default;
                return false;
            }

            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanWorkerSecondaryCommandBufferAllocation(
                    replacement: false);
            chain.RecordedArtifact.AssignNativeBuffer(
                secondary,
                workerPool,
                ownsPool: false,
                laneArena);
            TrackOwnedCommandChainSecondaryCommandBuffer(workerPool, secondary);
            RegisterCommandBufferImageIndex(secondary, imageIndex);
            SetSecondaryDebugObjectName(
                ObjectType.CommandBuffer,
                unchecked((ulong)secondary.Handle),
                BuildCommandChainSecondaryDebugName(chain, imageIndex, "WorkerSecondary"));
        }

        if (!executedSecondaryHandles.Contains(secondary.Handle) &&
            CanResetSecondaryCommandBuffer(secondary))
            return true;

        CommandBufferAllocateInfo replacementAllocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = workerPool,
            Level = CommandBufferLevel.Secondary,
            CommandBufferCount = 1,
        };
        Result replacementResult = AllocateVulkanCommandBufferTracked(
            ref replacementAllocInfo,
            out CommandBuffer replacement,
            "CommandChain.WorkerSecondaryReplacement");
        if (replacementResult != Result.Success || replacement.Handle == 0)
            return false;

        RuntimeEngine.Rendering.Stats.Vulkan
            .RecordVulkanWorkerSecondaryCommandBufferAllocation(
                replacement: true);
        VulkanRecordedCommandArtifactRetirement retirement =
            chain.RecordedArtifact.CaptureRetirement();
        DeferRecordedCommandArtifactRetirement(imageIndex, retirement);
        RegisterCommandBufferImageIndex(replacement, imageIndex);
        SetSecondaryDebugObjectName(
            ObjectType.CommandBuffer,
            unchecked((ulong)replacement.Handle),
            BuildCommandChainSecondaryDebugName(chain, imageIndex, "WorkerSecondary"));
        chain.RecordedArtifact.AssignNativeBuffer(
            replacement,
            workerPool,
            ownsPool: false,
            laneArena);
        TrackOwnedCommandChainSecondaryCommandBuffer(workerPool, replacement);
        secondary = replacement;
        return true;
    }

    private static string BuildCommandChainSecondaryDebugName(CommandChain chain, uint imageIndex, string suffix)
        => $"CommandChain.{suffix} image={imageIndex} frameSlot={chain.Key.FrameSlot} pass={chain.Key.PassIndex} target={chain.Key.TargetIdentity} view={chain.Key.ViewKey.Kind}:{chain.Key.ViewKey.ViewIndex} ordinal={chain.Key.ChainOrdinal}";

    internal void MarkCommandChainSecondaryCommandBufferRecorded(CommandChain chain)
        => _ = TryPublishCommandChainSecondaryArtifact(chain, ResourceRuntime);

    internal void MarkCommandChainSecondaryRecording(
        CommandChain chain,
        CommandBuffer commandBuffer)
        => chain.RecordedArtifact.BeginRecording(
            ResolveCommandBufferRecordingGeneration(commandBuffer));

    private static bool CommandChainSecondaryInheritanceMatches(
        CommandChain chain,
        bool dynamicRendering,
        RenderPass renderPass,
        Framebuffer framebuffer,
        DynamicRenderingFormatSignature dynamicRenderingFormats,
        bool depthStencilReadOnly,
        SampleCountFlags samples,
        in DynamicRenderingLocalReadSignature localReadSignature,
        RenderingFlags renderingFlags)
    {
        VulkanRecordedCommandArtifact artifact = chain.RecordedArtifact;
        if (!artifact.HasInheritance ||
            artifact.Inheritance.DynamicRendering != dynamicRendering)
        {
            return false;
        }

        if (dynamicRendering)
        {
            return artifact.Inheritance.DynamicRenderingFormats.Equals(dynamicRenderingFormats) &&
                artifact.Inheritance.DepthStencilReadOnly == depthStencilReadOnly &&
                artifact.Inheritance.Samples == samples &&
                artifact.Inheritance.LocalReadSignature.Equals(
                    localReadSignature) &&
                artifact.Inheritance.RenderingFlags == renderingFlags;
        }

        return artifact.Inheritance.RenderPass.Handle == renderPass.Handle &&
            artifact.Inheritance.Framebuffer.Handle == framebuffer.Handle;
    }

    internal static void StoreCommandChainSecondaryInheritance(
        CommandChain chain,
        bool dynamicRendering,
        RenderPass renderPass,
        Framebuffer framebuffer,
        DynamicRenderingFormatSignature dynamicRenderingFormats,
        bool depthStencilReadOnly,
        SampleCountFlags samples,
        in DynamicRenderingLocalReadSignature localReadSignature,
        RenderingFlags renderingFlags)
    {
        chain.RecordedArtifact.StoreInheritance(
            new VulkanRecordedCommandInheritance(
                dynamicRendering,
                dynamicRendering ? default : renderPass,
                dynamicRendering ? default : framebuffer,
                dynamicRendering ? dynamicRenderingFormats : default,
                depthStencilReadOnly,
                samples,
                dynamicRendering ? localReadSignature : default,
                dynamicRendering ? renderingFlags : 0));
    }

    internal static void MarkCommandChainSecondaryCommandBufferInvalid(
        CommandChain chain,
        EVulkanRecordedCommandArtifactInvalidationReason reason =
            EVulkanRecordedCommandArtifactInvalidationReason.RecordingStarted)
        => chain.RecordedArtifact.Invalidate(reason);

    private void DestroyCommandChainSecondaryCommandBuffer(CommandChain chain)
    {
        CommandBuffer secondary = chain.SecondaryCommandBuffer;
        CommandPool pool = chain.SecondaryCommandPool;
        bool ownsPool = chain.OwnsSecondaryCommandPool;
        VulkanRecordedCommandArtifactRetirement retirement =
            chain.RecordedArtifact.CaptureRetirement();

        if (ownsPool && pool.Handle != 0)
            MarkOwnedCommandChainSecondaryPoolPendingDestroy(pool);

        if (secondary.Handle != 0)
        {
            if (!_deviceLost && pool.Handle != 0)
            {
                int imageIndex = ResolveCommandBufferImageIndex(secondary);
                if (imageIndex >= 0)
                {
                    DeferRecordedCommandArtifactRetirement(
                        unchecked((uint)imageIndex),
                        retirement);
                }
                else
                {
                    CommandBuffer freedSecondary = secondary;
                    FreeVulkanCommandBufferTracked(pool, ref secondary, "CommandChain.SecondaryReplacement");
                    if (!IsCommandBufferPendingRetirement(freedSecondary))
                    {
                        UntrackOwnedCommandChainSecondaryCommandBuffer(pool, freedSecondary);
                        DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(pool);
                    }
                }
            }
            else
            {
                RemoveCommandBufferBindState(secondary);
                UntrackOwnedCommandChainSecondaryCommandBuffer(pool, secondary);
                DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(pool);
            }
        }
        else if (ownsPool && pool.Handle != 0)
        {
            DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(pool);
        }

        chain.RecordedArtifact.MarkRetired();
    }
}
