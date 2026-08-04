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

public unsafe partial class VulkanRenderer
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

        QueueFamilyIndices queueFamilyIndices = FamilyQueueIndices;
        uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
            ?? throw new InvalidOperationException("Graphics queue family is not available.");
        CommandPool pool = CreateCommandPoolForFamily(graphicsFamily);
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Secondary,
            CommandBufferCount = 1
        };

        Result allocateResult = AllocateVulkanCommandBuffersTracked(ref allocInfo, out secondary, "CommandChain.Secondary");
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
        SetDebugObjectName(ObjectType.CommandPool, pool.Handle, BuildCommandChainSecondaryDebugName(chain, imageIndex, "Pool"));
        SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)secondary.Handle), BuildCommandChainSecondaryDebugName(chain, imageIndex, "Secondary"));
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
            CanResetVulkanCommandBuffer(secondary, out _))
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

        Result allocateResult = AllocateVulkanCommandBuffersTracked(ref allocInfo, out CommandBuffer replacement, "CommandChain.SecondaryReplacement");
        if (allocateResult != Result.Success || replacement.Handle == 0)
            return false;

        VulkanRecordedCommandArtifactRetirement retirement =
            chain.RecordedArtifact.CaptureRetirement();
        DeferRecordedCommandArtifactRetirement(imageIndex, retirement);
        TrackOwnedCommandChainSecondaryCommandBuffer(pool, replacement);
        RegisterCommandBufferImageIndex(replacement, imageIndex);
        SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)replacement.Handle), BuildCommandChainSecondaryDebugName(chain, imageIndex, "Secondary"));
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
        VulkanWorkerSecondaryCommandArena workerArena,
        HashSet<nint> executedSecondaryHandles,
        out CommandBuffer secondary)
    {
        using VulkanWorkerSecondaryCommandArena.RecordingLease arenaLease =
            VulkanWorkerSecondaryCommandArena.EnterRecording(workerArena);
        CommandPool workerPool = workerArena.GetPool(chain.Key.FrameSlot);
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
            Result allocateResult = AllocateVulkanCommandBuffersTracked(
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
                workerArena);
            TrackOwnedCommandChainSecondaryCommandBuffer(workerPool, secondary);
            RegisterCommandBufferImageIndex(secondary, imageIndex);
            SetDebugObjectName(
                ObjectType.CommandBuffer,
                unchecked((ulong)secondary.Handle),
                BuildCommandChainSecondaryDebugName(chain, imageIndex, "WorkerSecondary"));
        }

        if (!executedSecondaryHandles.Contains(secondary.Handle) &&
            CanResetVulkanCommandBuffer(secondary, out _))
            return true;

        CommandBufferAllocateInfo replacementAllocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = workerPool,
            Level = CommandBufferLevel.Secondary,
            CommandBufferCount = 1,
        };
        Result replacementResult = AllocateVulkanCommandBuffersTracked(
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
        SetDebugObjectName(
            ObjectType.CommandBuffer,
            unchecked((ulong)replacement.Handle),
            BuildCommandChainSecondaryDebugName(chain, imageIndex, "WorkerSecondary"));
        chain.RecordedArtifact.AssignNativeBuffer(
            replacement,
            workerPool,
            ownsPool: false,
            workerArena);
        TrackOwnedCommandChainSecondaryCommandBuffer(workerPool, replacement);
        secondary = replacement;
        return true;
    }

    private static string BuildCommandChainSecondaryDebugName(CommandChain chain, uint imageIndex, string suffix)
        => $"CommandChain.{suffix} image={imageIndex} frameSlot={chain.Key.FrameSlot} pass={chain.Key.PassIndex} target={chain.Key.TargetIdentity} view={chain.Key.ViewKey.Kind}:{chain.Key.ViewKey.ViewIndex} ordinal={chain.Key.ChainOrdinal}";

    private void MarkCommandChainSecondaryCommandBufferRecorded(CommandChain chain)
    {
        VulkanRecordedCommandArtifact artifact = chain.RecordedArtifact;
        ulong handle = unchecked((ulong)artifact.NativeBuffer.Handle);
        if (handle == 0)
        {
            artifact.MarkFailed();
            return;
        }

        lock (_resourceLifetimeTracker.SyncRoot)
        {
            IReadOnlyList<KeyValuePair<VulkanResourceLifetimeKey, ulong>> dependencies =
                Array.Empty<KeyValuePair<VulkanResourceLifetimeKey, ulong>>();
            ulong recordingGeneration = artifact.RecordingGeneration;
            int queuedSubmissionCount = 0;
            if (_resourceLifetimeTracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                dependencies = lifetime.TouchedDependencies;
                recordingGeneration = lifetime.RecordingGeneration;
                queuedSubmissionCount = lifetime.QueuedSubmissionCount;
            }

            int recordedPrimaryReferenceCount = 0;
            if (_resourceLifetimeTracker.ResourceLifetimes.TryGetValue(
                    ResourceKey(ObjectType.CommandBuffer, handle),
                    out VulkanResourceLifetimeRecord? resource))
            {
                recordedPrimaryReferenceCount =
                    resource.Pins.RecordedReferenceCount;
            }

            artifact.PublishExecutable(
                chain.DependencySignature,
                dependencies,
                recordingGeneration,
                queuedSubmissionCount,
                recordedPrimaryReferenceCount);
        }
    }

    private void MarkCommandChainSecondaryRecording(
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

    private static void StoreCommandChainSecondaryInheritance(
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

    private static void MarkCommandChainSecondaryCommandBufferInvalid(
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
                    RemoveCommandBufferBindState(freedSecondary);
                    UntrackOwnedCommandChainSecondaryCommandBuffer(pool, freedSecondary);
                    DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(pool);
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

