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
    public unsafe partial class VulkanRenderer
    {
        private ConcurrentDictionary<ulong, byte> _invalidatedCommandBuffersPendingReset
            => _commandRuntime.CommandBuffers.InvalidatedBuffersPendingReset;

        private void CreateCommandBuffers()
        {
            if (OutputRuntime.Desktop.Framebuffers is null || OutputRuntime.Desktop.Framebuffers.Length == 0)
                throw new InvalidOperationException("Framebuffers must be created before allocating command buffers.");

            _commandBuffers = new CommandBuffer[OutputRuntime.Desktop.Framebuffers.Length];

            CommandBufferAllocateInfo allocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)_commandBuffers.Length,
            };

            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                if (AllocateVulkanCommandBuffersTracked(ref allocInfo, commandBuffersPtr, "Frame.Primary") != Result.Success)
                    throw new Exception("Failed to allocate command buffers.");
            }

            _dynamicUiBatchTextSecondaryCommandBuffers = new CommandBuffer[_commandBuffers.Length];
            _dynamicUiBatchTextSecondaryOpCounts = new int[_commandBuffers.Length];
            _dynamicUiBatchTextSecondarySignatures = new ulong[_commandBuffers.Length];
            Array.Fill(_dynamicUiBatchTextSecondaryOpCounts, -1);
            Array.Fill(_dynamicUiBatchTextSecondarySignatures, ulong.MaxValue);
            CommandBufferAllocateInfo dynamicUiTextAllocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Secondary,
                CommandBufferCount = (uint)_dynamicUiBatchTextSecondaryCommandBuffers.Length,
            };

            fixed (CommandBuffer* commandBuffersPtr = _dynamicUiBatchTextSecondaryCommandBuffers)
            {
                if (AllocateVulkanCommandBuffersTracked(ref dynamicUiTextAllocInfo, commandBuffersPtr, "UI.TextSecondary") != Result.Success)
                    throw new Exception("Failed to allocate dynamic UI text secondary command buffers.");
            }

            _dynamicUiBatchTextOverlayCommandBuffers = new CommandBuffer[_commandBuffers.Length];
            CommandBufferAllocateInfo dynamicUiTextOverlayAllocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)_dynamicUiBatchTextOverlayCommandBuffers.Length,
            };

            fixed (CommandBuffer* commandBuffersPtr = _dynamicUiBatchTextOverlayCommandBuffers)
            {
                if (AllocateVulkanCommandBuffersTracked(ref dynamicUiTextOverlayAllocInfo, commandBuffersPtr, "UI.TextOverlaySecondary") != Result.Success)
                    throw new Exception("Failed to allocate dynamic UI text overlay command buffers.");
            }

            _imguiOverlayCommandBuffers = new CommandBuffer[_commandBuffers.Length];
            CommandBufferAllocateInfo imguiOverlayAllocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)_imguiOverlayCommandBuffers.Length,
            };

            fixed (CommandBuffer* commandBuffersPtr = _imguiOverlayCommandBuffers)
            {
                if (AllocateVulkanCommandBuffersTracked(ref imguiOverlayAllocInfo, commandBuffersPtr, "UI.ImGuiOverlaySecondary") != Result.Success)
                    throw new Exception("Failed to allocate ImGui overlay command buffers.");
            }

            InitializeCommandBufferVariants();
            AllocateCommandBufferDirtyFlags();
            EnsureCommandBufferFrameDataSlotCapacity(_commandBuffers.Length);
        }

        private void InitializeCommandBufferVariants()
        {
            if (_commandBuffers is null ||
                _dynamicUiBatchTextSecondaryCommandBuffers is null ||
                _dynamicUiBatchTextOverlayCommandBuffers is null ||
                _imguiOverlayCommandBuffers is null ||
                _commandBuffers.Length != _dynamicUiBatchTextSecondaryCommandBuffers.Length ||
                _commandBuffers.Length != _dynamicUiBatchTextOverlayCommandBuffers.Length ||
                _commandBuffers.Length != _imguiOverlayCommandBuffers.Length)
            {
                _primaryCommandArtifactOwners = null;
                _activeCommandBuffers = null;
                return;
            }

            _activeCommandBuffers = new CommandBuffer[_commandBuffers.Length];
            if (_primaryCommandPlans is null)
                _primaryCommandPlans =
                    new VulkanPrimaryCommandPlan[_commandBuffers.Length];
            else if (_primaryCommandPlans.Length < _commandBuffers.Length)
                Array.Resize(
                    ref _primaryCommandPlans,
                    _commandBuffers.Length);
            _primaryCommandArtifactOwners = new PrimaryCommandArtifactOwner[_commandBuffers.Length];
            for (int i = 0; i < _commandBuffers.Length; i++)
            {
                uint imageIndex = unchecked((uint)i);
                RegisterCommandBufferImageIndex(_commandBuffers[i], imageIndex);
                RegisterCommandBufferImageIndex(_dynamicUiBatchTextSecondaryCommandBuffers[i], imageIndex);
                RegisterCommandBufferImageIndex(_dynamicUiBatchTextOverlayCommandBuffers[i], imageIndex);
                RegisterCommandBufferImageIndex(_imguiOverlayCommandBuffers[i], imageIndex);
                SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)_commandBuffers[i].Handle), $"SwapchainPrimary[{i}]");
                SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)_dynamicUiBatchTextSecondaryCommandBuffers[i].Handle), $"DynamicUiBatchText.Secondary[{i}]");
                SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)_dynamicUiBatchTextOverlayCommandBuffers[i].Handle), $"DynamicUiBatchTextOverlay.Primary[{i}]");
                SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)_imguiOverlayCommandBuffers[i].Handle), $"ImGuiOverlay.Primary[{i}]");
                _activeCommandBuffers[i] = _commandBuffers[i];
                _primaryCommandPlans[i] ??= new VulkanPrimaryCommandPlan();
                _primaryCommandArtifactOwners[i] =
                    new PrimaryCommandArtifactOwner(
                        _commandBuffers[i],
                        _dynamicUiBatchTextSecondaryCommandBuffers[i],
                        commandPool,
                        commandPool,
                        ownsPrimaryCommandBuffer: false,
                        ownsDynamicUiSecondaryCommandBuffer: false);
            }
        }

        private bool TryEnsureCommandBuffersForSwapchain()
        {
            if (OutputRuntime.Desktop.Framebuffers is null || OutputRuntime.Desktop.Framebuffers.Length == 0)
                return false;

            bool needsAllocation =
                _commandBuffers is null ||
                _commandBufferDirtyFlags is null ||
                _dynamicUiBatchTextOverlayCommandBuffers is null ||
                _imguiOverlayCommandBuffers is null ||
                _commandBuffers.Length != OutputRuntime.Desktop.Framebuffers.Length ||
                _dynamicUiBatchTextOverlayCommandBuffers.Length != OutputRuntime.Desktop.Framebuffers.Length ||
                _imguiOverlayCommandBuffers.Length != OutputRuntime.Desktop.Framebuffers.Length ||
                _commandBufferDirtyFlags.Length != OutputRuntime.Desktop.Framebuffers.Length;

            if (!needsAllocation)
                return true;

            if (_commandBuffers is not null)
                DestroySwapchainCommandBuffers();

            CreateCommandBuffers();

            return _commandBuffers is not null &&
                _commandBufferDirtyFlags is not null &&
                _commandBuffers.Length == OutputRuntime.Desktop.Framebuffers.Length &&
                _commandBufferDirtyFlags.Length == OutputRuntime.Desktop.Framebuffers.Length;
        }

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
            RegisterCommandBufferImageIndex(owner.PrimaryCommandBuffer, imageIndex);
            RegisterCommandBufferImageIndex(owner.DynamicUiSecondaryCommandBuffer, imageIndex);
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

            if (AllocateVulkanCommandBuffersTracked(ref allocInfo, out CommandBuffer commandBuffer, label) != Result.Success ||
                commandBuffer.Handle == 0)
            {
                throw new Exception($"Failed to allocate Vulkan {label}.");
            }

            SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)commandBuffer.Handle), label);
            return commandBuffer;
        }

        private void SetActivePrimaryCommandArtifactOwner(uint imageIndex, PrimaryCommandArtifactOwner variant)
        {
            if (_activeCommandBuffers is null || imageIndex >= _activeCommandBuffers.Length)
                return;

            _activeCommandBuffers[imageIndex] = variant.PrimaryCommandBuffer;
        }

        private bool IsCommandBufferVariantGpuProfilerStateDirty(
            PrimaryCommandArtifactOwner variant,
            bool profilingActive,
            int frameSlot)
        {
            if (variant.GpuProfilerActive != profilingActive)
                return true;

            return profilingActive && variant.GpuProfilerFrameSlot != frameSlot;
        }

        private bool IsCommandBufferVariantImageLayoutStateDirty(
            PrimaryCommandArtifactOwner variant,
            ulong imageLayoutStartSignature)
            => IsCommandBufferVariantImageLayoutStateDirty(
                variant,
                imageLayoutStartSignature,
                out _);

        private bool IsCommandBufferVariantImageLayoutStateDirty(
            PrimaryCommandArtifactOwner variant,
            ulong imageLayoutStartSignature,
            out VulkanImageEntryStateMismatch mismatch)
        {
            // Keep the global signature for diagnostics, but scope reuse validity to
            // the entry states of images actually consumed by this command buffer.
            // Texture streaming and unrelated render outputs legitimately mutate the
            // renderer-wide layout map without changing this primary's contract.
            _ = imageLayoutStartSignature;
            if (variant.RecordedImageLayoutEndState is null)
            {
                mismatch = new VulkanImageEntryStateMismatch(
                    EVulkanPrimaryEntryStateMismatch.MissingCommandBufferState,
                    0,
                    0,
                    0,
                    ImageAspectFlags.None,
                    VulkanImageAccessState.Undefined,
                    VulkanImageAccessState.Undefined);
                return true;
            }

            return TryGetRecordedImageEntryStateMismatch(
                variant.PrimaryCommandBuffer,
                out mismatch);
        }

        private void LogCommandChainSecondaryInheritanceMismatch(
            string chainName,
            XRFrameBuffer? target,
            int passIndex,
            string reason)
        {
            if (!CommandChainsEnabledForCurrentRecording && !CommandChainValidationEnabled)
                return;

            string targetName = target?.Name ?? "<swapchain>";
            Debug.VulkanWarningEvery(
                $"Vulkan.CommandChains.SecondaryInheritance.{chainName}.{passIndex}.{target?.GetHashCode() ?? 0}.{reason.GetHashCode(StringComparison.Ordinal)}",
                TimeSpan.FromSeconds(2),
                "[Vulkan.CommandChains] Secondary inheritance mismatch chain={0} target='{1}' pass={2}: {3}",
                chainName,
                targetName,
                passIndex,
                reason);
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
        {
            using VulkanCpuStageScope dirtyPropagationStage =
                new(_frameTelemetry, EVulkanCpuStage.CommandDirtyPropagation);
            if (dependentCommandBuffers.IsEmpty)
                return default;

            for (int i = 0; i < dependentCommandBuffers.Length; i++)
            {
                if (dependentCommandBuffers[i] != 0)
                    _invalidatedCommandBuffersPendingReset.TryAdd(dependentCommandBuffers[i], 0);
            }

            int exactVariantsDirtied = 0;
            int exactChainsDirtied = 0;
            int unrelatedVariantsPreserved = 0;

            if (_primaryCommandArtifactOwners is not null)
            {
                for (int imageIndex = 0; imageIndex < _primaryCommandArtifactOwners.Length; imageIndex++)
                {
                    PrimaryCommandArtifactOwner variant = _primaryCommandArtifactOwners[imageIndex];
                    bool dependent = ContainsCommandBufferHandle(
                            dependentCommandBuffers,
                            unchecked((ulong)variant.PrimaryCommandBuffer.Handle)) ||
                        ContainsCommandBufferHandle(
                            dependentCommandBuffers,
                            unchecked((ulong)variant.DynamicUiSecondaryCommandBuffer.Handle));
                    if (!dependent)
                    {
                        unrelatedVariantsPreserved++;
                        continue;
                    }

                    if (!variant.Dirty)
                        exactVariantsDirtied++;
                    variant.Dirty = true;
                    variant.DirtyReason = reason;
                }
            }

            lock (OutputRuntime.OpenXrBackend.PrimaryCommandArtifactOwnersLock)
            {
                foreach (PrimaryCommandArtifactOwner owner in OpenXrPrimaryCommandArtifactOwners.Values)
                {
                    if (!ContainsCommandBufferHandle(
                            dependentCommandBuffers,
                            unchecked((ulong)owner.PrimaryCommandBuffer.Handle)))
                    {
                        unrelatedVariantsPreserved++;
                        continue;
                    }

                    if (!owner.Dirty)
                        exactVariantsDirtied++;
                    owner.Dirty = true;
                    owner.DirtyReason = reason;
                }
            }

            if (_commandChainCaches is not null)
            {
                for (int cacheIndex = 0; cacheIndex < _commandChainCaches.Length; cacheIndex++)
                {
                    foreach (CommandChain chain in _commandChainCaches[cacheIndex].Values)
                    {
                        if (!ContainsCommandBufferHandle(
                                dependentCommandBuffers,
                                unchecked((ulong)chain.SecondaryCommandBuffer.Handle)))
                            continue;

                        chain.State = CommandChainState.Unrecorded;
                        MarkCommandChainSecondaryCommandBufferInvalid(
                            chain,
                            EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged);
                        chain.DirtyReason = CommandChainDirtyReason.ResourcePlan;
                        exactChainsDirtied++;
                    }
                }
            }

            return new VulkanExactInvalidationResult(
                exactVariantsDirtied,
                exactChainsDirtied,
                unrelatedVariantsPreserved,
                GlobalFallbackInvalidations: 0);
        }

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

        private void DrainInvalidatedCommandBufferRecordings(int maxItems = 64)
        {
            if (maxItems <= 0 || _invalidatedCommandBuffersPendingReset.IsEmpty || Api is null)
                return;

            int resetCount = 0;
            foreach (KeyValuePair<ulong, byte> pair in _invalidatedCommandBuffersPendingReset)
            {
                if (resetCount >= maxItems)
                    break;

                ulong handle = pair.Key;
                CommandBuffer commandBuffer = new() { Handle = unchecked((nint)handle) };
                bool reset = false;
                lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
                {
                    // Use the complete reset predicate here. The former partial copy omitted
                    // PendingRetirement, then called the throwing wrapper and turned a normal
                    // deferred reset into a permanent render-loop exception.
                    if (!CanResetVulkanCommandBuffer(commandBuffer, out _))
                        continue;

                    ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                        handle,
                        out VulkanCommandBufferLifetimeRecord? lifetime);

                    Result result = ResetVulkanCommandBufferTracked(commandBuffer);
                    if (result != Result.Success)
                    {
                        Debug.VulkanWarningEvery(
                            $"Vulkan.CommandBuffer.InvalidatedReset.{handle}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Failed to reset invalidated command buffer 0x{0:X}: {1}",
                            handle,
                            result);
                        continue;
                    }

                    if (lifetime is not null)
                    {
                        ReleaseVulkanCommandBufferDependencies_NoLock(handle, lifetime);
                        lifetime.FrameDataLease.EvictCachedVariant();
                        lifetime.FrameDataLease.Reset();
                        lifetime.RecordingGeneration++;
                    }

                    _commandBufferTrackingBatches.TryRemove(handle, out _);
                    reset = true;
                }

                if (!reset)
                    continue;

                ResetRecordedImageLayoutState(commandBuffer);
                _invalidatedCommandBuffersPendingReset.TryRemove(handle, out _);
                resetCount++;
            }
        }

        private void AllocateCommandBufferDirtyFlags()
        {
            if (_commandBuffers is null)
            {
                _commandBufferDirtyFlags = null;
                _commandBufferFrameOpSignatures = null;
                _commandBufferFrameOpSignatureDebugParts = null;
                _commandBufferPlannerRevisions = null;
                return;
            }

            _commandBufferDirtyFlags = new bool[_commandBuffers.Length];
            _commandBufferFrameOpSignatures = new ulong[_commandBuffers.Length];
            _commandBufferFrameOpSignatureDebugParts = FrameOpSignatureDiffDiagnosticsEnabled
                ? new FrameOpSignatureDebugPart[_commandBuffers.Length][]
                : null;
            _commandBufferPlannerRevisions = new ulong[_commandBuffers.Length];
            for (int i = 0; i < _commandBufferDirtyFlags.Length; i++)
            {
                _commandBufferDirtyFlags[i] = true;
                _commandBufferFrameOpSignatures[i] = ulong.MaxValue;
                _commandBufferPlannerRevisions[i] = ulong.MaxValue;
            }
        }
    }
}
