using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanCommandRuntime
    {
        private ConcurrentDictionary<ulong, byte> _invalidatedCommandBuffersPendingReset
            => _commandRuntime.CommandBuffers.InvalidatedBuffersPendingReset;

        private PrimaryCommandArtifactOwner GetOrCreatePrimaryCommandArtifactOwner(
            uint imageIndex,
            ulong frameOpsSignature,
            ulong dynamicUiBatchTextSignature,
            int dynamicUiBatchTextOpCount,
            CommandChainSchedule? commandChainSchedule,
            ulong commandChainPrimaryGroupSignature,
            int commandChainPrimaryGroupCount,
            bool preserveSwapchainForOverlay,
            in CommandRecordingDependencySignature currentDependencySignature,
            FrameOp[] frameOpsForDiagnostics)
        {
            if (_primaryCommandArtifactOwners is null || imageIndex >= _primaryCommandArtifactOwners.Length)
                throw new InvalidOperationException("Primary command artifact owners are not initialised correctly.");

            int variantImageIndex = unchecked((int)Math.Min(imageIndex, int.MaxValue));
            // A frame slot owns one primary artifact for its current output
            // target generation. It is lifetime storage, not an LRU cache:
            // dependency validation decides whether that artifact can execute
            // again, and an output rotation re-records this owner in place.
            PrimaryCommandArtifactOwner owner = _primaryCommandArtifactOwners[variantImageIndex]
                ?? throw new InvalidOperationException("Primary command artifact owner is missing.");
            CommandBuffers.RegisterImageIndex(owner.PrimaryCommandBuffer, imageIndex);
            CommandBuffers.RegisterImageIndex(owner.DynamicUiSecondaryCommandBuffer, imageIndex);
            return owner;
        }

        private CommandBuffer AllocateCommandBuffer(CommandBufferLevel level, string label)
            => AllocateCommandBuffer(level, label, commandPool);

        private CommandBuffer AllocateCommandBuffer(CommandBufferLevel level, string label, CommandPool ownerPool)
        {
            CommandBufferAllocateInfo allocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = ownerPool,
                Level = level,
                CommandBufferCount = 1,
            };

            if (AllocateVulkanCommandBufferTracked(ref allocInfo, out CommandBuffer commandBuffer, label) != Result.Success ||
                commandBuffer.Handle == 0)
            {
                throw new Exception($"Failed to allocate Vulkan {label}.");
            }

            return commandBuffer;
        }

        private void MarkPrimaryCommandArtifactOwnersDirty(string? reason = null)
        {
            if (_primaryCommandArtifactOwners is null)
                return;

            for (int i = 0; i < _primaryCommandArtifactOwners.Length; i++)
                MarkPrimaryCommandArtifactOwnersDirty(unchecked((uint)i), reason);
        }

        private void MarkPrimaryCommandArtifactOwnersDirty(uint imageIndex, string? reason = null)
        {
            if (_primaryCommandArtifactOwners is null || imageIndex >= _primaryCommandArtifactOwners.Length)
                return;

            PrimaryCommandArtifactOwner owner = _primaryCommandArtifactOwners[imageIndex];
            owner.Dirty = true;
            owner.DirtyReason = string.IsNullOrWhiteSpace(reason) ? "owner invalidated" : reason;
        }


        private VulkanExactInvalidationResult InvalidateCachedCommandBuffersByHandle(
            ReadOnlySpan<ulong> dependentCommandBuffers,
            string reason)
            => InvalidateCachedCommandBuffers(dependentCommandBuffers, reason);

        private static bool ContainsCommandBufferHandle(
            ReadOnlySpan<ulong> commandBufferHandles,
            ulong candidate)
        {
            if (candidate == 0)
                return false;

            for (int i = 0; i < commandBufferHandles.Length; i++)
                if (commandBufferHandles[i] == candidate)
                    return true;

            return false;
        }

    }
}
