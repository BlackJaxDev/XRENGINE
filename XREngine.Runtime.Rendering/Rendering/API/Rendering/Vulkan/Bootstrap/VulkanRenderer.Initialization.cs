using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using System;
using System.Linq;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer(XRWindow window, bool shouldLinkWindow = true) : AbstractRenderer<Vk>(window, shouldLinkWindow)
    {
        protected override Vk GetAPI()
            => Vk.GetApi();

        public override void Initialize()
        {
            if (Window?.VkSurface is null)
                throw new Exception("Windowing platform doesn't support Vulkan.");

            CreateInstance();
            SetupDebugMessenger();
            CreateSurface();
            PickPhysicalDevice();
            CreateLogicalDevice();
            InitializeMemoryAllocator();
            InitializeCanonicalImmutableSamplers();
            CreateCommandPool();

            CreateDescriptorSetLayout();
            CreateAllSwapChainObjects();

            //CreateTestModel();
            //CreateUniformBuffers();

            CreateSyncObjects();
            CreateFrameTimingResources();
            InitializeSynchronizationBackend();
            LogStartupCapabilitySnapshot();
            InitializeDynamicUniformRingBuffers();
            ReserveOpenXrFrameDataSlotsIfRequired("initialization");
            FlushPendingDeviceReadyProgramLinks();
        }

        /// <summary>
        /// Whether any device memory type supports <see cref="MemoryPropertyFlags.LazilyAllocatedBit"/>.
        /// True on most mobile/tiler GPUs; false on typical discrete desktop GPUs.
        /// </summary>
        internal bool SupportsLazyAllocation { get; private set; }

        private void InitializeMemoryAllocator()
        {
            // Probe for lazy allocation support (TransientAttachment optimization).
            Api!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProps);
            for (int i = 0; i < memProps.MemoryTypeCount; i++)
            {
                if (memProps.MemoryTypes[i].PropertyFlags.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit))
                {
                    SupportsLazyAllocation = true;
                    break;
                }
            }

            EVulkanAllocatorBackend backend = RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.AllocatorBackend;
            _memoryAllocator = backend switch
            {
                EVulkanAllocatorBackend.Legacy => new VulkanLegacyAllocator(this),
                EVulkanAllocatorBackend.Managed => new VulkanBlockAllocator(this),
                EVulkanAllocatorBackend.Vma => new VulkanVmaAllocator(
                    instance,
                    _physicalDevice,
                    device,
                    Vk.Version13,
                    SupportsBufferDeviceAddress),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(backend),
                    backend,
                    "Unknown Vulkan allocator backend.")
            };
            Debug.Vulkan($"[Vulkan] Memory allocator initialized: {backend} (lazyAlloc={SupportsLazyAllocation})");
        }

        public override void CleanUp()
        {
            if (device.Handle != 0)
                DeviceWaitIdle();

            bool forceRetirementDrain = IsDeviceLost;
            if (forceRetirementDrain)
                BeginForcedVulkanRetirementDrain();

            try
            {
            _textureUploadService.CancelAllQueuedWork(this, "Vulkan renderer shutdown");
            CancelPendingImportedTextureUploadFrameOps("Vulkan renderer shutdown");
            CancelRecordedTextureUploadPublications("Vulkan renderer shutdown");
            DrainVulkanPipelineCompileQueueForShutdown();
            WaitForPendingReadbackTasks(TimeSpan.FromSeconds(6));
            DestroyComputeTransientResources();
            DestroyDanglingMaterialWrappers();
            DestroyDanglingMeshRendererWrappers();
            DestroyDanglingRenderProgramPipelineWrappers();
            DestroyDanglingRenderProgramWrappers();
            DestroyDanglingDataBufferWrappers();
            DestroyDanglingFrameBufferWrappers();
            DestroyDanglingTextureWrappers();
            DestroyCachedAPIRenderObjects();
            DestroyRemainingTrackedMeshUniformBuffers();

            // Drain all deferred-deletion queues now that the GPU is idle.
            ForceFlushAllRetiredResources();

            DestroyAutoExposureComputeResources();
            DestroyPlaceholderTexture();
            DisposeImGuiResources();
            DestroyOpenXrRenderingResources();
            DestroyFrameOpResourcePlannerStates();
            DestroyAllSwapChainObjects();
            // FBO render passes are NOT destroyed during swapchain recreation
            // (they are swapchain-independent). Clean them up here at full shutdown.
            DestroyFrameBufferRenderPasses();
            DestroyDescriptorSetLayout();
            DestroyRetainedAutoExposureHistory("renderer shutdown");
            ResourceAllocator.DestroyPhysicalImages(this);
            ResourceAllocator.DestroyPhysicalBuffers(this);
            _stagingManager.Destroy(this);
            DestroyDynamicUniformRingBuffers();

            // Teardown paths above may create or retain late-bound GPU resources.
            // Sweep wrappers and deferred queues before disposing the allocator so
            // final destruction can still free through the correct allocation path.
            DestroyDanglingMaterialWrappers();
            DestroyDanglingMeshRendererWrappers();
            DestroyDanglingRenderProgramPipelineWrappers();
            DestroyDanglingRenderProgramWrappers();
            DestroyDanglingDataBufferWrappers();
            DestroyDanglingFrameBufferWrappers();
            DestroyDanglingTextureWrappers();
            DestroyCachedAPIRenderObjects();
            DestroyRemainingTrackedMeshUniformBuffers();
            ForceFlushAllRetiredResources();
            DestroyRemainingTrackedImageViews();
            DestroyRemainingTrackedPipelineLayouts();
            DestroyRemainingTrackedBufferAllocations();
            DestroyRemainingTrackedImageAllocations();

            if (_memoryAllocator is VulkanBlockAllocator blockAllocator)
                blockAllocator.DestroyAllBlocks(Api!, device);
            _memoryAllocator?.Dispose();
            _memoryAllocator = null;
            _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
            DestroyFrameTimingResources();

            DestroySyncObjects();
            DestroyCommandPool();

            // Flush once more before destroying the logical device to catch any
            // handles retired by sync/command-pool teardown.
            ForceFlushAllRetiredResources();
            DestroyRemainingTrackedImageViews();
            DestroyRemainingTrackedDescriptorSetLayouts();
            DestroySharedGraphicsPipelines();
            DestroyRemainingTrackedPipelineLayouts();
            DestroySharedGraphicsPipelineLibraries();

            DestroyLogicalDevice();
            DestroyValidationLayers();
            DestroySurface();
            DestroyInstance();
            }
            finally
            {
                if (forceRetirementDrain)
                    EndForcedVulkanRetirementDrain();
            }
        }

        private void DestroyDanglingMaterialWrappers()
        {
            var wrappers = VkObject<XRMaterial>.Cache.Values.ToArray();
            foreach (var wrapper in wrappers)
            {
                try
                {
                    wrapper?.Destroy();
                }
                catch
                {
                }
            }
        }

        private void DestroyDanglingMeshRendererWrappers()
        {
            var wrappers = VkObject<XRMeshRenderer.BaseVersion>.Cache.Values.ToArray();
            foreach (var wrapper in wrappers)
            {
                try
                {
                    wrapper?.Destroy();
                }
                catch
                {
                }
            }
        }

        private void DestroyDanglingRenderProgramPipelineWrappers()
        {
            var wrappers = VkObject<XRRenderProgramPipeline>.Cache.Values.ToArray();
            foreach (var wrapper in wrappers)
            {
                try
                {
                    wrapper?.Destroy();
                }
                catch
                {
                }
            }
        }

        private void DestroyDanglingRenderProgramWrappers()
        {
            var wrappers = VkObject<XRRenderProgram>.Cache.Values.ToArray();
            foreach (var wrapper in wrappers)
            {
                try
                {
                    wrapper?.Destroy();
                }
                catch
                {
                }
            }
        }

        private void DestroyDanglingDataBufferWrappers()
        {
            var wrappers = VkObject<XRDataBuffer>.Cache.Values.ToArray();
            foreach (var wrapper in wrappers)
            {
                try
                {
                    wrapper?.Destroy();
                }
                catch
                {
                }
            }
        }

        private void DestroyDanglingFrameBufferWrappers()
        {
            DestroyCachedWrappers(VkObject<XRFrameBuffer>.Cache.Values.ToArray(), "framebuffer");
            DestroyCachedWrappers(VkObject<XRRenderBuffer>.Cache.Values.ToArray(), "renderbuffer");
        }

        private void DestroyDanglingTextureWrappers()
        {
            DestroyCachedWrappers(VkObject<XRTexture1D>.Cache.Values.ToArray(), "texture1D");
            DestroyCachedWrappers(VkObject<XRTexture1DArray>.Cache.Values.ToArray(), "texture1DArray");
            DestroyCachedWrappers(VkObject<XRTexture2D>.Cache.Values.ToArray(), "texture2D");
            DestroyCachedWrappers(VkObject<XRTexture2DArray>.Cache.Values.ToArray(), "texture2DArray");
            DestroyCachedWrappers(VkObject<XRTexture3D>.Cache.Values.ToArray(), "texture3D");
            DestroyCachedWrappers(VkObject<XRTextureCube>.Cache.Values.ToArray(), "textureCube");
            DestroyCachedWrappers(VkObject<XRTextureCubeArray>.Cache.Values.ToArray(), "textureCubeArray");
            DestroyCachedWrappers(VkObject<XRTextureRectangle>.Cache.Values.ToArray(), "textureRectangle");
            DestroyCachedWrappers(VkObject<XRTextureBuffer>.Cache.Values.ToArray(), "textureBuffer");
            DestroyCachedWrappers(VkObject<XRTextureViewBase>.Cache.Values.ToArray(), "textureView");
            DestroyCachedWrappers(VkObject<XRSampler>.Cache.Values.ToArray(), "sampler");
        }

        private static void DestroyCachedWrappers<T>(VkObject<T>[] wrappers, string label)
            where T : GenericRenderObject
        {
            foreach (var wrapper in wrappers)
            {
                try
                {
                    wrapper?.Destroy();
                }
                catch (Exception ex)
                {
                    Debug.VulkanWarning(
                        "[Vulkan] Failed to destroy cached {0} wrapper '{1}'. {2}",
                        label,
                        wrapper?.GetType().Name ?? "<null>",
                        ex.Message);
                }
            }
        }

        private void DestroyRemainingTrackedBufferAllocations()
        {
            foreach (var pair in _bufferAllocations.ToArray())
            {
                if (!_bufferAllocations.TryRemove(pair.Key, out VulkanMemoryAllocation allocation))
                    continue;

                Buffer buffer = new() { Handle = pair.Key };
                if (buffer.Handle != 0 && TryBeginDestroyBuffer(buffer, "DestroyRemainingTrackedBufferAllocations"))
                    Api!.DestroyBuffer(device, buffer, null);

                FreeMemoryAllocation(allocation);
            }

            foreach (var pair in _legacyBufferAllocations.ToArray())
            {
                if (!_legacyBufferAllocations.TryRemove(pair.Key, out VulkanMemoryAllocation allocation))
                    continue;

                Buffer buffer = new() { Handle = pair.Key };
                if (buffer.Handle != 0 && TryBeginDestroyBuffer(buffer, "DestroyRemainingTrackedLegacyBufferAllocations"))
                    Api!.DestroyBuffer(device, buffer, null);

                if (allocation.Memory.Handle != 0)
                    FreeLegacyBufferMemory(allocation);
            }
        }

        private void DestroyRemainingTrackedImageAllocations()
        {
            foreach (var pair in _imageAllocations.ToArray())
            {
                if (!_imageAllocations.TryRemove(pair.Key, out VulkanMemoryAllocation allocation))
                    continue;

                Image image = new() { Handle = pair.Key };
                if (image.Handle != 0)
                    DestroyVulkanImageImmediateTracked(image, "RendererShutdown.RemainingAllocation");

                FreeMemoryAllocation(allocation);
            }
        }

        // It should be noted that in a real world application, you're not supposed to actually call vkAllocateMemory for every individual buffer.
        // The maximum number of simultaneous memory allocations is limited by the maxMemoryAllocationCount physical device limit, which may be as low as 4096 even on high end hardware like an NVIDIA GTX 1080.
        // The right way to allocate memory for a large number of objects at the same time is to create a custom allocator that splits up a single allocation among many different objects by using the offset parameters that we've seen in many functions.

        private void AllocateMemory(MemoryAllocateInfo allocInfo, DeviceMemory* memPtr)
        {
            Result result = Api!.AllocateMemory(device, ref allocInfo, null, memPtr);
            if (result == Result.ErrorOutOfDeviceMemory || result == Result.ErrorOutOfHostMemory)
            {
                Debug.VulkanWarning(
                    $"[Vulkan] OOM during AllocateMemory (size={allocInfo.AllocationSize}, memType={allocInfo.MemoryTypeIndex}). Result={result}");
                throw new VulkanOutOfMemoryException(
                    $"Vulkan memory allocation failed ({result}). Size={allocInfo.AllocationSize}",
                    MemoryPropertyFlags.None);
            }
            if (result != Result.Success)
                throw new Exception($"Failed to allocate memory. Result={result}");
        }

        /// <summary>
        /// Attempts to allocate memory for a buffer through the active allocator,
        /// with automatic fallback to host-visible memory on OOM.
        /// </summary>
        internal VulkanMemoryAllocation AllocateBufferMemoryWithFallback(
            Buffer buffer, MemoryPropertyFlags requiredProperties)
        {
            IVulkanMemoryAllocator alloc = MemoryAllocator;
            if (alloc.TryAllocateForBuffer(Api!, device, buffer, requiredProperties, out VulkanMemoryAllocation allocation))
                return allocation;

            // OOM — attempt fallback to host-visible if the original was device-local.
            if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
            {
                MemoryPropertyFlags fallback = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                Debug.VulkanWarning(
                    $"[Vulkan] OOM for buffer (requested {requiredProperties}). Falling back to {fallback}.");
                if (alloc.TryAllocateForBuffer(Api!, device, buffer, fallback, out allocation))
                {
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanOomFallback();
                    return allocation;
                }
            }

            throw new VulkanOutOfMemoryException(
                $"Vulkan buffer allocation failed with no viable fallback. Requested={requiredProperties}",
                requiredProperties);
        }

        /// <summary>
        /// Attempts to allocate memory for an image through the active allocator,
        /// with automatic fallback chain: requested → DeviceLocal (if lazy was requested) → HostVisible on OOM.
        /// Callers may include <see cref="MemoryPropertyFlags.LazilyAllocatedBit"/> for transient attachments;
        /// the allocator will strip it if the device doesn't support lazy allocation.
        /// </summary>
        internal VulkanMemoryAllocation AllocateImageMemoryWithFallback(
            Image image, MemoryPropertyFlags requiredProperties)
        {
            if (TryAllocateImageMemoryWithFallback(image, requiredProperties, out VulkanMemoryAllocation allocation, out string failureReason))
                return allocation;

            throw new VulkanOutOfMemoryException(failureReason, requiredProperties);
        }

        internal bool TryAllocateImageMemoryWithFallback(
            Image image,
            MemoryPropertyFlags requiredProperties,
            out VulkanMemoryAllocation allocation,
            out string failureReason)
        {
            IVulkanMemoryAllocator alloc = MemoryAllocator;
            allocation = VulkanMemoryAllocation.Null;
            MemoryPropertyFlags originalProperties = requiredProperties;
            failureReason = string.Empty;

            // Strip lazy if device doesn't support it, to avoid guaranteed first-try failure.
            if (requiredProperties.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit) && !SupportsLazyAllocation)
                requiredProperties &= ~MemoryPropertyFlags.LazilyAllocatedBit;

            if (ShouldDeferVulkanImageMemoryAllocationForPressure(
                    image,
                    requiredProperties,
                    out failureReason))
            {
                return false;
            }

            if (alloc.TryAllocateForImage(Api!, device, image, requiredProperties, out allocation))
            {
                failureReason = string.Empty;
                return true;
            }

            // If lazy was requested, retry without it (device-local only).
            if (requiredProperties.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit))
            {
                MemoryPropertyFlags withoutLazy = requiredProperties & ~MemoryPropertyFlags.LazilyAllocatedBit;
                if (alloc.TryAllocateForImage(Api!, device, image, withoutLazy, out allocation))
                {
                    Debug.VulkanWarning(
                        $"[Vulkan] Image allocation requested {requiredProperties} but lazy allocation failed; falling back to {withoutLazy}.");
                    failureReason = string.Empty;
                    return true;
                }
            }

            if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
                Debug.VulkanWarning(
                    $"[Vulkan] Image allocation failed for {requiredProperties}; no host-visible fallback is attempted for Vulkan images.");

            allocation = VulkanMemoryAllocation.Null;
            failureReason = $"Vulkan image allocation failed with no viable fallback. Requested={originalProperties}";
            return false;
        }

        private bool ShouldDeferVulkanImageMemoryAllocationForPressure(
            Image image,
            MemoryPropertyFlags requiredProperties,
            out string reason)
        {
            reason = string.Empty;
            if (!requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit) ||
                Api is null ||
                device.Handle == 0 ||
                image.Handle == 0)
            {
                return false;
            }

            IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
            if (!host.IsOpenXRActive && !host.IsInVR)
                return false;

            Api.GetImageMemoryRequirements(device, image, out MemoryRequirements requirements);
            long requestedBytes = requirements.Size > long.MaxValue
                ? long.MaxValue
                : (long)requirements.Size;

            if (!TryGetOpenXrVulkanImageAllocationPressureSnapshot(
                    out long trackedVramBytes,
                    out long trackedVramDeferLimitBytes,
                    out long allocatorBytes,
                    out long allocatorDeferLimitBytes,
                    out long allocatorLargestHeapBytes,
                    out int activeAllocationCount))
            {
                return false;
            }

            if (!TryDescribeOpenXrVulkanImageAllocationPressure(
                    requestedBytes,
                    requiredProperties,
                    trackedVramBytes,
                    trackedVramDeferLimitBytes,
                    allocatorBytes,
                    allocatorDeferLimitBytes,
                    allocatorLargestHeapBytes,
                    activeAllocationCount,
                    out reason))
            {
                return false;
            }

            return true;
        }

        internal bool ShouldAvoidSynchronousImageAllocationForOpenXr(out string reason)
        {
            reason = string.Empty;

            IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
            if (!host.IsOpenXRActive && !host.IsInVR)
                return false;

            if (!TryGetOpenXrVulkanImageAllocationPressureSnapshot(
                    out long trackedVramBytes,
                    out long trackedVramDeferLimitBytes,
                    out long allocatorBytes,
                    out long allocatorDeferLimitBytes,
                    out long allocatorLargestHeapBytes,
                    out int activeAllocationCount))
            {
                return false;
            }

            return TryDescribeOpenXrVulkanImageAllocationPressure(
                requestedBytes: 0L,
                MemoryPropertyFlags.DeviceLocalBit,
                trackedVramBytes,
                trackedVramDeferLimitBytes,
                allocatorBytes,
                allocatorDeferLimitBytes,
                allocatorLargestHeapBytes,
                activeAllocationCount,
                out reason);
        }

        private bool TryGetOpenXrVulkanImageAllocationPressureSnapshot(
            out long trackedVramBytes,
            out long trackedVramDeferLimitBytes,
            out long allocatorBytes,
            out long allocatorDeferLimitBytes,
            out long allocatorLargestHeapBytes,
            out int activeAllocationCount)
        {
            trackedVramBytes = 0L;
            trackedVramDeferLimitBytes = long.MaxValue;
            allocatorBytes = 0L;
            allocatorDeferLimitBytes = long.MaxValue;
            allocatorLargestHeapBytes = 0L;
            activeAllocationCount = 0;

            try
            {
                activeAllocationCount = MemoryAllocator.ActiveVkAllocationCount;
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
            trackedVramBytes = Math.Max(0L, host.TrackedVramBytes);
            trackedVramDeferLimitBytes = ResolveOpenXrVulkanImageAllocationTrackedVramLimit(host.TrackedVramBudgetBytes);
            if (TryGetVulkanAllocatorBudgetSnapshot(
                    OpenXrVulkanImageAllocationPressurePreflightRatio,
                    OpenXrVulkanImageAllocationPressureReserveBytes,
                    out long currentAllocatorBytes,
                    out long currentAllocatorDeferLimitBytes,
                    out long currentAllocatorLargestHeapBytes,
                    out int currentActiveAllocationCount))
            {
                allocatorBytes = Math.Max(0L, currentAllocatorBytes);
                allocatorDeferLimitBytes = currentAllocatorDeferLimitBytes > 0L
                    ? currentAllocatorDeferLimitBytes
                    : long.MaxValue;
                allocatorLargestHeapBytes = Math.Max(0L, currentAllocatorLargestHeapBytes);
                activeAllocationCount = currentActiveAllocationCount;
            }

            return true;
        }

        private static long ResolveOpenXrVulkanImageAllocationTrackedVramLimit(long trackedVramBudgetBytes)
        {
            if (trackedVramBudgetBytes <= 0L || trackedVramBudgetBytes == long.MaxValue)
                return long.MaxValue;

            double clampedRatio = Math.Clamp(OpenXrVulkanImageAllocationPressurePreflightRatio, 0.1, 1.0);
            long ratioLimitBytes = (long)Math.Floor(trackedVramBudgetBytes * clampedRatio);
            long reserveLimitBytes = trackedVramBudgetBytes > OpenXrVulkanImageAllocationPressureReserveBytes
                ? trackedVramBudgetBytes - Math.Max(0L, OpenXrVulkanImageAllocationPressureReserveBytes)
                : trackedVramBudgetBytes;
            return Math.Max(1L, Math.Min(ratioLimitBytes, reserveLimitBytes));
        }

        private bool TryDescribeOpenXrVulkanImageAllocationPressure(
            long requestedBytes,
            MemoryPropertyFlags requiredProperties,
            long trackedVramBytes,
            long trackedVramDeferLimitBytes,
            long allocatorBytes,
            long allocatorDeferLimitBytes,
            long allocatorLargestHeapBytes,
            int activeAllocationCount,
            out string reason)
        {
            reason = string.Empty;

            if (allocatorDeferLimitBytes != long.MaxValue)
            {
                long projectedAllocatorBytes = allocatorBytes > long.MaxValue - requestedBytes
                    ? long.MaxValue
                    : allocatorBytes + requestedBytes;
                if (projectedAllocatorBytes >= allocatorDeferLimitBytes)
                {
                    reason =
                        $"Vulkan image allocation deferred under allocator pressure. requested={requestedBytes}, allocated={allocatorBytes}, projectedAllocated={projectedAllocatorBytes}, largestHeap={allocatorLargestHeapBytes}, deferLimit={allocatorDeferLimitBytes}, activeVkAllocations={activeAllocationCount}, requestedProperties={requiredProperties}";
                    return true;
                }
            }

            if (trackedVramDeferLimitBytes != long.MaxValue)
            {
                long projectedBytes = trackedVramBytes > long.MaxValue - requestedBytes
                    ? long.MaxValue
                    : trackedVramBytes + requestedBytes;
                if (projectedBytes >= trackedVramDeferLimitBytes)
                {
                    reason =
                        $"Vulkan image allocation deferred under tracked VRAM pressure. requested={requestedBytes}, trackedVram={trackedVramBytes}, projectedTrackedVram={projectedBytes}, trackedVramDeferLimit={trackedVramDeferLimitBytes}, activeVkAllocations={activeAllocationCount}, requestedProperties={requiredProperties}";
                    return true;
                }
            }

            if (Api is null || _physicalDevice.Handle == 0)
                return false;

            Api.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
            uint maxAllocationCount = properties.Limits.MaxMemoryAllocationCount;
            if (maxAllocationCount == 0)
                return false;

            int ratioLimit = (int)Math.Floor(maxAllocationCount * OpenXrVulkanImageAllocationCountPreflightRatio);
            int reserveLimit = maxAllocationCount > OpenXrVulkanImageAllocationCountReserve
                ? (int)Math.Min(int.MaxValue, maxAllocationCount - OpenXrVulkanImageAllocationCountReserve)
                : (int)Math.Min(int.MaxValue, maxAllocationCount);
            int allocationCountLimit = Math.Max(1, Math.Min(ratioLimit, reserveLimit));
            if (activeAllocationCount < allocationCountLimit)
                return false;

            reason =
                $"Vulkan image allocation deferred under allocation-count pressure. activeVkAllocations={activeAllocationCount}, maxMemoryAllocationCount={maxAllocationCount}, limit={allocationCountLimit}, requested={requestedBytes}, requestedProperties={requiredProperties}";
            return true;
        }

        /// <summary>Frees a memory allocation through the active allocator.</summary>
        internal void FreeMemoryAllocation(VulkanMemoryAllocation allocation)
        {
            if (allocation.IsNull)
                return;
            MemoryAllocator.Free(Api!, device, allocation);
        }

        public static unsafe void* Allocated(void* pUserData, nuint size, nuint alignment, SystemAllocationScope allocationScope)
        {
            //Output.Log();
            return null;
        }

        private void* Reallocated(void* pUserData, void* pOriginal, nuint size, nuint alignment, SystemAllocationScope allocationScope)
        {
            return null;
        }

        private void Freed(void* pUserData, void* pMemory)
        {

        }
        private void InternalAllocated(void* pUserData, nuint size, InternalAllocationType allocationType, SystemAllocationScope allocationScope)
        {

        }

        private void InternalFreed(void* pUserData, nuint size, InternalAllocationType allocationType, SystemAllocationScope allocationScope)
        {

        }

        public override void StencilMask(uint mask)
        {
            ActiveState.SetStencilWriteMask(mask);
        }

        public override void EnableStencilTest(bool enable)
        {
            // Vulkan: stencil test is configured per-pipeline; tracked in dynamic state for future use.
        }

        public override void StencilFunc(EComparison function, int reference, uint mask)
        {
            // Vulkan: stencil compare is per-pipeline state; no global toggle.
        }

        public override void StencilOp(EStencilOp sfail, EStencilOp dpfail, EStencilOp dppass)
        {
            // Vulkan: stencil ops are per-pipeline state; no global toggle.
        }

        public override void EnableBlend(bool enable)
        {
            // Vulkan: blend enable is per-pipeline state; no global toggle.
        }

        public override void BlendFunc(EBlendingFactor src, EBlendingFactor dst)
        {
            // Vulkan: blend factors are per-pipeline state; no global toggle.
        }

        public override void BlendFuncSeparate(EBlendingFactor srcRGB, EBlendingFactor dstRGB, EBlendingFactor srcAlpha, EBlendingFactor dstAlpha)
        {
            // Vulkan: blend factors are per-pipeline state; no global toggle.
        }

        public override void BlendEquation(EBlendEquationMode mode)
        {
            // Vulkan: blend equation is per-pipeline state; no global toggle.
        }

        public override void BlendEquationSeparate(EBlendEquationMode modeRGB, EBlendEquationMode modeAlpha)
        {
            // Vulkan: blend equation is per-pipeline state; no global toggle.
        }

        public override void EnableSampleShading(float minValue)
        {
            // Vulkan: sample shading is configured per-pipeline, not a global state toggle.
            // Per-pipeline configuration would happen in VkMeshRenderer.Pipeline.cs.
        }
        public override void DisableSampleShading()
        {
            // Vulkan: sample shading is configured per-pipeline, not a global state toggle.
        }
        public override void AllowDepthWrite(bool v)
        {
            ActiveState.SetDepthWriteEnabled(v);
        }
        public override void ClearDepth(float v)
        {
            ActiveState.SetClearDepth(v);
        }
        public override void ClearStencil(int v)
        {
            ActiveState.SetClearStencil(v);
        }
        public override void EnableDepthTest(bool v)
        {
            ActiveState.SetDepthTestEnabled(v);
        }
        public override void DepthFunc(EComparison always)
        {
            ActiveState.SetDepthCompare(ToVulkanCompareOp(always));
        }
        public override void DispatchCompute(XRRenderProgram program, int numGroupsX, int numGroupsY, int numGroupsZ)
        {
            if (program is null)
                return;

            uint x = (uint)Math.Max(numGroupsX, 1);
            uint y = (uint)Math.Max(numGroupsY, 1);
            uint z = (uint)Math.Max(numGroupsZ, 1);

            if (GetOrCreateAPIRenderObject(program) is not VkRenderProgram vkProgram)
            {
                Debug.VulkanWarning("DispatchCompute skipped: program could not be resolved to VkRenderProgram.");
                return;
            }

            vkProgram.Generate();
            if (!vkProgram.Link())
            {
                Debug.VulkanWarning($"DispatchCompute skipped: failed to link program '{program.Name ?? "UnnamedProgram"}'.");
                return;
            }

            FrameOpContext context = CaptureFrameOpContextOrLastActive();
            string programName = string.IsNullOrWhiteSpace(program.Name) ? "UnnamedProgram" : program.Name;
            string opName = $"DispatchCompute:{programName}";
            int passIndex = EnsureValidPassIndex(
                RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
                opName,
                context.PassMetadata);
            if (passIndex == int.MinValue)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.DispatchCompute.NoPass.{programName}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] DispatchCompute skipped for '{0}' because no active render-graph pass could be resolved.",
                    programName);
                return;
            }

            EnqueueFrameOp(new ComputeDispatchOp(
                passIndex,
                vkProgram,
                x,
                y,
                z,
                vkProgram.CaptureComputeSnapshot(),
                context));
        }
        public override void WaitForGpu()
        {
            DeviceWaitIdle();
        }
        public override void SetReadBuffer(EReadBufferMode mode)
        {
            ActiveReadBufferMode = mode;
        }
        public override void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode)
        {
            ActiveBoundReadFrameBuffer = fbo;
            ActiveReadBufferMode = mode;

            if (fbo is not null)
            {
                EnsureFrameBufferRegistered(fbo);
                EnsureFrameBufferAttachmentsRegistered(fbo);

                if (GetOrCreateAPIRenderObject(fbo, generateNow: true) is VkFrameBuffer vkFrameBuffer)
                    vkFrameBuffer.Generate();
            }
        }
        public override void TrackWindowPresentSource(XRTexture? colorTexture, XRFrameBuffer? sourceFrameBuffer)
        {
            _lastWindowPresentColorTexture = colorTexture;
            _lastWindowPresentFrameBuffer = sourceFrameBuffer ?? ResolveWindowPresentFallbackFrameBuffer(colorTexture);
            _lastWindowPresentFrameOpContext = CaptureFrameOpContext();
        }

        public override bool IsTextureReadyForShaderSampling(XRTexture? texture)
        {
            if (texture is null)
                return false;

            if (GetOrCreateAPIRenderObject(texture, generateNow: false) is not IVkImageDescriptorSource source)
                return false;

            if (!source.TryGetDescriptorSnapshot(
                    requestedViewType: null,
                    requestedAspectMask: null,
                    "shader sampling readiness",
                    allowSynchronousUpload: false,
                    out VkImageDescriptorSnapshot snapshot))
                return false;

            return snapshot.View.Handle != 0 &&
                IsLiveImageViewBackedByLiveImage(snapshot.View) &&
                (snapshot.Usage & ImageUsageFlags.SampledBit) != 0;
        }

        private XRFrameBuffer? ResolveWindowPresentFallbackFrameBuffer(XRTexture? colorTexture)
        {
            if (colorTexture is not IFrameBufferAttachement attachment)
                return null;

            if (!ReferenceEquals(_lastWindowPresentFallbackFrameBufferTexture, colorTexture))
            {
                _lastWindowPresentFallbackFrameBuffer = new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = $"{colorTexture.Name ?? "WindowPresentSource"}FBO"
                };
                _lastWindowPresentFallbackFrameBufferTexture = colorTexture;
            }

            return _lastWindowPresentFallbackFrameBuffer;
        }
        public override void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
        {
            switch (fboTarget)
            {
                case EFramebufferTarget.Framebuffer:
                    ActiveBoundReadFrameBuffer = fbo;
                    ActiveBoundDrawFrameBuffer = fbo;
                    break;
                case EFramebufferTarget.ReadFramebuffer:
                    ActiveBoundReadFrameBuffer = fbo;
                    break;
                case EFramebufferTarget.DrawFramebuffer:
                    ActiveBoundDrawFrameBuffer = fbo;
                    break;
                default:
                    return;
            }

            XRFrameBuffer? boundDrawFrameBuffer = ActiveBoundDrawFrameBuffer;
            if (boundDrawFrameBuffer is null)
            {
                if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
                    ActiveState.SetCurrentTargetExtent(externalExtent);
                else
                    ActiveState.SetCurrentTargetExtent(swapChainExtent);
            }
            else
            {
                ActiveState.SetCurrentTargetExtent(new Extent2D(Math.Max(boundDrawFrameBuffer.Width, 1u), Math.Max(boundDrawFrameBuffer.Height, 1u)));
            }

            if (fbo is not null)
            {
                EnsureFrameBufferRegistered(fbo);
                EnsureFrameBufferAttachmentsRegistered(fbo);

                if (GetOrCreateAPIRenderObject(fbo, generateNow: true) is VkFrameBuffer vkFrameBuffer)
                    vkFrameBuffer.Generate();
            }
        }
        public override void Clear(bool color, bool depth, bool stencil)
        {
            // Don't enqueue clear ops when there's no active rendering pipeline;
            // they would be emitted with an invalid pass index and dropped at recording time.
            if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null)
                return;

            ActiveState.SetClearState(color, depth, stencil);

            FrameOpContext context = CaptureFrameOpContext();
            int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
            XRFrameBuffer? target = ResolveCurrentFrameOpDrawTarget();
            Extent2D clearTargetExtent = ResolveCurrentDrawTargetExtent();
            Rect2D rect = ActiveState.GetCroppingEnabled()
                ? ActiveState.GetScissor(clearTargetExtent)
                : new Rect2D(new Offset2D(0, 0), clearTargetExtent);

            EnqueueFrameOp(new ClearOp(
                EnsureValidPassIndex(passIndex, "Clear", context.PassMetadata),
                target,
                color,
                depth,
                stencil,
                ActiveState.GetClearColorValue(),
                ActiveState.GetClearDepthValue(),
                ActiveState.GetClearStencilValue(),
                rect,
                context));
        }
        public override byte GetStencilIndex(float x, float y)
        {
            XRFrameBuffer? fbo = GetCurrentReadFrameBuffer() ?? GetCurrentDrawFrameBuffer();
            int sampleX;
            int sampleY;

            if (fbo is not null)
            {
                sampleX = Math.Clamp((int)x, 0, Math.Max((int)fbo.Width - 1, 0));
                sampleY = Math.Clamp((int)y, 0, Math.Max((int)fbo.Height - 1, 0));

                if (TryResolveBlitImage(
                        fbo,
                        _lastPresentedImageIndex,
                        GetReadBufferMode(),
                        wantColor: false,
                        wantDepth: false,
                        wantStencil: true,
                        out BlitImageInfo stencilSource,
                        isSource: true) &&
                    TryReadStencilPixel(stencilSource, sampleX, sampleY, out byte stencilValue))
                {
                    return stencilValue;
                }
            }

            if (_swapchainDepthImage.Handle == 0)
                return 0;

            sampleX = Math.Clamp((int)x, 0, Math.Max((int)swapChainExtent.Width - 1, 0));
            sampleY = Math.Clamp((int)y, 0, Math.Max((int)swapChainExtent.Height - 1, 0));

            BlitImageInfo swapchainStencilSource = ResolveSwapchainBlitImage(
                _lastPresentedImageIndex,
                wantColor: false,
                wantDepth: false,
                wantStencil: true);

            if (!swapchainStencilSource.IsValid)
                return 0;

            return TryReadStencilPixel(swapchainStencilSource, sampleX, sampleY, out byte swapchainStencil)
                ? swapchainStencil
                : (byte)0;
        }
        public override void SetCroppingEnabled(bool enabled)
        {
            ActiveState.SetCroppingEnabled(enabled);
        }

        public void DeviceWaitIdle()
        {
            lock (_oneTimeSubmitLock)
            {
                if (!IsDeviceOperational)
                    return;

                Result result = Api!.DeviceWaitIdle(device);
                if (result == Result.Success)
                {
                    NotifyVulkanDeviceIdle();
                }
                else if (result == Result.ErrorDeviceLost)
                {
                    MarkDeviceLost("DeviceWaitIdle returned ErrorDeviceLost");
                    Debug.VulkanWarning("[Vulkan] DeviceWaitIdle returned ErrorDeviceLost. Device state is irrecoverable.");
                // Don't throw — allow callers (e.g. RecreateSwapChain) to proceed with
                // teardown/recreation even after the device is lost, rather than getting
                // stuck in an infinite exception loop.
                }
            }
        }

        public bool SupportsMultipleGraphicsQueues()
        {
            return HasSecondaryGraphicsQueue;
        }

        private static CompareOp ToVulkanCompareOp(EComparison comparison)
            => comparison switch
            {
                EComparison.Never => CompareOp.Never,
                EComparison.Less => CompareOp.Less,
                EComparison.Equal => CompareOp.Equal,
                EComparison.Lequal => CompareOp.LessOrEqual,
                EComparison.Greater => CompareOp.Greater,
                EComparison.Nequal => CompareOp.NotEqual,
                EComparison.Gequal => CompareOp.GreaterOrEqual,
                EComparison.Always => CompareOp.Always,
                _ => CompareOp.Always
            };
    }
}
