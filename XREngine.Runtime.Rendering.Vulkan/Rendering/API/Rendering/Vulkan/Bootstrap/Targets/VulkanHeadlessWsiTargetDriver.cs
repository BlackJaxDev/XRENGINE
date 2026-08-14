using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns a real <c>VK_EXT_headless_surface</c> swapchain. Presentation executes
/// the WSI acquire/present protocol but has no desktop compositor output.
/// </summary>
internal sealed unsafe class VulkanHeadlessWsiTargetDriver :
    IVulkanRendererTargetDriver,
    IVulkanExplicitFrameTargetDriver
{
    private readonly RenderTargetOutputProperties _output;
    // This is the target's scoped output capability, never a renderer facade.
    private VulkanTargetOutputContext? _targetContext;
    private KhrSwapchain? _swapchainApi;
    private SwapchainKHR _swapchain;
    private Image[] _images = [];
    private ImageView[] _imageViews = [];
    private bool[] _imagePresented = [];
    private Semaphore[] _renderFinishedSemaphores = [];
    private VulkanHeadlessWsiFrameSlot[] _slots = [];
    private int _nextSlot;
    private bool _deviceLost;

    public VulkanHeadlessWsiTargetDriver(RendererHostContext hostContext)
    {
        _output = hostContext.OutputProperties
            ?? throw new InvalidOperationException("A headless WSI Vulkan target requires fixed output properties.");
        _output.Validate();
        if (_output.SampleCount != 1)
        {
            throw new NotSupportedException(
                "Headless WSI swapchain images are single-sample. Resolve multisample output before presentation.");
        }

        VulkanHeadlessWsiProbeResult probe = VulkanHeadlessWsiSupport.Probe();
        if (!probe.Supported)
            throw new NotSupportedException(probe.Message);
    }

    public RenderExecutionMode ExecutionMode => RenderExecutionMode.HeadlessWsi;
    public bool RequiresPresentQueue => true;
    public bool RequiresSwapchainOutput => true;
    public bool SupportsStreamlinePresentation => false;
    public IReadOnlyList<string> RequiredDeviceExtensions { get; } = [KhrSwapchain.ExtensionName];
    public RenderTargetOutputProperties OutputProperties => _output;
    public ulong TargetGeneration { get; private set; }
    public bool IsDeviceLost => _deviceLost;
    public double LastCompletedGpuFrameNanoseconds => 0;
    public string PresentationDescription => "VK_EXT_headless_surface acquire/present; presentation is a headless WSI no-op.";

    public string[] GetRequiredInstanceExtensions()
        => [KhrSurface.ExtensionName, VulkanHeadlessWsiSupport.ExtensionName];

    public void CreateInstanceResources(VulkanTargetSurfaceAuthority surfaces)
        => surfaces.CreateHeadlessSurface();

    public void InitializeFinalOutput(VulkanTargetOutputContext output)
    {
        VulkanTargetOutputContext renderer = output;
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("HeadlessWsi.InitializeFinalOutput");
        _targetContext = output;
        if (!renderer.VulkanApi.TryGetDeviceExtension(
                renderer.Instance,
                renderer.Device,
                out _swapchainApi))
        {
            throw new NotSupportedException(
                "VK_KHR_swapchain was enabled but its device entry points are unavailable.");
        }

        try
        {
            CreateSwapchain(renderer);
            _renderFinishedSemaphores = CreateRenderFinishedSemaphores(renderer, _images.Length);
            _slots = new VulkanHeadlessWsiFrameSlot[checked((int)_output.FrameSlotCount)];
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = CreateFrameSlot(renderer, i);
            TargetGeneration = 1;
        }
        catch
        {
            DestroyFinalOutput(renderer);
            throw;
        }
    }

    public void DestroyFinalOutput(VulkanTargetOutputContext output)
    {
        VulkanTargetOutputContext renderer = output;
        for (int i = 0; i < _slots.Length; i++)
            DestroyFrameSlot(renderer, in _slots[i]);
        _slots = [];

        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
            if (_renderFinishedSemaphores[i].Handle != 0)
                api.DestroySemaphore(device, _renderFinishedSemaphores[i], null);
        _renderFinishedSemaphores = [];

        for (int i = 0; i < _imageViews.Length; i++)
            if (_imageViews[i].Handle != 0)
            {
                if (renderer.TryBeginDestroyImageView(_imageViews[i], "HeadlessWsiTarget.SwapchainColorView"))
                    api.DestroyImageView(device, _imageViews[i], null);
            }
        _imageViews = [];
        _images = [];
        _imagePresented = [];

        if (_swapchain.Handle != 0)
            _swapchainApi?.DestroySwapchain(device, _swapchain, null);
        _swapchain = default;
        _swapchainApi = null;
        _targetContext = null;
        _nextSlot = 0;
        TargetGeneration = 0;
    }

    public void DestroyInstanceResources(VulkanTargetSurfaceAuthority surfaces)
        => surfaces.DestroyHeadlessSurface();

    public VulkanFrameTargetLease AcquireFrameTarget(
        out CommandBuffer commandBuffer)
    {
        VulkanTargetOutputContext renderer = RequireTargetContext();
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("HeadlessWsi.AcquireFrameTarget");
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        int slotIndex = _nextSlot;
        ref VulkanHeadlessWsiFrameSlot slot = ref _slots[slotIndex];
        uint frameSlotIndex = checked((uint)_nextSlot);
        _nextSlot = (_nextSlot + 1) % _slots.Length;

        Fence fence = slot.Fence;
        ThrowIfDeviceFailure(
            api.WaitForFences(device, 1, in fence, true, ulong.MaxValue),
            "wait for headless WSI frame slot");

        uint imageIndex;
        Result acquire = _swapchainApi!.AcquireNextImage(
            device,
            _swapchain,
            ulong.MaxValue,
            slot.ImageAvailable,
            default,
            &imageIndex);
        ThrowIfHeadlessSurfaceUnavailable(acquire, "acquire");
        ThrowIfDeviceFailureAllowSuboptimal(acquire, "acquire headless swapchain image");

        VulkanRenderFrameTarget target = new(
            _images[imageIndex],
            _imageViews[imageIndex],
            slot.DepthImage,
            slot.DepthView,
            new Extent2D(_output.Width, _output.Height),
            _output.Layers,
            _imagePresented[imageIndex]
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined,
            ImageLayout.PresentSrcKhr,
            TargetGeneration,
            frameSlotIndex);
        VulkanFrameTargetLease lease = new(
            target,
            VulkanFixedOutputFormatResolver.ResolveColorFormat(
                _output.ColorFormat),
            VulkanFixedOutputFormatResolver.ResolveDepthFormat(
                _output.DepthFormat),
            SampleCountFlags.Count1Bit,
            imageIndex,
            acquire,
            slot.ImageAvailable,
            PipelineStageFlags.ColorAttachmentOutputBit,
            _renderFinishedSemaphores[imageIndex],
            slot.Fence,
            VulkanFrameTargetCompletionKind.WsiPresent,
            ImagesExternallyOwned: true,
            ViewIndex: 0,
            SupportsHiddenAreaMask: false);

        try
        {
            ThrowIfDeviceFailure(
                api.ResetFences(device, 1, in fence),
                "reset headless WSI frame fence");
            ThrowIfDeviceFailure(
                renderer.ResetVulkanCommandPoolTracked(
                    slot.CommandPool,
                    "HeadlessWsi.AcquireFrameTarget"),
                "reset headless WSI command pool");
            commandBuffer = slot.CommandBuffer;
            return lease;
        }
        catch
        {
            AbortFrameTarget(in lease, submissionAccepted: false);
            throw;
        }
    }

    public void BeginFrameRecording(
        in VulkanFrameTargetLease lease,
        CommandBuffer commandBuffer)
    {
        VulkanTargetOutputContext renderer = RequireTargetContext();
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("HeadlessWsi.BeginFrameRecording");
        CommandBufferBeginInfo begin = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        ThrowIfDeviceFailure(
            renderer.VulkanApi.BeginCommandBuffer(commandBuffer, in begin),
            "begin headless WSI command buffer");
    }

    public void EndFrameRecording(
        in VulkanFrameTargetLease lease,
        CommandBuffer commandBuffer)
    {
        VulkanTargetOutputContext renderer = RequireTargetContext();
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("HeadlessWsi.EndFrameRecording");
        ThrowIfDeviceFailure(
            renderer.EndCommandBufferTracked(commandBuffer),
            "end headless WSI command buffer");
    }

    public void NotifyFrameSubmitted(in VulkanFrameTargetLease lease)
    {
    }

    public void CompleteFrameTarget(in VulkanFrameTargetLease lease)
    {
        if (lease.CompletionKind != VulkanFrameTargetCompletionKind.WsiPresent)
        {
            throw new InvalidOperationException(
                $"Headless WSI target received unexpected completion kind '{lease.CompletionKind}'.");
        }

        VulkanTargetOutputContext renderer = RequireTargetContext();
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("HeadlessWsi.CompleteFrameTarget");
        Semaphore renderFinished = lease.SubmissionSignalSemaphore;
        uint imageIndex = lease.ImageIndex;
        SwapchainKHR swapchain = _swapchain;
        PresentInfoKHR present = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };
        Result presentResult = renderer.PresentToQueueTracked(
            _swapchainApi!,
            renderer.PresentQueue,
            ref present,
            "vkQueuePresentKHR.HeadlessWsi");
        ThrowIfHeadlessSurfaceUnavailable(presentResult, "present");
        ThrowIfDeviceFailureAllowSuboptimal(presentResult, "present headless WSI frame");
        _imagePresented[imageIndex] = true;
    }

    public void AbortFrameTarget(
        in VulkanFrameTargetLease lease,
        bool submissionAccepted)
    {
        if (submissionAccepted || _deviceLost || !lease.IsValid)
            return;

        VulkanTargetOutputContext renderer = RequireTargetContext();
        if (!renderer.TryAdmitVulkanDeviceOperation("HeadlessWsi.AbortFrameTarget", out _))
        {
            _deviceLost = true;
            return;
        }
        int slotIndex = ResolveSlotIndex(in lease);
        VulkanHeadlessWsiFrameSlot slot = _slots[slotIndex];
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        _ = renderer.ResetVulkanCommandPoolTracked(
            slot.CommandPool,
            "HeadlessWsi.AbortFrameTarget");

        PipelineStageFlags waitStage = lease.SubmissionWaitStage;
        Semaphore waitSemaphore = lease.SubmissionWaitSemaphore;
        Semaphore signalSemaphore = lease.SubmissionSignalSemaphore;
        SubmitInfo recoverySubmit = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = waitSemaphore.Handle != 0 ? 1u : 0u,
            PWaitSemaphores = waitSemaphore.Handle != 0
                ? &waitSemaphore
                : null,
            PWaitDstStageMask = waitSemaphore.Handle != 0
                ? &waitStage
                : null,
            SignalSemaphoreCount = signalSemaphore.Handle != 0 ? 1u : 0u,
            PSignalSemaphores = signalSemaphore.Handle != 0
                ? &signalSemaphore
                : null,
        };
        Result recoverySubmitResult = renderer.SubmitToQueueTracked(
            renderer.GraphicsQueue,
            ref recoverySubmit,
            lease.CompletionFence,
            caller: "HeadlessWsi.AbortFrameTarget");
        ThrowIfDeviceFailure(
            recoverySubmitResult,
            "consume rejected headless WSI acquisition");
        CompleteFrameTarget(in lease);
    }

    public byte[] ReadbackLastSubmittedColor(int maxByteCount, ImageLayout sourceLayout)
        => throw new NotSupportedException(
            "Headless WSI readback is not implicit. Use a render-graph readback resource before presenting the acquired image.");

    public string ComputeLastSubmittedColorHash(ImageLayout sourceLayout)
        => throw new NotSupportedException(
            "Headless WSI hashing requires an explicit render-graph readback before presentation.");

    private void CreateSwapchain(VulkanTargetOutputContext renderer)
    {
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("HeadlessWsi.CreateSwapchain");
        KhrSurface surfaceApi = renderer.RequireSurfaceApi();
        Vk api = renderer.VulkanApi;
        PhysicalDevice physicalDevice = renderer.PhysicalDevice;
        Device device = renderer.Device;
        SurfaceKHR surface = renderer.TargetSurface;

        ThrowIfDeviceFailure(
            surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                physicalDevice,
                surface,
                out SurfaceCapabilitiesKHR capabilities),
            "query headless surface capabilities");
        SurfaceFormatKHR format = ChooseSurfaceFormat(surfaceApi, physicalDevice, surface);
        PresentModeKHR presentMode = ChoosePresentMode(surfaceApi, physicalDevice, surface);
        Extent2D extent = ChooseExtent(capabilities);
        if (_output.Layers > capabilities.MaxImageArrayLayers)
        {
            throw new NotSupportedException(
                $"The Vulkan headless surface supports at most {capabilities.MaxImageArrayLayers} image layers, not {_output.Layers}.");
        }

        ImageUsageFlags requiredUsage =
            ImageUsageFlags.ColorAttachmentBit |
            ImageUsageFlags.TransferSrcBit |
            ImageUsageFlags.TransferDstBit;
        if ((capabilities.SupportedUsageFlags & requiredUsage) != requiredUsage)
        {
            throw new NotSupportedException(
                $"The Vulkan headless surface does not support required swapchain usage '{requiredUsage}'.");
        }

        uint imageCount = Math.Max(capabilities.MinImageCount, _output.FrameSlotCount);
        if (capabilities.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
        CompositeAlphaFlagsKHR compositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha);
        uint graphicsFamily = renderer.GraphicsQueueFamilyIndex;
        uint presentFamily = renderer.PresentQueueFamilyIndex;
        uint* queueFamilies = stackalloc uint[2] { graphicsFamily, presentFamily };
        bool concurrent = graphicsFamily != presentFamily;

        SwapchainCreateInfoKHR create = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = _output.Layers,
            ImageUsage = requiredUsage,
            ImageSharingMode = concurrent ? SharingMode.Concurrent : SharingMode.Exclusive,
            QueueFamilyIndexCount = concurrent ? 2u : 0u,
            PQueueFamilyIndices = concurrent ? queueFamilies : null,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = compositeAlpha,
            PresentMode = presentMode,
            Clipped = true,
        };
        ThrowIfDeviceFailure(
            _swapchainApi!.CreateSwapchain(device, in create, null, out _swapchain),
            "create headless WSI swapchain");

        uint actualCount = 0;
        ThrowIfDeviceFailure(
            _swapchainApi.GetSwapchainImages(device, _swapchain, ref actualCount, null),
            "query headless WSI swapchain image count");
        _images = new Image[actualCount];
        fixed (Image* imagesPtr = _images)
        {
            ThrowIfDeviceFailure(
                _swapchainApi.GetSwapchainImages(device, _swapchain, ref actualCount, imagesPtr),
                "query headless WSI swapchain images");
        }

        _imageViews = new ImageView[_images.Length];
        _imagePresented = new bool[_images.Length];
        for (int i = 0; i < _images.Length; i++)
            _imageViews[i] = CreateImageView(
                api,
                device,
                _images[i],
                format.Format,
                ImageAspectFlags.ColorBit,
                $"Swapchain.Color.HeadlessWsi[{i}]");
    }

    private SurfaceFormatKHR ChooseSurfaceFormat(
        KhrSurface surfaceApi,
        PhysicalDevice physicalDevice,
        SurfaceKHR surface)
    {
        uint count = 0;
        ThrowIfDeviceFailure(
            surfaceApi.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref count, null),
            "query headless surface format count");
        if (count == 0)
            throw new NotSupportedException("The Vulkan headless surface exposes no swapchain formats.");

        SurfaceFormatKHR[] formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* formatsPtr = formats)
        {
            ThrowIfDeviceFailure(
                surfaceApi.GetPhysicalDeviceSurfaceFormats(
                    physicalDevice,
                    surface,
                    ref count,
                    formatsPtr),
                "query headless surface formats");
        }

        Format requested = VulkanFixedOutputFormatResolver.ResolveColorFormat(_output.ColorFormat);
        for (int i = 0; i < formats.Length; i++)
            if (formats[i].Format == requested)
                return formats[i];
        throw new NotSupportedException(
            $"The Vulkan headless surface does not support requested swapchain format '{requested}'.");
    }

    private PresentModeKHR ChoosePresentMode(
        KhrSurface surfaceApi,
        PhysicalDevice physicalDevice,
        SurfaceKHR surface)
    {
        uint count = 0;
        ThrowIfDeviceFailure(
            surfaceApi.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref count, null),
            "query headless present mode count");
        PresentModeKHR[] modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* modesPtr = modes)
        {
            ThrowIfDeviceFailure(
                surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                    physicalDevice,
                    surface,
                    ref count,
                    modesPtr),
                "query headless present modes");
        }

        for (int i = 0; i < modes.Length; i++)
            if (modes[i] == PresentModeKHR.FifoKhr)
                return modes[i];
        throw new NotSupportedException(
            "The Vulkan headless surface does not expose the required FIFO present mode.");
    }

    private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            if (capabilities.CurrentExtent.Width != _output.Width ||
                capabilities.CurrentExtent.Height != _output.Height)
            {
                throw new NotSupportedException(
                    $"The Vulkan headless surface requires {capabilities.CurrentExtent.Width}x{capabilities.CurrentExtent.Height}, not {_output.Width}x{_output.Height}.");
            }
            return capabilities.CurrentExtent;
        }

        if (_output.Width < capabilities.MinImageExtent.Width ||
            _output.Width > capabilities.MaxImageExtent.Width ||
            _output.Height < capabilities.MinImageExtent.Height ||
            _output.Height > capabilities.MaxImageExtent.Height)
        {
            throw new NotSupportedException(
                $"The requested Vulkan headless extent {_output.Width}x{_output.Height} is outside the supported " +
                $"{capabilities.MinImageExtent.Width}x{capabilities.MinImageExtent.Height}-" +
                $"{capabilities.MaxImageExtent.Width}x{capabilities.MaxImageExtent.Height} range.");
        }
        return new(_output.Width, _output.Height);
    }

    private VulkanHeadlessWsiFrameSlot CreateFrameSlot(
        VulkanTargetOutputContext renderer,
        int slotIndex)
    {
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("HeadlessWsi.CreateFrameSlot");
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        CommandPool commandPool = default;
        Fence fence = default;
        Semaphore imageAvailable = default;
        Image depthImage = default;
        ImageView depthView = default;
        VulkanMemoryAllocation depthAllocation = default;
        try
        {
            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = renderer.GraphicsQueueFamilyIndex,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };
            ThrowIfDeviceFailure(
                renderer.CreateVulkanCommandPoolTracked(
                    ref poolInfo,
                    out commandPool,
                    "HeadlessWsi.CommandPool"),
                "create headless WSI command pool");
            CommandBufferAllocateInfo allocate = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Result allocateCommandBufferResult = renderer.AllocateVulkanCommandBufferTracked(
                ref allocate,
                out CommandBuffer commandBuffer,
                "HeadlessWsi.CommandBuffer");
            ThrowIfDeviceFailure(
                allocateCommandBufferResult,
                "allocate headless WSI command buffer");
            FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            ThrowIfDeviceFailure(api.CreateFence(device, in fenceInfo, null, out fence), "create headless WSI frame fence");
            SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
            ThrowIfDeviceFailure(api.CreateSemaphore(device, in semaphoreInfo, null, out imageAvailable), "create headless WSI acquire semaphore");

            Format depthFormat = VulkanFixedOutputFormatResolver.ResolveDepthFormat(_output.DepthFormat);
            depthImage = CreateDepthImage(renderer, depthFormat, slotIndex, out depthAllocation);
            depthView = CreateImageView(
                api,
                device,
                depthImage,
                depthFormat,
                VulkanFixedOutputFormatResolver.DepthAspect(depthFormat),
                $"HeadlessWsi.DepthView[{slotIndex}]");
            return new(
                commandPool,
                commandBuffer,
                fence,
                imageAvailable,
                depthImage,
                depthView,
                depthAllocation);
        }
        catch
        {
            VulkanHeadlessWsiFrameSlot partial = new(
                commandPool,
                default,
                fence,
                imageAvailable,
                depthImage,
                depthView,
                depthAllocation);
            DestroyFrameSlot(renderer, in partial);
            throw;
        }
    }

    private Semaphore[] CreateRenderFinishedSemaphores(
        VulkanTargetOutputContext renderer,
        int count)
    {
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("HeadlessWsi.CreateRenderFinishedSemaphores");
        Semaphore[] semaphores = new Semaphore[count];
        SemaphoreCreateInfo create = new() { SType = StructureType.SemaphoreCreateInfo };
        try
        {
            for (int i = 0; i < semaphores.Length; i++)
            {
                ThrowIfDeviceFailure(
                    renderer.VulkanApi.CreateSemaphore(
                        renderer.Device,
                        in create,
                        null,
                        out semaphores[i]),
                    "create image-scoped headless WSI present semaphore");
            }
            return semaphores;
        }
        catch
        {
            for (int i = 0; i < semaphores.Length; i++)
                if (semaphores[i].Handle != 0)
                    renderer.VulkanApi.DestroySemaphore(renderer.Device, semaphores[i], null);
            throw;
        }
    }

    private Image CreateDepthImage(
        VulkanTargetOutputContext renderer,
        Format format,
        int slotIndex,
        out VulkanMemoryAllocation allocation)
    {
        renderer.ThrowIfVulkanDeviceOperationNotAdmitted("HeadlessWsi.CreateDepthImage");
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        allocation = default;
        Image image = default;
        ImageCreateInfo create = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(_output.Width, _output.Height, 1),
            MipLevels = 1,
            ArrayLayers = _output.Layers,
            Format = format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransferSrcBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };
        string owner = $"HeadlessWsi.Generation{Math.Max(TargetGeneration, 1UL)}.Slot{slotIndex}.Depth";
        try
        {
            ThrowIfDeviceFailure(renderer.CreateVulkanImageTracked(ref create, out image, owner), "create headless WSI depth image");
            allocation = renderer.AllocateImageMemoryWithFallback(image, MemoryPropertyFlags.DeviceLocalBit);
            ThrowIfDeviceFailure(
                api.BindImageMemory(device, image, allocation.Memory, allocation.Offset),
                "bind headless WSI depth image memory");
            return image;
        }
        catch
        {
            if (image.Handle != 0)
                renderer.DestroyVulkanImageImmediateTracked(image, $"{owner}.CreateFailure");
            renderer.FreeMemoryAllocation(allocation);
            allocation = default;
            throw;
        }
    }

    private ImageView CreateImageView(
        Vk api,
        Device device,
        Image image,
        Format format,
        ImageAspectFlags aspect,
        string owner)
    {
        RequireTargetContext().ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateImageView.HeadlessWsi");
        ImageViewCreateInfo info = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = _output.Layers == 1 ? ImageViewType.Type2D : ImageViewType.Type2DArray,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, _output.Layers),
        };
        ThrowIfDeviceFailure(api.CreateImageView(device, in info, null, out ImageView view), "create headless WSI image view");
        RequireTargetContext().TrackLiveImageView(view, in info, owner);
        return view;
    }

    private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(
        CompositeAlphaFlagsKHR supported)
    {
        CompositeAlphaFlagsKHR[] preferences =
        [
            CompositeAlphaFlagsKHR.OpaqueBitKhr,
            CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
            CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
            CompositeAlphaFlagsKHR.InheritBitKhr,
        ];
        for (int i = 0; i < preferences.Length; i++)
            if ((supported & preferences[i]) != 0)
                return preferences[i];
        throw new NotSupportedException("The Vulkan headless surface exposes no supported composite-alpha mode.");
    }

    private static void DestroyFrameSlot(
        VulkanTargetOutputContext renderer,
        in VulkanHeadlessWsiFrameSlot slot)
    {
        Vk api = renderer.VulkanApi;
        Device device = renderer.Device;
        if (slot.DepthView.Handle != 0)
        {
            if (renderer.TryBeginDestroyImageView(slot.DepthView, "HeadlessWsiTarget.DepthView"))
                api.DestroyImageView(device, slot.DepthView, null);
        }
        if (slot.DepthImage.Handle != 0)
            renderer.DestroyVulkanImageImmediateTracked(slot.DepthImage, "HeadlessWsiTarget.Depth");
        renderer.FreeMemoryAllocation(slot.DepthAllocation);
        if (slot.ImageAvailable.Handle != 0)
            api.DestroySemaphore(device, slot.ImageAvailable, null);
        if (slot.Fence.Handle != 0)
            api.DestroyFence(device, slot.Fence, null);
        if (slot.CommandPool.Handle != 0)
            renderer.DestroyCommandPoolHostSynchronized(slot.CommandPool);
    }

    private VulkanTargetOutputContext RequireTargetContext()
        => _targetContext
            ?? throw new InvalidOperationException("The headless WSI target generation is not initialized.");

    private int ResolveSlotIndex(in VulkanFrameTargetLease lease)
    {
        int slotIndex = checked((int)lease.Target.FrameSlotIndex);
        if ((uint)slotIndex >= (uint)_slots.Length)
        {
            throw new InvalidOperationException(
                $"Headless WSI frame lease references invalid slot {slotIndex}.");
        }
        return slotIndex;
    }

    private void ThrowIfHeadlessSurfaceUnavailable(Result result, string operation)
    {
        if (result is not Result.ErrorOutOfDateKhr and not Result.ErrorSurfaceLostKhr)
            return;
        throw new InvalidOperationException(
            $"The Vulkan headless WSI surface became unavailable during {operation}: {result}. " +
            "Headless WSI does not silently fall back to presentationless execution.");
    }

    private void ThrowIfDeviceFailureAllowSuboptimal(Result result, string operation)
    {
        if (result is Result.Success or Result.SuboptimalKhr)
            return;
        ThrowIfDeviceFailure(result, operation);
    }

    private void ThrowIfDeviceFailure(Result result, string operation)
    {
        if (result == Result.Success)
            return;
        if (result == Result.ErrorDeviceLost)
        {
            _deviceLost = true;
            _targetContext?.MarkDeviceLost($"Headless WSI target {operation} returned ErrorDeviceLost", operation, result);
        }
        throw new InvalidOperationException($"Vulkan failed to {operation}: {result}.");
    }
}
