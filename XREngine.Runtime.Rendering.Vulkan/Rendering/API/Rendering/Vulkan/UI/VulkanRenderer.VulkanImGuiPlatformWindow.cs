using ImGuiNET;
using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using System.Numerics;
using System.Runtime.InteropServices;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Owns one detached ImGui native window and the Vulkan WSI resources that present it.
    /// </summary>
    private sealed class VulkanImGuiPlatformWindow : IDisposable
    {
        private const int FramesInFlight = 2;

        private readonly VulkanImGuiMultiViewportController _owner;
        private readonly VulkanRenderer _renderer;
        private readonly GCHandle _handle;
        private readonly VulkanImGuiDrawDataCache _drawData = new();
        private IInputContext? _input;
        private IMouse? _mouse;
        private readonly List<IKeyboard> _keyboards = [];
        private SurfaceKHR _surface;
        private SwapchainKHR _swapchain;
        private Format _format;
        private ColorSpaceKHR _colorSpace;
        private Extent2D _extent;
        private Image[] _images = [];
        private ImageView[] _imageViews = [];
        private bool[] _imagePresented = [];
        private CommandPool _commandPool;
        private CommandBuffer[] _commandBuffers = [];
        private Fence[] _frameFences = [];
        private bool[] _frameFenceSubmitted = [];
        private Semaphore[] _imageAvailableSemaphores = [];
        private Semaphore[] _renderFinishedSemaphores = [];
        private VulkanImGuiDrawBufferSet[] _drawBuffers = [];
        private Vector2D<int> _lastPosition;
        private Vector2D<int> _lastSize;
        private int _frameSlot;
        private bool _rendererReady;
        private bool _resizeRequested;
        private bool _disposeStarted;
        private bool _disposed;

        public VulkanImGuiPlatformWindow(
            VulkanImGuiMultiViewportController owner,
            VulkanRenderer renderer,
            ImGuiViewportPtr viewport)
        {
            _owner = owner;
            _renderer = renderer;
            ViewportId = viewport.ID;
            _handle = GCHandle.Alloc(this);

            WindowOptions options = WindowOptions.Default;
            options.API = renderer.XRWindow.Window.API;
            options.Size = ToWindowSize(viewport.Size);
            options.Position = ToWindowPosition(viewport.Pos);
            options.Title = "XREngine";
            options.WindowBorder = (viewport.Flags & ImGuiViewportFlags.NoDecoration) != 0
                ? WindowBorder.Hidden
                : WindowBorder.Resizable;
            options.TopMost = (viewport.Flags & ImGuiViewportFlags.TopMost) != 0;
            options.IsVisible = false;
            options.ShouldSwapAutomatically = false;

            Window = Silk.NET.Windowing.Window.Create(options);
            Window.Load += OnLoad;
            Window.FocusChanged += OnFocusChanged;
            Window.Closing += OnClosing;
            Window.Initialize();
            VulkanImGuiMultiViewportController.SetClientScreenPosition(Window, ToWindowPosition(viewport.Pos));
            _lastPosition = VulkanImGuiMultiViewportController.GetClientScreenPosition(Window);
            _lastSize = Window.Size;
        }

        public IWindow Window { get; }
        public uint ViewportId { get; }
        public bool Focused { get; private set; }
        public bool IsDisposed => _disposeStarted;
        public nint Handle => GCHandle.ToIntPtr(_handle);

        public void SetPosition(Vector2D<int> position)
        {
            VulkanImGuiMultiViewportController.SetClientScreenPosition(Window, position);
            _lastPosition = VulkanImGuiMultiViewportController.GetClientScreenPosition(Window);
        }

        public void SetSize(Vector2D<int> size)
        {
            Window.Size = size;
            _lastSize = Window.Size;
            RequestRendererResize();
        }

        public void ProcessEvents(ImGuiViewportPtr viewport)
        {
            Window.DoEvents();
            if (Window.IsClosing)
                viewport.PlatformRequestClose = true;

            Vector2D<int> position = VulkanImGuiMultiViewportController.GetClientScreenPosition(Window);
            if (position != _lastPosition)
            {
                _lastPosition = position;
                viewport.PlatformRequestMove = true;
            }

            Vector2D<int> size = Window.Size;
            if (size != _lastSize)
            {
                _lastSize = size;
                viewport.PlatformRequestResize = true;
                RequestRendererResize();
            }
        }

        public void CaptureDrawData(ImDrawDataPtr drawData)
            => _drawData.Store(drawData);

        public void CreateRendererResources()
        {
            if (_rendererReady || _disposed)
                return;

            _renderer.ThrowIfVulkanDeviceOperationNotAdmitted("ImGuiViewport.CreateRendererResources");

            if (Window.VkSurface is null)
                throw new NotSupportedException("The detached ImGui window does not expose Vulkan surface services.");

            _surface = Window.VkSurface
                .Create<AllocationCallbacks>(_renderer.instance.ToHandle(), null)
                .ToSurface();
            try
            {
                ValidatePresentSupport();
                _rendererReady = TryCreateSwapchainResources();
                _resizeRequested = !_rendererReady;
            }
            catch
            {
                DestroyRendererResources();
                throw;
            }
        }

        public void RequestRendererResize()
            => _resizeRequested = true;

        public void RenderPending()
        {
            if (_disposed || !_drawData.TryConsume(out VulkanImGuiFrameSnapshot? snapshot) || snapshot is null)
                return;

            try
            {
                if (!_rendererReady)
                    CreateRendererResources();

                if (_resizeRequested || SwapchainExtentChanged())
                    RecreateSwapchainResources();

                if (!_rendererReady || Window.WindowState == WindowState.Minimized)
                    return;

                RenderSnapshot(snapshot);
            }
            finally
            {
                _drawData.Recycle(snapshot);
            }
        }

        public bool BeginDispose()
        {
            if (_disposeStarted)
                return false;

            _disposeStarted = true;
            Window.Load -= OnLoad;
            Window.FocusChanged -= OnFocusChanged;
            Window.Closing -= OnClosing;
            DetachInputHandlers();

            try
            {
                Window.IsVisible = false;
            }
            catch
            {
            }
            return true;
        }

        public void ReleaseAfterRuntimeClose()
        {
            DestroyRendererResources();
            if (VulkanImGuiMultiViewportController.ShouldDisposeNativeWindow)
            {
                Dispose();
                return;
            }

            AbandonNativeWindowForShutdown();
        }

        public void Dispose()
        {
            BeginDispose();
            if (_disposed)
                return;

            _disposed = true;
            DestroyRendererResources();
            try
            {
                (Window as IDisposable)?.Dispose();
                _input = null;
            }
            catch
            {
                VulkanImGuiMultiViewportController.PreserveAbandonedWindow(Window, _input);
                _input = null;
            }

            if (_handle.IsAllocated)
                _handle.Free();
        }

        public void AbandonNativeWindowForShutdown()
        {
            BeginDispose();
            if (_disposed)
                return;

            _disposed = true;
            DestroyRendererResources();
            if (_handle.IsAllocated)
                _handle.Free();
            VulkanImGuiMultiViewportController.PreserveAbandonedWindow(Window, _input);
            _input = null;
        }

        public void DestroyRendererResources()
        {
            if (_surface.Handle == 0 && !_rendererReady)
                return;

            WaitForViewportQueuesIdle();
            DestroySwapchainResources();
            if (_surface.Handle != 0 && _renderer.khrSurface is not null)
                _renderer.khrSurface.DestroySurface(_renderer.instance, _surface, null);
            _surface = default;
            _rendererReady = false;
            _resizeRequested = false;
            _drawData.Clear();
        }

        private void ValidatePresentSupport()
        {
            uint presentFamily = _renderer.FamilyQueueIndices.PresentFamilyIndex
                ?? throw new InvalidOperationException("The Vulkan renderer has no presentation queue family.");
            Result result = _renderer.khrSurface!.GetPhysicalDeviceSurfaceSupport(
                _renderer._physicalDevice,
                presentFamily,
                _surface,
                out Bool32 supported);
            ThrowIfFailed(result, "query detached-window presentation support");
            if (!supported)
            {
                throw new NotSupportedException(
                    $"The renderer's presentation queue family {presentFamily} cannot present this detached ImGui window.");
            }
        }

        private bool TryCreateSwapchainResources()
        {
            if (!_renderer.TryAdmitVulkanDeviceOperation("ImGuiViewport.CreateSwapchainResources", out _))
                return false;

            Vector2D<int> framebufferSize = Window.FramebufferSize;
            if (framebufferSize.X <= 0 || framebufferSize.Y <= 0)
                return false;

            SurfaceCapabilitiesKHR capabilities;
            ThrowIfFailed(
                _renderer.khrSurface!.GetPhysicalDeviceSurfaceCapabilities(
                    _renderer._physicalDevice,
                    _surface,
                    out capabilities),
                "query detached-window surface capabilities");

            SurfaceFormatKHR surfaceFormat = ChooseSurfaceFormat();
            PresentModeKHR presentMode = ChoosePresentMode();
            Extent2D extent = ChooseExtent(capabilities, framebufferSize);
            if (extent.Width == 0 || extent.Height == 0)
                return false;

            if ((capabilities.SupportedUsageFlags & ImageUsageFlags.ColorAttachmentBit) == 0)
                throw new NotSupportedException("The detached ImGui surface does not support color-attachment swapchain images.");

            uint imageCount = Math.Max(capabilities.MinImageCount + 1, 2u);
            if (capabilities.MaxImageCount > 0)
                imageCount = Math.Min(imageCount, capabilities.MaxImageCount);

            uint graphicsFamily = _renderer.FamilyQueueIndices.GraphicsFamilyIndex!.Value;
            uint presentFamily = _renderer.FamilyQueueIndices.PresentFamilyIndex!.Value;
            uint* queueFamilies = stackalloc uint[2] { graphicsFamily, presentFamily };
            bool concurrent = graphicsFamily != presentFamily;

            SwapchainCreateInfoKHR createInfo = new()
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = concurrent ? SharingMode.Concurrent : SharingMode.Exclusive,
                QueueFamilyIndexCount = concurrent ? 2u : 0u,
                PQueueFamilyIndices = concurrent ? queueFamilies : null,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha),
                PresentMode = presentMode,
                Clipped = true,
            };

            ThrowIfFailed(
                _renderer.khrSwapChain!.CreateSwapchain(_renderer.device, in createInfo, null, out _swapchain),
                "create detached-window swapchain");

            uint actualImageCount = 0;
            ThrowIfFailed(
                _renderer.khrSwapChain.GetSwapchainImages(
                    _renderer.device,
                    _swapchain,
                    ref actualImageCount,
                    null),
                "query detached-window swapchain image count");
            _images = new Image[actualImageCount];
            fixed (Image* imagesPtr = _images)
            {
                ThrowIfFailed(
                    _renderer.khrSwapChain.GetSwapchainImages(
                        _renderer.device,
                        _swapchain,
                        ref actualImageCount,
                        imagesPtr),
                    "query detached-window swapchain images");
            }

            _format = surfaceFormat.Format;
            _colorSpace = surfaceFormat.ColorSpace;
            _extent = extent;
            _imagePresented = new bool[_images.Length];
            _imageViews = new ImageView[_images.Length];
            for (int i = 0; i < _images.Length; i++)
            {
                _imageViews[i] = CreateImageView(_images[i]);
                _renderer.ClearTrackedImageLayouts(_images[i]);
            }

            CreateCommandResources(graphicsFamily);
            CreateSynchronizationResources();
            // Command and upload buffers follow the frame fences, not swapchain images.
            // Acquire may return the same image for consecutive frame slots; indexing
            // these resources by image would then reset a still-submitted command buffer.
            _drawBuffers = new VulkanImGuiDrawBufferSet[FramesInFlight];
            _frameSlot = 0;
            _resizeRequested = false;
            return true;
        }

        private SurfaceFormatKHR ChooseSurfaceFormat()
        {
            uint count = 0;
            ThrowIfFailed(
                _renderer.khrSurface!.GetPhysicalDeviceSurfaceFormats(
                    _renderer._physicalDevice,
                    _surface,
                    ref count,
                    null),
                "query detached-window surface format count");
            if (count == 0)
                throw new NotSupportedException("The detached ImGui surface exposes no Vulkan formats.");

            SurfaceFormatKHR[] formats = new SurfaceFormatKHR[count];
            fixed (SurfaceFormatKHR* formatsPtr = formats)
            {
                ThrowIfFailed(
                    _renderer.khrSurface.GetPhysicalDeviceSurfaceFormats(
                        _renderer._physicalDevice,
                        _surface,
                        ref count,
                        formatsPtr),
                    "query detached-window surface formats");
            }

            Format requiredFormat = _renderer.swapChainImageFormat;
            ColorSpaceKHR requiredColorSpace = _renderer.swapChainImageColorSpace;
            if (formats.Length == 1 && formats[0].Format == Format.Undefined)
                return new SurfaceFormatKHR(requiredFormat, requiredColorSpace);

            foreach (SurfaceFormatKHR format in formats)
            {
                if (format.Format == requiredFormat && format.ColorSpace == requiredColorSpace)
                    return format;
            }

            throw new NotSupportedException(
                $"The detached ImGui surface does not support the primary swapchain format {requiredFormat}/{requiredColorSpace}; " +
                "a compatible format is required to reuse the ImGui graphics pipeline.");
        }

        private PresentModeKHR ChoosePresentMode()
        {
            uint count = 0;
            ThrowIfFailed(
                _renderer.khrSurface!.GetPhysicalDeviceSurfacePresentModes(
                    _renderer._physicalDevice,
                    _surface,
                    ref count,
                    null),
                "query detached-window present mode count");
            if (count == 0)
                throw new NotSupportedException("The detached ImGui surface exposes no Vulkan present modes.");

            PresentModeKHR[] modes = new PresentModeKHR[count];
            fixed (PresentModeKHR* modesPtr = modes)
            {
                ThrowIfFailed(
                    _renderer.khrSurface.GetPhysicalDeviceSurfacePresentModes(
                        _renderer._physicalDevice,
                        _surface,
                        ref count,
                        modesPtr),
                    "query detached-window present modes");
            }

            if (Array.IndexOf(modes, PresentModeKHR.MailboxKhr) >= 0)
                return PresentModeKHR.MailboxKhr;
            if (Array.IndexOf(modes, PresentModeKHR.ImmediateKhr) >= 0)
                return PresentModeKHR.ImmediateKhr;
            return PresentModeKHR.FifoKhr;
        }

        private static Extent2D ChooseExtent(
            SurfaceCapabilitiesKHR capabilities,
            Vector2D<int> framebufferSize)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
                return capabilities.CurrentExtent;

            uint width = (uint)Math.Max(framebufferSize.X, 1);
            uint height = (uint)Math.Max(framebufferSize.Y, 1);
            return new Extent2D(
                Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
        {
            CompositeAlphaFlagsKHR[] preferences =
            [
                CompositeAlphaFlagsKHR.OpaqueBitKhr,
                CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
                CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
                CompositeAlphaFlagsKHR.InheritBitKhr,
            ];
            foreach (CompositeAlphaFlagsKHR preference in preferences)
                if ((supported & preference) != 0)
                    return preference;
            throw new NotSupportedException("The detached ImGui surface exposes no supported composite-alpha mode.");
        }

        private ImageView CreateImageView(Image image)
        {
            _renderer.ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateImageView.ImGuiViewport");
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = _format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.ColorBit,
                    0,
                    1,
                    0,
                    1),
            };
            ThrowIfFailed(
                _renderer.Api!.CreateImageView(_renderer.device, in createInfo, null, out ImageView view),
                "create detached-window swapchain image view");
            _renderer.TrackLiveImageView(
                view,
                in createInfo,
                $"Swapchain.Color.ImGuiViewport[{ViewportId:X8}]");
            return view;
        }

        private void CreateCommandResources(uint graphicsFamily)
        {
            _renderer.ThrowIfVulkanDeviceOperationNotAdmitted("ImGuiViewport.CreateCommandResources");
            _commandPool = _renderer.CreateCommandPoolForFamily(graphicsFamily);
            _commandBuffers = new CommandBuffer[FramesInFlight];
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)_commandBuffers.Length,
            };
            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                Result result = _renderer.AllocateCommandBuffersHostSynchronized(ref allocateInfo, commandBuffersPtr);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocateCommandBuffersCall(
                    allocateInfo.CommandBufferCount,
                    result == Result.Success);
                ThrowIfFailed(result, "allocate detached-window command buffers");
            }
        }

        private void CreateSynchronizationResources()
        {
            _renderer.ThrowIfVulkanDeviceOperationNotAdmitted("ImGuiViewport.CreateSynchronizationResources");
            _frameFences = new Fence[FramesInFlight];
            _frameFenceSubmitted = new bool[FramesInFlight];
            _imageAvailableSemaphores = new Semaphore[FramesInFlight];
            _renderFinishedSemaphores = new Semaphore[_images.Length];
            FenceCreateInfo fenceInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };
            SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };

            for (int i = 0; i < FramesInFlight; i++)
            {
                ThrowIfFailed(
                    _renderer.Api!.CreateFence(_renderer.device, in fenceInfo, null, out _frameFences[i]),
                    "create detached-window frame fence");
                ThrowIfFailed(
                    _renderer.Api.CreateSemaphore(_renderer.device, in semaphoreInfo, null, out _imageAvailableSemaphores[i]),
                    "create detached-window acquire semaphore");
            }

            for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
            {
                ThrowIfFailed(
                    _renderer.Api!.CreateSemaphore(_renderer.device, in semaphoreInfo, null, out _renderFinishedSemaphores[i]),
                    "create detached-window render-finished semaphore");
            }
        }

        private bool SwapchainExtentChanged()
        {
            Vector2D<int> framebufferSize = Window.FramebufferSize;
            return framebufferSize.X > 0 && framebufferSize.Y > 0 &&
                ((uint)framebufferSize.X != _extent.Width || (uint)framebufferSize.Y != _extent.Height);
        }

        private void RecreateSwapchainResources()
        {
            if (!_renderer.TryAdmitVulkanDeviceOperation("ImGuiViewport.RecreateSwapchainResources", out _))
            {
                _rendererReady = false;
                return;
            }

            WaitForViewportQueuesIdle();
            DestroySwapchainResources();
            _rendererReady = TryCreateSwapchainResources();
            _resizeRequested = !_rendererReady;
        }

        private void RenderSnapshot(VulkanImGuiFrameSnapshot snapshot)
        {
            if (!_renderer.TryAdmitVulkanDeviceOperation("ImGuiViewport.RenderSnapshot", out _))
            {
                _resizeRequested = true;
                return;
            }

            int frameSlot = _frameSlot;
            Fence frameFence = _frameFences[frameSlot];
            if (_frameFenceSubmitted[frameSlot])
            {
                _renderer.ThrowIfVulkanDeviceOperationNotAdmitted("vkWaitForFences.ImGuiViewport");
                ThrowIfFailed(
                    _renderer.Api!.WaitForFences(_renderer.device, 1, in frameFence, true, ulong.MaxValue),
                    "wait for detached-window frame fence");
                _renderer.NotifyVulkanFenceCompleted(frameFence);
                _frameFenceSubmitted[frameSlot] = false;
            }

            uint imageIndex = 0;
            Result acquireResult = _renderer.khrSwapChain!.AcquireNextImage(
                _renderer.device,
                _swapchain,
                ulong.MaxValue,
                _imageAvailableSemaphores[frameSlot],
                default,
                &imageIndex);
            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                _resizeRequested = true;
                return;
            }
            if (acquireResult is Result.NotReady or Result.Timeout)
                return;
            if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
                ThrowIfFailed(acquireResult, "acquire detached-window swapchain image");
            if (acquireResult == Result.SuboptimalKhr)
                _resizeRequested = true;

            Result resetFenceResult = _renderer.Api!.ResetFences(_renderer.device, 1, in frameFence);
            if (resetFenceResult != Result.Success)
            {
                _resizeRequested = true;
                ThrowIfFailed(resetFenceResult, "reset detached-window frame fence");
            }

            CommandBuffer commandBuffer = _commandBuffers[frameSlot];
            try
            {
                RecordCommandBuffer(commandBuffer, imageIndex, frameSlot, snapshot);
            }
            catch
            {
                // An image has already been acquired. Recreate the swapchain on the
                // next attempt so that failed recording cannot strand that image.
                _resizeRequested = true;
                throw;
            }

            PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
            Semaphore imageAvailable = _imageAvailableSemaphores[frameSlot];
            Semaphore renderFinished = _renderFinishedSemaphores[imageIndex];
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imageAvailable,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFinished,
            };
            Result submitResult = _renderer.SubmitToQueueTracked(
                _renderer.graphicsQueue,
                ref submitInfo,
                frameFence,
                caller: "ImGuiViewport");
            if (submitResult != Result.Success)
            {
                _resizeRequested = true;
                ThrowIfFailed(submitResult, "submit detached-window ImGui draw");
            }
            _frameFenceSubmitted[frameSlot] = true;

            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFinished,
                SwapchainCount = 1,
                PSwapchains = null,
                PImageIndices = &imageIndex,
            };
            SwapchainKHR swapchain = _swapchain;
            presentInfo.PSwapchains = &swapchain;
            Result presentResult = _renderer.PresentImGuiViewport(ref presentInfo);
            if (presentResult is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
                _resizeRequested = true;
            else if (presentResult != Result.Success)
            {
                _resizeRequested = true;
                ThrowIfFailed(presentResult, "present detached-window ImGui swapchain image");
            }

            if (presentResult is Result.Success or Result.SuboptimalKhr)
                _imagePresented[imageIndex] = true;
            _frameSlot = (frameSlot + 1) % FramesInFlight;
        }

        private void RecordCommandBuffer(
            CommandBuffer commandBuffer,
            uint imageIndex,
            int frameSlot,
            VulkanImGuiFrameSnapshot snapshot)
        {
            _renderer.ThrowIfVulkanDeviceOperationNotAdmitted("ImGuiViewport.RecordCommandBuffer");
            _renderer.EnsureImGuiFontResources();
            _renderer.EnsureImGuiPipeline();

            ThrowIfFailed(
                _renderer.ResetVulkanCommandBufferTracked(commandBuffer),
                "reset detached-window command buffer");
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            _renderer.ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.ImGuiViewport");
            ThrowIfFailed(
                _renderer.Api!.BeginCommandBuffer(commandBuffer, in beginInfo),
                "begin detached-window command buffer");

            _renderer.ResetCommandBufferBindState(commandBuffer);
            _renderer.TransitionImGuiSnapshotTexturesForSampling(commandBuffer, snapshot);
            _renderer.TransitionImGuiViewportImage(
                commandBuffer,
                _images[imageIndex],
                _imagePresented[imageIndex] ? ImageLayout.PresentSrcKhr : ImageLayout.Undefined,
                ImageLayout.ColorAttachmentOptimal);

            ClearValue clearValue = new()
            {
                Color = new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f),
            };
            RenderingAttachmentInfo colorAttachment = new()
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _imageViews[imageIndex],
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = clearValue,
            };
            RenderingInfo renderingInfo = new()
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
            };

            _renderer.CmdBeginDynamicRendering(commandBuffer, &renderingInfo);
            if (HasRenderableImGuiSnapshot(snapshot))
            {
                _renderer.RenderImGuiViewportSnapshot(
                    commandBuffer,
                    (uint)frameSlot,
                    snapshot,
                    _extent,
                    ref _drawBuffers);
            }
            _renderer.CmdEndDynamicRendering(commandBuffer);

            _renderer.TransitionImGuiViewportImage(
                commandBuffer,
                _images[imageIndex],
                ImageLayout.ColorAttachmentOptimal,
                ImageLayout.PresentSrcKhr);
            ThrowIfFailed(
                _renderer.EndCommandBufferTracked(commandBuffer),
                "end detached-window command buffer");
        }

        private void WaitForViewportQueuesIdle()
        {
            if (!_renderer.IsLogicalDeviceReady || !_renderer.IsDeviceOperational)
                return;

            _ = _renderer.WaitForQueueIdleTracked(_renderer.graphicsQueue, "ImGuiViewportDestroy.Graphics");
            if (_renderer.presentQueue.Handle != _renderer.graphicsQueue.Handle)
                _ = _renderer.WaitForQueueIdleTracked(_renderer.presentQueue, "ImGuiViewportDestroy.Present");
        }

        private void DestroySwapchainResources()
        {
            if (_renderer.Api is null || !_renderer.IsLogicalDeviceReady)
                return;

            _renderer.DestroyImGuiDrawBuffers(ref _drawBuffers);

            foreach (Fence fence in _frameFences)
                if (fence.Handle != 0)
                    _renderer.Api.DestroyFence(_renderer.device, fence, null);
            foreach (Semaphore semaphore in _imageAvailableSemaphores)
                if (semaphore.Handle != 0)
                    _renderer.Api.DestroySemaphore(_renderer.device, semaphore, null);
            foreach (Semaphore semaphore in _renderFinishedSemaphores)
                if (semaphore.Handle != 0)
                    _renderer.Api.DestroySemaphore(_renderer.device, semaphore, null);

            if (_commandPool.Handle != 0)
            {
                foreach (CommandBuffer commandBuffer in _commandBuffers)
                {
                    try
                    {
                        // Match the renderer's other command-pool teardown paths. Native
                        // drivers may recycle a command-buffer handle immediately after
                        // this pool is destroyed, so retaining a Destroyed tombstone would
                        // reject the new allocation on its first reset.
                        _renderer.RemoveCommandBufferBindState(commandBuffer);
                    }
                    catch (Exception ex)
                    {
                        Debug.VulkanWarning(
                            "[Vulkan.ImGuiMultiViewport] Failed to retire command-buffer tracking for viewport 0x{0:X8}: {1}",
                            ViewportId,
                            ex.Message);
                    }
                }
                _renderer.DestroyCommandPoolHostSynchronized(_commandPool);
            }

            foreach (ImageView view in _imageViews)
                if (view.Handle != 0)
                {
                    if (_renderer.TryBeginDestroyImageView(view, "ImGuiViewport.DestroySwapchainResources"))
                        _renderer.Api.DestroyImageView(_renderer.device, view, null);
                }
            foreach (Image image in _images)
                _renderer.ClearTrackedImageLayouts(image);
            if (_swapchain.Handle != 0 && _renderer.khrSwapChain is not null)
                _renderer.khrSwapChain.DestroySwapchain(_renderer.device, _swapchain, null);

            _swapchain = default;
            _format = default;
            _colorSpace = default;
            _extent = default;
            _images = [];
            _imageViews = [];
            _imagePresented = [];
            _commandPool = default;
            _commandBuffers = [];
            _frameFences = [];
            _frameFenceSubmitted = [];
            _imageAvailableSemaphores = [];
            _renderFinishedSemaphores = [];
            _drawBuffers = [];
            _rendererReady = false;
        }

        private void OnLoad()
        {
            try
            {
                _input = Window.CreateInput();
                AttachInputHandlers();
            }
            catch (Exception ex)
            {
                Debug.RenderingWarning(
                    "[Vulkan.ImGuiMultiViewport] Failed to initialize input for viewport 0x{0:X8}: {1}",
                    ViewportId,
                    ex.Message);
            }
        }

        private void AttachInputHandlers()
        {
            if (_input is null)
                return;

            if (_input.Mice.Count > 0)
            {
                _mouse = _input.Mice[0];
                _mouse.MouseMove += OnMouseMove;
                _mouse.MouseDown += OnMouseDown;
                _mouse.MouseUp += OnMouseUp;
                _mouse.Scroll += OnMouseScroll;
            }

            foreach (IKeyboard keyboard in _input.Keyboards)
            {
                keyboard.KeyDown += OnKeyDown;
                keyboard.KeyUp += OnKeyUp;
                keyboard.KeyChar += OnKeyChar;
                _keyboards.Add(keyboard);
            }
        }

        private void DetachInputHandlers()
        {
            if (_mouse is not null)
            {
                _mouse.MouseMove -= OnMouseMove;
                _mouse.MouseDown -= OnMouseDown;
                _mouse.MouseUp -= OnMouseUp;
                _mouse.Scroll -= OnMouseScroll;
                _mouse = null;
            }

            foreach (IKeyboard keyboard in _keyboards)
            {
                keyboard.KeyDown -= OnKeyDown;
                keyboard.KeyUp -= OnKeyUp;
                keyboard.KeyChar -= OnKeyChar;
            }
            _keyboards.Clear();
        }

        private void OnFocusChanged(bool focused)
            => Focused = focused;

        private void OnClosing()
        {
            if (_disposeStarted)
                return;

            _owner.RequestClose(ViewportId);
            try
            {
                Window.IsClosing = false;
            }
            catch
            {
            }
        }

        private void OnMouseMove(IMouse mouse, Vector2 position)
            => _owner.PushMousePosition(ViewportId, Window, position);
        private void OnMouseDown(IMouse mouse, MouseButton button)
            => _owner.PushMouseButton(ViewportId, button, true);
        private void OnMouseUp(IMouse mouse, MouseButton button)
            => _owner.PushMouseButton(ViewportId, button, false);
        private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
            => _owner.PushMouseWheel(ViewportId, wheel);
        private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
            => _owner.PushKey(keyboard, key, true);
        private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
            => _owner.PushKey(keyboard, key, false);
        private void OnKeyChar(IKeyboard keyboard, char value)
            => _owner.PushChar(value);

        private static Vector2D<int> ToWindowSize(Vector2 size)
            => new(Math.Max(1, (int)MathF.Round(size.X)), Math.Max(1, (int)MathF.Round(size.Y)));

        private static Vector2D<int> ToWindowPosition(Vector2 position)
            => new((int)MathF.Round(position.X), (int)MathF.Round(position.Y));

        private void ThrowIfFailed(Result result, string operation)
        {
            if (result == Result.Success)
                return;

            if (result == Result.ErrorDeviceLost)
                _renderer.MarkDeviceLost($"Detached ImGui viewport failed to {operation}", operation, result);
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
        }
    }
}
