using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    public sealed unsafe partial class VulkanRenderer :
        AbstractRenderer<Vk>,
        ISparseTextureStreamingBackendCapability,
        IStreamlinePresentationBackendCapability
    {
        private readonly VulkanDeviceContext _deviceContext;
        private readonly VulkanOutputRuntime _outputRuntime;
        private readonly VulkanFrameLoop _frameLoop;
        private readonly VulkanFramePlanner _framePlanner = new();
        private readonly VulkanResourceRuntime _resourceRuntime = new(MAX_FRAMES_IN_FLIGHT);
        private readonly VulkanCommandRuntime _commandRuntime = new();
        private readonly VulkanFrameTelemetry _frameTelemetry = new();
        private readonly VulkanTextureReadbackService _textureReadbackService;

        public VulkanRenderer(
            XRWindow window,
            bool shouldLinkWindow = true,
            long backendGeneration = 0)
            : this(RendererHostContext.CreateDesktop(window, shouldLinkWindow, backendGeneration))
        {
        }

        public VulkanRenderer(RendererHostContext hostContext)
            : base(hostContext)
        {
            IVulkanRendererTargetDriver targetDriver = VulkanRendererTargetDriverFactory.Create(hostContext);
            _outputRuntime = new VulkanOutputRuntime(targetDriver);
            _deviceContext = new VulkanDeviceContext(
                new VulkanDeviceContextConfiguration(
                    targetDriver.RequiresPresentQueue,
                    targetDriver.RequiresSwapchainOutput,
                    targetDriver.RequiredDeviceExtensions,
                    OptionalDeviceExtensions));
            _commandRuntime.ConfigurePrimaryRecording(
                _deviceContext,
                _resourceRuntime,
                _frameTelemetry);
            _resourceRuntime.DescriptorLifetime.Configure(
                _deviceContext,
                _commandRuntime,
                _frameTelemetry);
            _resourceRuntime.FallbackTexture.Configure(_commandRuntime);
            _textureReadbackService = new VulkanTextureReadbackService(
                _deviceContext,
                _resourceRuntime,
                _commandRuntime,
                _framePlanner,
                _frameTelemetry);
            _frameLoop = new VulkanFrameLoop(
                _deviceContext,
                _outputRuntime,
                _framePlanner,
                _resourceRuntime,
                _commandRuntime,
                _frameTelemetry,
                _textureReadbackService);
            VulkanBackendObjectContext backendObjectContext = ResourceRuntime.GetOrCreateBackendObjectContext(
                Api!,
                _deviceContext,
                _commandRuntime,
                _framePlanner,
                _frameTelemetry,
                AllowSynchronousResourceUploads);
            backendObjectContext.ConfigureMeshServices(
                _commandRuntime,
                _framePlanner,
                _outputRuntime,
                _frameLoop,
                _frameOperationQueue,
                _frameTelemetry);
            _framePlanner.PublishResourcePlannerGeneration(
                new ResourcePlannerRuntimeGeneration(ResourcePlannerRuntimeState.CreateEmpty()));
        }

        /// <summary>Executes one frame through the composed frame-loop authority.</summary>
        protected override void RenderFrameCallback(double delta)
            => RenderComposedFrame(delta);

        internal Vk VulkanApi => Api!;
        internal string TargetDriverName => OutputRuntime.TargetDriver.GetType().Name;
        internal bool TargetRequiresPresentQueue => OutputRuntime.TargetDriver.RequiresPresentQueue;
        internal bool TargetRequiresSwapchainOutput => OutputRuntime.TargetDriver.RequiresSwapchainOutput;
        internal bool HasInitializedMemoryAllocator => ResourceRuntime.Allocations.Buffers.MemoryAllocator is not null;
        internal bool HasExplicitFrameTarget => OutputRuntime.TargetDriver is IVulkanExplicitFrameTargetDriver;
        private VulkanBindlessMaterialTextureTableState BindlessMaterialTextureTableState
            => ResourceRuntime.Descriptors.BindlessMaterialTextures;

        public override RendererBackendId BackendId => RendererBackendId.Vulkan;

        protected override Vk GetAPI()
            => Vk.GetApi();

        public override void Initialize()
        {
            VulkanIndirectCommandLayoutContract.ValidateRuntimeLayout();

            if (OutputRuntime.TargetDriver.SupportsStreamlinePresentation)
                PrepareStreamlineVulkanRequirements();
            CreateInstance();
            SetupDebugMessenger();
            OutputRuntime.CreateTargetInstanceResources(Api!, _deviceContext, Window);
            if (OutputRuntime.TargetDriver.RequiresSwapchainOutput)
                OutputRuntime.InitializeDesktopSwapchainService(
                    VulkanApi,
                    _deviceContext,
                    _commandRuntime,
                    _resourceRuntime,
                    _frameTelemetry,
                    _framePlanner);
            PublishPresentationSupportProbe();
            PickPhysicalDevice();
            if (OutputRuntime.TargetDriver.SupportsStreamlinePresentation)
                ValidateStreamlineSelectedPhysicalDevice();
            CreateLogicalDevice();
            InitializeMemoryAllocator();
            VulkanTextureStreamingBackendProvider.Instance.BindScheduler(this);
            VulkanCanonicalImmutableSamplerService.Initialize(ResourceRuntime, Api!, _deviceContext);
            CreateCommandPool();

            VulkanRootDescriptorLayoutService.Create(ResourceRuntime, Api!, _deviceContext.Device);
            OutputRuntime.InitializeTargetFinalOutput(
                VulkanApi,
                _deviceContext,
                _commandRuntime,
                _resourceRuntime,
                _frameTelemetry);
            if (OutputRuntime.TargetDriver is VulkanDesktopWsiTargetDriver)
            {
                OutputRuntime.CreateInitialDesktopSwapchainGeneration();
            }

            //CreateTestModel();
            //CreateUniformBuffers();

            CreateSyncObjects();
            CreateFrameTimingResources();
            InitializeSynchronizationBackend();
            LogStartupCapabilitySnapshot();
            InitializeMappedFrameArena();
            ReserveOpenXrFrameDataSlotsIfRequired("initialization");
            int deferredProgramLinkCount = ResourceRuntime.PipelineManager.FlushPendingDeviceReadyProgramLinks();
            if (deferredProgramLinkCount > 0)
            {
                Debug.Vulkan(
                    $"Deferred {deferredProgramLinkCount} Vulkan program link(s) until first use after logical device creation.");
            }
        }

        /// <summary>
        /// Whether any device memory type supports <see cref="MemoryPropertyFlags.LazilyAllocatedBit"/>.
        /// True on most mobile/tiler GPUs; false on typical discrete desktop GPUs.
        /// </summary>
        private void InitializeMemoryAllocator()
        {
            // Probe for lazy allocation support (TransientAttachment optimization).
            Api!.GetPhysicalDeviceMemoryProperties(_deviceContext.PhysicalDevice, out PhysicalDeviceMemoryProperties memProps);
            for (int i = 0; i < memProps.MemoryTypeCount; i++)
            {
                if (memProps.MemoryTypes[i].PropertyFlags.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit))
                {
                    _deviceContext.MutableCapabilities.SupportsLazyAllocation = true;
                    break;
                }
            }

            EVulkanAllocatorBackend backend = RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.AllocatorBackend;
            ResourceRuntime.Allocations.Buffers.MemoryAllocator = backend switch
            {
                EVulkanAllocatorBackend.Legacy => new VulkanLegacyAllocator(_deviceContext),
                EVulkanAllocatorBackend.Managed => new VulkanBlockAllocator(_deviceContext),
                EVulkanAllocatorBackend.Vma => new VulkanVmaAllocator(
                    _deviceContext.Instance,
                    _deviceContext.PhysicalDevice,
                    _deviceContext.Device,
                    Vk.Version13,
                    SupportsBufferDeviceAddress),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(backend),
                    backend,
                    "Unknown Vulkan allocator backend.")
            };
            Debug.Vulkan($"[Vulkan] Memory allocator initialized: {backend} (lazyAlloc={_deviceContext.MutableCapabilities.SupportsLazyAllocation})");
        }

        internal void SubmitExplicitTargetFrame(Action<Vk, CommandBuffer, VulkanRenderFrameTarget> record)
            => ExecuteExplicitTargetFrame(record);

        internal byte[] ReadbackExplicitTargetColor(
            int maxByteCount,
            ImageLayout sourceLayout = ImageLayout.TransferSrcOptimal)
            => RequireExplicitFrameTarget().ReadbackLastSubmittedColor(maxByteCount, sourceLayout);

        internal string ComputeExplicitTargetColorHash(
            ImageLayout sourceLayout = ImageLayout.TransferSrcOptimal)
            => RequireExplicitFrameTarget().ComputeLastSubmittedColorHash(sourceLayout);

        internal RenderTargetOutputProperties ExplicitTargetOutputProperties
            => RequireExplicitFrameTarget().OutputProperties;

        internal ulong ExplicitTargetGeneration
            => RequireExplicitFrameTarget().TargetGeneration;

        internal double ExplicitTargetLastCompletedGpuFrameNanoseconds
            => RequireExplicitFrameTarget().LastCompletedGpuFrameNanoseconds;

        internal string ExplicitTargetPresentationDescription
            => RequireExplicitFrameTarget().PresentationDescription;

        internal bool ExplicitTargetIsDeviceLost
            => OutputRuntime.TargetDriver is IVulkanExplicitFrameTargetDriver explicitTarget &&
               explicitTarget.IsDeviceLost;

        private IVulkanExplicitFrameTargetDriver RequireExplicitFrameTarget()
            => OutputRuntime.TargetDriver as IVulkanExplicitFrameTargetDriver
                ?? throw new InvalidOperationException(
                    $"Vulkan target '{ExecutionMode}' does not expose explicit target-frame submission.");

        private VulkanDesktopWsiTargetDriver DesktopWsiTarget
            => OutputRuntime.TargetDriver as VulkanDesktopWsiTargetDriver
                ?? throw new InvalidOperationException(
                    $"Vulkan target '{ExecutionMode}' does not provide desktop WSI policy.");

        internal bool TryMapMemoryAllocation(
            VulkanMemoryAllocation allocation,
            ulong offset,
            ulong length,
            out void* mapped)
        {
            bool mappedSuccessfully = MemoryAllocator.TryMap(
                Api!,
                _deviceContext.Device,
                allocation,
                offset,
                length,
                out mapped,
                out Result result);
            if (!mappedSuccessfully)
                RecordAllocatorNativeFailure("vkMapMemory.Allocator", result);
            return mappedSuccessfully;
        }

        private void RecordAllocatorNativeFailure(string operation, Result result)
        {
            if (result != Result.ErrorDeviceLost)
                return;

            MarkDeviceLost(
                $"{operation} returned ErrorDeviceLost",
                operation,
                result);
        }

        internal void UnmapMemoryAllocation(VulkanMemoryAllocation allocation)
            => MemoryAllocator.Unmap(Api!, _deviceContext.Device, allocation);

        public override void CleanUp() => CleanUp(waitForGpu: true);

        public override void CleanUpAfterGpuIdle() => CleanUp(waitForGpu: false);

        private void CleanUp(bool waitForGpu)
        {
            if (_deviceContext.Device.Handle != 0)
            {
                if (waitForGpu)
                    DeviceWaitIdle();

                // Swapchain generations use nonblocking queue-marker fences during normal
                // rendering. The caller establishes the teardown-only GPU-idle boundary.
                OutputRuntime.DrainRetiredDesktopSwapchainGenerations(force: true);
            }

            bool forceRetirementDrain = IsDeviceLost;
            if (forceRetirementDrain)
                BeginForcedVulkanRetirementDrain();

            try
            {
            VulkanTextureStreamingBackendProvider.Instance.UnbindScheduler(this);
            ResourceRuntime.Uploads.CancelAllQueuedWork(this, "Vulkan renderer shutdown");
            CancelPendingImportedTextureUploadFrameOps("Vulkan renderer shutdown");
            CancelRecordedTextureUploadPublications("Vulkan renderer shutdown");
            ResourceRuntime.PipelineManager.DrainPipelineCompileQueueForShutdown();
            DrainScreenshotReadbacksForShutdown();
            _commandRuntime.CommandBuffers.ReadbackTasks.WaitForPendingTasks(TimeSpan.FromSeconds(6));
            DisposeScreenshotReadbacks();
            DisposeGpuRenderStatsReadbacks();
            DestroyComputeTransientResources();
            ResourceRuntime.RetireComputeDescriptorCachesForShutdown();
            DestroyDanglingMaterialWrappers();
            DestroyDanglingMeshRendererWrappers();
            DestroyDanglingRenderProgramPipelineWrappers();
            DestroyDanglingRenderProgramWrappers();
            DestroyDanglingDataBufferWrappers();
            DestroyDanglingFrameBufferWrappers();
            DestroyDanglingTextureWrappers();
            DestroyCachedAPIRenderObjects();
            ResourceRuntime.Queries.DisposeArenas();
            DestroyRemainingTrackedMeshUniformBuffers();

            // Drain all deferred-deletion queues now that the GPU is idle.
            ForceFlushAllRetiredResources();

            DestroyAutoExposureComputeResources();
            ResourceRuntime.FallbackTexture.RetireAll();
            _outputRuntime.DisposeImGuiResources(ResourceRuntime, _commandRuntime, _deviceContext);
            ResetImGuiFrameMarker();
            DestroyOpenXrRenderingResources();
            DestroyFrameOpResourcePlannerStates();
            if (OutputRuntime.TargetDriver is VulkanDesktopWsiTargetDriver)
                OutputRuntime.DestroyDesktopSwapchainGenerationForShutdown();
            OutputRuntime.DestroyTargetFinalOutput();
            // FBO render passes are NOT destroyed during swapchain recreation
            // (they are swapchain-independent). Clean them up here at full shutdown.
            ResourceRuntime.Framebuffers.DestroyRenderPasses(Api!, _deviceContext.Device);
            VulkanRootDescriptorLayoutService.Destroy(
                ResourceRuntime,
                Api!,
                _deviceContext.Device,
                _commandRuntime,
                CurrentDesktopFrameSlot);
            DestroyRetainedAutoExposureHistory("renderer shutdown");
            VulkanResourceAllocator resourceAllocator = CaptureResourcePlannerRuntimeState().ResourceAllocator;
            resourceAllocator.DestroyPhysicalImages(this);
            resourceAllocator.DestroyPhysicalBuffers(this);
            ResourceRuntime.Allocations.Staging.Destroy(this);
            DestroyMappedFrameArena();

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
            ResourceRuntime.Images.DestroyRemaining(Api!, _deviceContext.Device);
            ResourceRuntime.DestroyRemainingTrackedPipelineLayouts(Api!, _deviceContext.Device);
            DestroyRemainingTrackedBufferAllocations();
            DestroyRemainingTrackedImageAllocations();

            if (ResourceRuntime.Allocations.Buffers.MemoryAllocator is VulkanBlockAllocator blockAllocator)
                blockAllocator.DestroyAllBlocks(Api!, _deviceContext.Device);
            ResourceRuntime.Allocations.Buffers.MemoryAllocator?.Dispose();
            ResourceRuntime.Allocations.Buffers.MemoryAllocator = null;
            _commandRuntime.Synchronization._activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
            DestroyFrameTimingResources();

            DestroySyncObjects();
            DestroyCommandPool();

            // Flush once more before destroying the logical device to catch any
            // handles retired by sync/command-pool teardown.
            ForceFlushAllRetiredResources();
            ResourceRuntime.Images.DestroyRemaining(Api!, _deviceContext.Device);
            ResourceRuntime.DestroyRemainingDescriptorSetLayouts(
                Api!,
                _deviceContext.Device,
                _commandRuntime,
                CurrentDesktopFrameSlot);
            ResourceRuntime.PipelineManager.DestroySharedGraphicsPipelines();
            ResourceRuntime.DestroyRemainingTrackedPipelineLayouts(Api!, _deviceContext.Device);
            ResourceRuntime.PipelineManager.DestroySharedGraphicsPipelineLibraries();

            DestroyLogicalDevice();
            OutputRuntime.DestroyTargetInstanceResources(Api!, _deviceContext, Window);
            DestroyInstance();
            }
            finally
            {
                if (forceRetirementDrain)
                    EndForcedVulkanRetirementDrain();

                DisposeNativeApi();
                ReleaseHotReloadManagedCaches();
            }
        }

        private void DestroyDanglingMaterialWrappers()
        {
            var wrappers = ResourceRuntime.BackendObjects.Snapshot<XRMaterial>();
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
            var wrappers = ResourceRuntime.BackendObjects.Snapshot<XRMeshRenderer.BaseVersion>();
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
            var wrappers = ResourceRuntime.BackendObjects.Snapshot<XRRenderProgramPipeline>();
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
            var wrappers = ResourceRuntime.BackendObjects.Snapshot<XRRenderProgram>();
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
            var wrappers = ResourceRuntime.BackendObjects.Snapshot<XRDataBuffer>();
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
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRFrameBuffer>(), "framebuffer");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRRenderBuffer>(), "renderbuffer");
        }

        private void DestroyDanglingTextureWrappers()
        {
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTexture1D>(), "texture1D");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTexture1DArray>(), "texture1DArray");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTexture2D>(), "texture2D");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTexture2DArray>(), "texture2DArray");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTexture3D>(), "texture3D");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTextureCube>(), "textureCube");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTextureCubeArray>(), "textureCubeArray");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTextureRectangle>(), "textureRectangle");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTextureBuffer>(), "textureBuffer");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRTextureViewBase>(), "textureView");
            DestroyCachedWrappers(ResourceRuntime.BackendObjects.Snapshot<XRSampler>(), "sampler");
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
            => ResourceRuntime.Buffers.DestroyRemainingTrackedAllocations(
                BackendObjectContext);

        private void DestroyRemainingTrackedImageAllocations()
        {
            foreach (var pair in ResourceRuntime.Allocations.Images.Allocations.ToArray())
            {
                if (!ResourceRuntime.Allocations.Images.Allocations.TryRemove(pair.Key, out VulkanMemoryAllocation allocation))
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
            // Allocation admission must be checked at the native call boundary: a
            // device-loss transition can occur after a caller passed its create gate.
            ThrowIfDeviceLostForResourceCreation("vkAllocateMemory");
            Result result = Api!.AllocateMemory(_deviceContext.Device, ref allocInfo, null, memPtr);
            RecordAllocatorNativeFailure("vkAllocateMemory", result);
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
            ThrowIfDeviceLostForResourceCreation("AllocateBufferMemoryWithFallback.Initial");
            if (alloc.TryAllocateForBuffer(Api!, _deviceContext.Device, buffer, requiredProperties, out VulkanMemoryAllocation allocation, out Result initialResult))
                return allocation;
            RecordAllocatorNativeFailure("vkAllocateMemory.AllocatorBuffer.Initial", initialResult);
            ThrowIfDeviceLostForResourceCreation("vkAllocateMemory.AllocatorBuffer.Initial");

            // OOM — attempt fallback to host-visible if the original was device-local.
            if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
            {
                MemoryPropertyFlags fallback = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                Debug.VulkanWarning(
                    $"[Vulkan] OOM for buffer (requested {requiredProperties}). Falling back to {fallback}.");
                ThrowIfDeviceLostForResourceCreation("AllocateBufferMemoryWithFallback.Fallback");
                if (alloc.TryAllocateForBuffer(Api!, _deviceContext.Device, buffer, fallback, out allocation, out Result fallbackResult))
                {
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanOomFallback();
                    return allocation;
                }
                RecordAllocatorNativeFailure("vkAllocateMemory.AllocatorBuffer.Fallback", fallbackResult);
                ThrowIfDeviceLostForResourceCreation("vkAllocateMemory.AllocatorBuffer.Fallback");
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
            if (requiredProperties.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit) && !_deviceContext.MutableCapabilities.SupportsLazyAllocation)
                requiredProperties &= ~MemoryPropertyFlags.LazilyAllocatedBit;

            if (ShouldDeferVulkanImageMemoryAllocationForPressure(
                    image,
                    requiredProperties,
                    out failureReason))
            {
                return false;
            }

            ThrowIfDeviceLostForResourceCreation("TryAllocateImageMemoryWithFallback.Initial");
            if (alloc.TryAllocateForImage(Api!, _deviceContext.Device, image, requiredProperties, out allocation, out Result initialResult))
            {
                failureReason = string.Empty;
                return true;
            }
            RecordAllocatorNativeFailure("vkAllocateMemory.AllocatorImage.Initial", initialResult);
            ThrowIfDeviceLostForResourceCreation("vkAllocateMemory.AllocatorImage.Initial");

            // If lazy was requested, retry without it (device-local only).
            if (requiredProperties.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit))
            {
                MemoryPropertyFlags withoutLazy = requiredProperties & ~MemoryPropertyFlags.LazilyAllocatedBit;
                ThrowIfDeviceLostForResourceCreation("TryAllocateImageMemoryWithFallback.WithoutLazy");
                if (alloc.TryAllocateForImage(Api!, _deviceContext.Device, image, withoutLazy, out allocation, out Result withoutLazyResult))
                {
                    Debug.VulkanWarning(
                        $"[Vulkan] Image allocation requested {requiredProperties} but lazy allocation failed; falling back to {withoutLazy}.");
                    failureReason = string.Empty;
                    return true;
                }
                RecordAllocatorNativeFailure("vkAllocateMemory.AllocatorImage.WithoutLazy", withoutLazyResult);
                ThrowIfDeviceLostForResourceCreation("vkAllocateMemory.AllocatorImage.WithoutLazy");
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
                _deviceContext.Device.Handle == 0 ||
                image.Handle == 0)
            {
                return false;
            }

            IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
            if (!presentation.IsOpenXRActive && !presentation.IsInVR)
                return false;

            Api.GetImageMemoryRequirements(_deviceContext.Device, image, out MemoryRequirements requirements);
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

            IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
            if (!presentation.IsOpenXRActive && !presentation.IsInVR)
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

            IRuntimeRenderFrameTimingServices frameTiming = RuntimeRenderingHostServices.FrameTiming;
            trackedVramBytes = Math.Max(0L, frameTiming.TrackedVramBytes);
            trackedVramDeferLimitBytes = ResolveOpenXrVulkanImageAllocationTrackedVramLimit(frameTiming.TrackedVramBudgetBytes);
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

            if (Api is null || _deviceContext.PhysicalDevice.Handle == 0)
                return false;

            Api.GetPhysicalDeviceProperties(_deviceContext.PhysicalDevice, out PhysicalDeviceProperties properties);
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
            MemoryAllocator.Free(Api!, _deviceContext.Device, allocation);
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
            => TryDispatchCompute(
                program,
                checked((uint)Math.Max(numGroupsX, 1)),
                checked((uint)Math.Max(numGroupsY, 1)),
                checked((uint)Math.Max(numGroupsZ, 1)));

        public override ERendererComputeEnqueueStatus TryDispatchCompute(
            XRRenderProgram program,
            uint groupsX,
            uint groupsY,
            uint groupsZ)
        {
            if (!_deviceContext.IsOperational)
                return ERendererComputeEnqueueStatus.DeviceLost;
            if (program is null)
                return ERendererComputeEnqueueStatus.InvalidResource;

            uint x = Math.Max(groupsX, 1u);
            uint y = Math.Max(groupsY, 1u);
            uint z = Math.Max(groupsZ, 1u);

            if (GetOrCreateAPIRenderObject(program) is not VkRenderProgram vkProgram)
            {
                Debug.VulkanWarning("DispatchCompute skipped: program could not be resolved to VkRenderProgram.");
                return ERendererComputeEnqueueStatus.InvalidResource;
            }

            vkProgram.Generate();
            if (!vkProgram.Link(program.AllowAsyncBackendCompile))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.DispatchCompute.ProgramPending.{RuntimeHelpers.GetHashCode(program)}",
                    TimeSpan.FromSeconds(1),
                    "DispatchCompute deferred: program '{0}' is not ready.",
                    program.Name ?? "UnnamedProgram");
                return ERendererComputeEnqueueStatus.ProgramPending;
            }

            FrameOpContext context = CaptureFrameOpContextOrLastActive();
            string programName = string.IsNullOrWhiteSpace(program.Name) ? "UnnamedProgram" : program.Name;
            string opName = _frameTelemetry.ComputeDispatchOperationNames.GetOrAdd(
                programName,
                static name => string.Concat("DispatchCompute:", name));
            int passIndex = ResolveOrderedPrimaryWorkPassIndex(opName, context.PassMetadata);
            if (passIndex == int.MinValue)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.DispatchCompute.NoPass.{programName}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] DispatchCompute skipped for '{0}' because no active render-graph pass could be resolved.",
                    programName);
                return ERendererComputeEnqueueStatus.NoPassContext;
            }

            ComputeDispatchSnapshot snapshot = vkProgram.CaptureComputeSnapshot();
            if (!vkProgram.ValidateComputeSnapshot(snapshot, out string? descriptorFailure))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.DispatchCompute.DescriptorInvalid.{RuntimeHelpers.GetHashCode(program)}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] DispatchCompute skipped for '{0}' because its descriptor snapshot is invalid: {1}",
                    programName,
                    descriptorFailure ?? "unknown descriptor failure");
                return ERendererComputeEnqueueStatus.DescriptorInvalid;
            }

            try
            {
                if (vkProgram.GetOrCreateComputePipeline(passIndex, context.PassMetadata).Handle == 0)
                    return ERendererComputeEnqueueStatus.ProgramPending;
            }
            catch (Exception ex)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.DispatchCompute.PipelinePending.{RuntimeHelpers.GetHashCode(program)}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] DispatchCompute deferred for '{0}' because pipeline creation failed: {1}",
                    programName,
                    ex.Message);
                return ERendererComputeEnqueueStatus.ProgramPending;
            }

            EnqueueFrameOp(ComputeDispatchOp.Rent(
                passIndex,
                vkProgram,
                x,
                y,
                z,
                snapshot,
                context));
            return ERendererComputeEnqueueStatus.Enqueued;
        }
        public override void WaitForGpu()
        {
            DeviceWaitIdle();
        }

        public override bool TryWaitForGpu(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The shutdown timeout must be non-negative.");
            if (_deviceContext.Device.Handle == 0)
                return true;

            Exception? waitFailure = null;
            var waitThread = new Thread(() =>
            {
                try
                {
                    DeviceWaitIdle();
                }
                catch (Exception ex)
                {
                    waitFailure = ex;
                }
            })
            {
                Name = "XRE-VulkanShutdownWait",
                IsBackground = true,
            };

            waitThread.Start();
            TimeSpan maximumJoinTimeout = TimeSpan.FromMilliseconds(int.MaxValue);
            bool completed = waitThread.Join(timeout > maximumJoinTimeout ? maximumJoinTimeout : timeout);
            if (waitFailure is not null)
                throw new InvalidOperationException("Vulkan device-idle wait failed during shutdown.", waitFailure);
            return completed;
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
                if (GetOrCreateAPIRenderObject(fbo, generateNow: true) is VkFrameBuffer vkFrameBuffer)
                    vkFrameBuffer.Generate();
            }
        }
        public override void TrackWindowPresentSource(XRTexture? colorTexture, XRFrameBuffer? sourceFrameBuffer)
        {
            XRFrameBuffer? resolvedFrameBuffer =
                sourceFrameBuffer ?? ResolveWindowPresentFallbackFrameBuffer(colorTexture);
            FrameOpContext context = CaptureFrameOpContext();
            VkImageDescriptorSnapshot snapshot = default;
            bool snapshotReady =
                colorTexture is not null &&
                GetOrCreateAPIRenderObject(
                    colorTexture,
                    generateNow: false) is IVkImageDescriptorSource source &&
                source.TryGetDescriptorSnapshot(
                    requestedViewType: null,
                    requestedAspectMask: null,
                    "window presentation source publication",
                    allowSynchronousUpload: false,
                    out snapshot);

            VulkanPresentationSourceTuple published = _windowPresentSource.PublishLogical(
                new VulkanPresentationSourceTuple(
                    LogicalEpoch: 0,
                    colorTexture,
                    resolvedFrameBuffer,
                    context,
                    DescriptorResourceEpoch:
                        snapshotReady ? snapshot.Generation : 0,
                    snapshotReady ? snapshot.Image : default,
                    ImageAllocationGeneration: snapshotReady
                        ? GetCurrentVulkanResourceGeneration(
                            ObjectType.Image,
                            snapshot.Image.Handle)
                        : 0,
                    snapshotReady ? snapshot.View : default,
                    ImageViewGeneration: snapshotReady
                        ? GetCurrentVulkanResourceGeneration(
                            ObjectType.ImageView,
                            snapshot.View.Handle)
                        : 0,
                    snapshotReady ? snapshot.Sampler : default,
                    SamplerGeneration: snapshotReady
                        ? GetCurrentVulkanResourceGeneration(
                            ObjectType.Sampler,
                            snapshot.Sampler.Handle)
                        : 0,
                    snapshotReady ? snapshot.Format : default,
                    snapshotReady ? snapshot.Aspect : default,
                    snapshotReady ? snapshot.Samples : default,
                    snapshotReady ? snapshot.TrackedLayout : ImageLayout.Undefined,
                    resolvedFrameBuffer?.Width ?? 0,
                    resolvedFrameBuffer?.Height ?? 0,
                    DescriptorSet: default,
                    DescriptorSetGeneration: 0,
                    DescriptorSlot: -1,
                    DescriptorPublicationGeneration: 0,
                    OwningCommandArtifact: default,
                    OwningCommandArtifactGeneration: 0),
                // A logical epoch describes source identity, not frame identity.
                // Re-publishing an unchanged source every frame used to clear the
                // descriptor-slot bindings that associate reusable primaries with
                // their immutable present descriptor. Cached descriptor sets do
                // not issue another write, so those primaries were then rejected
                // as having an incomplete presentation source and re-recorded.
                // Preserve the epoch until the logical/native source really
                // changes; PublishLogical still advances it for image/view/
                // sampler, extent, pipeline, viewport, or registry changes.
                retainEquivalentCurrentSource: true);

            // Transitional readback consumers are migrated separately, but all
            // command selection and submission consume the tuple above.
            _lastWindowPresentColorTexture = published.ColorTexture;
            _lastWindowPresentFrameBuffer = published.FrameBuffer;
            _lastWindowPresentFrameOpContext = published.Context;
        }

        public override RenderTextureSamplingState GetTextureShaderSamplingState(
            XRTexture? texture)
        {
            if (texture is null)
                return default;

            if (GetOrCreateAPIRenderObject(texture, generateNow: false) is not IVkImageDescriptorSource source)
                return default;

            bool descriptorReady = source.TryGetDescriptorSnapshot(
                    requestedViewType: null,
                    requestedAspectMask: null,
                    "shader sampling readiness",
                    allowSynchronousUpload: false,
                    out VkImageDescriptorSnapshot snapshot);

            bool isReady = descriptorReady &&
                snapshot.View.Handle != 0 &&
                ResourceRuntime.Images.IsLiveBackedByLiveImage(snapshot.View) &&
                (snapshot.Usage & ImageUsageFlags.SampledBit) != 0;
            ulong descriptorGeneration = descriptorReady
                ? snapshot.Generation
                : source.DescriptorGeneration;
            if (DescriptorTraceEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.Descriptor.SamplingState.{texture.GetHashCode()}.{snapshot.Generation}.{snapshot.Image.Handle}.{snapshot.View.Handle}.{snapshot.Sampler.Handle}.{isReady}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanDescriptor] sampling-state texture='{0}' ready={1} descriptorReady={2} generation={3} image=0x{4:X} view=0x{5:X} sampler=0x{6:X} usage={7}.",
                    texture.Name ?? texture.GetDescribingName(),
                    isReady,
                    descriptorReady,
                    descriptorGeneration,
                    snapshot.Image.Handle,
                    snapshot.View.Handle,
                    snapshot.Sampler.Handle,
                    snapshot.Usage);
            }
            return RenderTextureSamplingState.FromBackendGeneration(
                isReady,
                descriptorGeneration);
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
                    ActiveState.SetCurrentTargetExtent(OutputRuntime.Desktop.Extent);
            }
            else
            {
                ActiveState.SetCurrentTargetExtent(new Extent2D(Math.Max(boundDrawFrameBuffer.Width, 1u), Math.Max(boundDrawFrameBuffer.Height, 1u)));
            }

            if (fbo is not null)
            {
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

            EnqueueFrameOp(ClearOp.Rent(
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
                        OutputRuntime.Desktop.LastPresentedImageIndex,
                        GetReadBufferMode(),
                        wantColor: false,
                        wantDepth: false,
                        wantStencil: true,
                        out BlitImageInfo stencilSource,
                        isSource: true) &&
                    _commandRuntime.TryReadStencilPixel(stencilSource, sampleX, sampleY, out byte stencilValue))
                {
                    return stencilValue;
                }
            }

            if (_swapchainDepthImage.Handle == 0)
                return 0;

            sampleX = Math.Clamp((int)x, 0, Math.Max((int)OutputRuntime.Desktop.Extent.Width - 1, 0));
            sampleY = Math.Clamp((int)y, 0, Math.Max((int)OutputRuntime.Desktop.Extent.Height - 1, 0));

            BlitImageInfo swapchainStencilSource = ResolveSwapchainBlitImage(
                OutputRuntime.Desktop.LastPresentedImageIndex,
                wantColor: false,
                wantDepth: false,
                wantStencil: true);

            if (!swapchainStencilSource.IsValid)
                return 0;

            return _commandRuntime.TryReadStencilPixel(swapchainStencilSource, sampleX, sampleY, out byte swapchainStencil)
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
                if (!_deviceContext.IsOperational)
                    return;

                Result result = Api!.DeviceWaitIdle(_deviceContext.Device);
                if (result == Result.Success)
                {
                    NotifyVulkanDeviceIdle();
                }
                else if (result == Result.ErrorDeviceLost)
                {
                    MarkDeviceLost(
                        "DeviceWaitIdle returned ErrorDeviceLost",
                        "vkDeviceWaitIdle",
                        result);
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

        internal static CompareOp ToVulkanCompareOp(EComparison comparison)
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
