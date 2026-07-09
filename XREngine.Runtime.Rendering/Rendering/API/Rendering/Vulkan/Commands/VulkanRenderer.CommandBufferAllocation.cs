using System;
using System.Buffers;
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
        private void CreateCommandBuffers()
        {
            if (swapChainFramebuffers is null || swapChainFramebuffers.Length == 0)
                throw new InvalidOperationException("Framebuffers must be created before allocating command buffers.");

            _commandBuffers = new CommandBuffer[swapChainFramebuffers.Length];

            CommandBufferAllocateInfo allocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)_commandBuffers.Length,
            };

            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                if (Api!.AllocateCommandBuffers(device, ref allocInfo, commandBuffersPtr) != Result.Success)
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
                if (Api!.AllocateCommandBuffers(device, ref dynamicUiTextAllocInfo, commandBuffersPtr) != Result.Success)
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
                if (Api!.AllocateCommandBuffers(device, ref dynamicUiTextOverlayAllocInfo, commandBuffersPtr) != Result.Success)
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
                if (Api!.AllocateCommandBuffers(device, ref imguiOverlayAllocInfo, commandBuffersPtr) != Result.Success)
                    throw new Exception("Failed to allocate ImGui overlay command buffers.");
            }

            InitializeCommandBufferVariants();
            AllocateCommandBufferDirtyFlags();
            _computeTransientResources = new ComputeTransientResources[_commandBuffers.Length];
            _deferredSecondaryCommandBuffers = new List<DeferredSecondaryCommandBuffer>[_commandBuffers.Length];
            InitializeComputeDescriptorCaches(_commandBuffers.Length);
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
                _commandBufferVariants = null;
                _activeCommandBuffers = null;
                return;
            }

            _activeCommandBuffers = new CommandBuffer[_commandBuffers.Length];
            _commandBufferVariants = new List<CommandBufferCacheVariant>[_commandBuffers.Length];
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
                _commandBufferVariants[i] =
                [
                    new CommandBufferCacheVariant(
                        _commandBuffers[i],
                        _dynamicUiBatchTextSecondaryCommandBuffers[i],
                        commandPool,
                        commandPool,
                        ownsPrimaryCommandBuffer: false,
                        ownsDynamicUiSecondaryCommandBuffer: false)
                ];
            }
        }

        private bool TryEnsureCommandBuffersForSwapchain()
        {
            if (swapChainFramebuffers is null || swapChainFramebuffers.Length == 0)
                return false;

            bool needsAllocation =
                _commandBuffers is null ||
                _commandBufferDirtyFlags is null ||
                _dynamicUiBatchTextOverlayCommandBuffers is null ||
                _imguiOverlayCommandBuffers is null ||
                _commandBuffers.Length != swapChainFramebuffers.Length ||
                _dynamicUiBatchTextOverlayCommandBuffers.Length != swapChainFramebuffers.Length ||
                _imguiOverlayCommandBuffers.Length != swapChainFramebuffers.Length ||
                _commandBufferDirtyFlags.Length != swapChainFramebuffers.Length;

            if (!needsAllocation)
                return true;

            if (_commandBuffers is not null)
                DestroyCommandBuffers();

            CreateCommandBuffers();

            return _commandBuffers is not null &&
                _commandBufferDirtyFlags is not null &&
                _commandBuffers.Length == swapChainFramebuffers.Length &&
                _commandBufferDirtyFlags.Length == swapChainFramebuffers.Length;
        }

        private CommandBufferCacheVariant GetOrCreateCommandBufferVariant(
            uint imageIndex,
            ulong frameOpsSignature,
            ulong dynamicUiBatchTextSignature,
            int dynamicUiBatchTextOpCount,
            CommandChainSchedule? commandChainSchedule,
            ulong commandChainPrimaryGroupSignature,
            int commandChainPrimaryGroupCount,
            bool preserveSwapchainForOverlay,
            FrameOp[] frameOpsForDiagnostics)
        {
            if (_commandBufferVariants is null || imageIndex >= _commandBufferVariants.Length)
                throw new InvalidOperationException("Command buffer variants are not initialised correctly.");

            int variantImageIndex = unchecked((int)Math.Min(imageIndex, int.MaxValue));
            List<CommandBufferCacheVariant> variants = _commandBufferVariants[variantImageIndex];
            bool useCommandChainKey = commandChainSchedule is not null;
            bool hasDynamicUiBatchTextOverlay = dynamicUiBatchTextOpCount > 0;

            CommandBufferCacheVariant? reusableDirtyMatch = null;
            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                if (useCommandChainKey)
                {
                    if (variant.CommandChainScheduleSignature == commandChainSchedule!.StructuralSignature &&
                        variant.CommandChainPrimaryGroupSignature == commandChainPrimaryGroupSignature &&
                        variant.CommandChainPrimaryGroupCount == commandChainPrimaryGroupCount &&
                        variant.FrameOpsSignature == frameOpsSignature &&
                        variant.DynamicUiSignature == dynamicUiBatchTextSignature &&
                        variant.PreserveSwapchainForOverlay == preserveSwapchainForOverlay &&
                        (variant.DynamicUiOpCount > 0) == hasDynamicUiBatchTextOverlay)
                    {
                        return variant;
                    }
                }
                else if (variant.FrameOpsSignature == frameOpsSignature &&
                    variant.DynamicUiSignature == dynamicUiBatchTextSignature &&
                    variant.PreserveSwapchainForOverlay == preserveSwapchainForOverlay)
                {
                    return variant;
                }

                if (reusableDirtyMatch is null &&
                    variant.Dirty &&
                    variant.FrameOpsSignature == ulong.MaxValue)
                {
                    reusableDirtyMatch = variant;
                }
            }

            if (reusableDirtyMatch is not null)
                return reusableDirtyMatch;

            if (variants.Count < PrimaryCommandBufferVariantCapacity)
            {
                CommandBuffer primary = AllocateCommandBuffer(CommandBufferLevel.Primary, "primary command buffer variant");
                CommandBuffer dynamicUiSecondary = AllocateCommandBuffer(CommandBufferLevel.Secondary, "dynamic UI text secondary command buffer variant");
                RegisterCommandBufferImageIndex(primary, imageIndex);
                RegisterCommandBufferImageIndex(dynamicUiSecondary, imageIndex);

                CommandBufferCacheVariant variant = new(
                    primary,
                    dynamicUiSecondary,
                    commandPool,
                    commandPool,
                    ownsPrimaryCommandBuffer: true,
                    ownsDynamicUiSecondaryCommandBuffer: true);
                variants.Add(variant);
                return variant;
            }

            CommandBufferCacheVariant evicted = variants[0];
            for (int i = 1; i < variants.Count; i++)
            {
                if (variants[i].LastUsedFrameId < evicted.LastUsedFrameId)
                    evicted = variants[i];
            }

            LogFrameOpSignatureVariantEvictionDiff(imageIndex, evicted, frameOpsSignature, frameOpsForDiagnostics);
            evicted.Dirty = true;
            evicted.DirtyReason = "variant eviction";
            evicted.FrameOpsSignature = ulong.MaxValue;
            evicted.DynamicUiSignature = ulong.MaxValue;
            evicted.DynamicUiOpCount = -1;
            evicted.DynamicUiSecondaryRecorded = false;
            evicted.PreserveSwapchainForOverlay = false;
            evicted.RecordedSwapchainFinalLayout = ImageLayout.PresentSrcKhr;
            evicted.RecordedSwapchainWriteCount = 0;
            evicted.RecordedSwapchainRefreshFromLastPresentSource = false;
            evicted.RecordedImageLayoutStartSignature = ulong.MaxValue;
            evicted.RecordedImageLayoutEndSignature = ulong.MaxValue;
            evicted.RecordedImageLayoutEndState = null;
            evicted.CommandChainScheduleSignature = ulong.MaxValue;
            evicted.CommandChainPrimaryGroupSignature = ulong.MaxValue;
            evicted.CommandChainPrimaryGroupCount = -1;
            evicted.PlannerRevision = ulong.MaxValue;
            evicted.GpuProfilerActive = false;
            evicted.GpuProfilerFrameSlot = -1;
            evicted.GpuProfilerScopes = null;
            evicted.GpuProfilerQueryCount = 0;
            evicted.SignatureDebugParts = null;
            RegisterCommandBufferImageIndex(evicted.PrimaryCommandBuffer, imageIndex);
            RegisterCommandBufferImageIndex(evicted.DynamicUiSecondaryCommandBuffer, imageIndex);
            return evicted;
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

            if (Api!.AllocateCommandBuffers(device, ref allocInfo, out CommandBuffer commandBuffer) != Result.Success ||
                commandBuffer.Handle == 0)
            {
                throw new Exception($"Failed to allocate Vulkan {label}.");
            }

            SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)commandBuffer.Handle), label);
            return commandBuffer;
        }

        private void SetActiveCommandBufferVariant(uint imageIndex, CommandBufferCacheVariant variant)
        {
            if (_activeCommandBuffers is null || imageIndex >= _activeCommandBuffers.Length)
                return;

            _activeCommandBuffers[imageIndex] = variant.PrimaryCommandBuffer;
        }

        private bool IsCommandBufferVariantGpuProfilerStateDirty(
            CommandBufferCacheVariant variant,
            bool profilingActive,
            int frameSlot)
        {
            if (variant.GpuProfilerActive != profilingActive)
                return true;

            return profilingActive && variant.GpuProfilerFrameSlot != frameSlot;
        }

        private static bool IsCommandBufferVariantImageLayoutStateDirty(
            CommandBufferCacheVariant variant,
            ulong imageLayoutStartSignature)
            => variant.RecordedImageLayoutStartSignature != imageLayoutStartSignature ||
               variant.RecordedImageLayoutEndState is null;

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

        private void MarkCommandBufferVariantsDirty(string? reason = null)
        {
            if (_commandBufferVariants is null)
                return;

            for (int i = 0; i < _commandBufferVariants.Length; i++)
                MarkCommandBufferVariantsDirty(unchecked((uint)i), reason);
        }

        private void MarkCommandBufferVariantsDirty(uint imageIndex, string? reason = null)
        {
            if (_commandBufferVariants is null || imageIndex >= _commandBufferVariants.Length)
                return;

            List<CommandBufferCacheVariant>? variants = _commandBufferVariants[imageIndex];
            if (variants is null)
                return;

            string dirtyReason = string.IsNullOrWhiteSpace(reason) ? "variant invalidated" : reason;
            foreach (CommandBufferCacheVariant variant in variants)
            {
                variant.Dirty = true;
                variant.DirtyReason = dirtyReason;
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
