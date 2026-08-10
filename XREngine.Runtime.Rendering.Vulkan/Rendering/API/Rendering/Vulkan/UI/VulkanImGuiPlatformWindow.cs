using ImGuiNET;
using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using System.Numerics;
using System.Runtime.InteropServices;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns one detached ImGui native window and the Vulkan WSI resources that present it.
/// </summary>
internal sealed unsafe class VulkanImGuiPlatformWindow : VulkanImGuiPlatformWindowOutputLifetime, IDisposable
    {
        private readonly VulkanImGuiMultiViewportController _owner;
        private readonly VulkanImGuiServices _services;
        private readonly GCHandle _handle;
        private readonly VulkanImGuiDrawDataCache _drawData = new();
        private IInputContext? _input;
        private IMouse? _mouse;
        private readonly List<IKeyboard> _keyboards = [];
        private Vector2D<int> _lastPosition;
        private Vector2D<int> _lastSize;
        private bool _disposeStarted;
        private bool _disposed;

        public VulkanImGuiPlatformWindow(
            VulkanImGuiMultiViewportController owner,
            VulkanImGuiServices services,
            ImGuiViewportPtr viewport)
        {
            _owner = owner;
            _services = services;
            ViewportId = viewport.ID;
            _handle = GCHandle.Alloc(this);

            WindowOptions options = WindowOptions.Default;
            options.API = services.MainWindow.API;
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
            services.Output.ImGuiPlatformWindows.Register(this);
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

            _services.Target.ThrowIfVulkanDeviceOperationNotAdmitted("ImGuiViewport.CreateRendererResources");

            _surface = _services.Output.ImGuiPlatformWindows.CreateSurface(
                _services.Device,
                Window);
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
            _services.Output.ImGuiPlatformWindows.Unregister(this);
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
            _services.Output.ImGuiPlatformWindows.Unregister(this);
        }

        public void DestroyRendererResources()
        {
            if (_surface.Handle == 0 && !_rendererReady)
                return;

            WaitForViewportQueuesIdle();
            DestroySwapchainResources();
            _services.Output.ImGuiPlatformWindows.DestroySurface(
                _services.Device,
                _services.Output.SurfaceApi,
                ref _surface);
            _rendererReady = false;
            _resizeRequested = false;
            _drawData.Clear();
        }

        private void ValidatePresentSupport()
        {
            uint presentFamily = _services.Device.QueueFamilies.PresentFamilyIndex
                ?? throw new InvalidOperationException("The Vulkan renderer has no presentation queue family.");
            Result result = _services.Output.SurfaceApi!.GetPhysicalDeviceSurfaceSupport(
                _services.Device.PhysicalDevice,
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
            if (!_services.Target.TryAdmitVulkanDeviceOperation("ImGuiViewport.CreateSwapchainResources", out _))
                return false;

            KhrSurface? surfaceApi = _services.Output.SurfaceApi;
            KhrSwapchain? swapchainApi = _services.Output.Desktop.SwapchainExtension;
            if (surfaceApi is null || swapchainApi is null)
                throw new InvalidOperationException("Detached ImGui viewport output requires initialized Vulkan surface and swapchain extensions.");

            if (!_services.Output.ImGuiPlatformWindows.TryCreateSwapchainGeneration(
                    _services.Device,
                    _services.Commands,
                    _services.Target,
                    surfaceApi,
                    swapchainApi,
                    _surface,
                    Window.FramebufferSize,
                    _services.Output.Desktop.ImageFormat,
                    _services.Output.Desktop.ImageColorSpace,
                    ViewportId,
                    out VulkanImGuiPlatformSwapchainGeneration generation))
            {
                return false;
            }

            _swapchain = generation.Swapchain;
            _format = generation.Format;
            _colorSpace = generation.ColorSpace;
            _extent = generation.Extent;
            _images = generation.Images;
            _imageViews = generation.ImageViews;
            _imagePresented = new bool[_images.Length];

            VulkanImGuiPlatformWindowCommandResources commandResources =
                _services.Commands.CreateImGuiPlatformWindowResources(
                    _services.Device,
                    _services.Target,
                    _services.Device.QueueFamilies.GraphicsFamilyIndex!.Value,
                    FramesInFlight,
                    _images.Length,
                    ViewportId);
            _commandPool = commandResources.CommandPool;
            _commandBuffers = commandResources.CommandBuffers;
            _frameFences = commandResources.Fences;
            _frameFenceSubmitted = commandResources.FrameFenceSubmitted;
            _imageAvailableSemaphores = commandResources.ImageAvailableSemaphores;
            _renderFinishedSemaphores = commandResources.RenderFinishedSemaphores;
            _frameSlot = 0;
            _resizeRequested = false;
            return true;
        }

        private PresentModeKHR ChoosePresentMode()
        {
            uint count = 0;
            ThrowIfFailed(
                _services.Output.SurfaceApi!.GetPhysicalDeviceSurfacePresentModes(
                    _services.Device.PhysicalDevice,
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
                    _services.Output.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(
                        _services.Device.PhysicalDevice,
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

        private bool SwapchainExtentChanged()
        {
            Vector2D<int> framebufferSize = Window.FramebufferSize;
            return framebufferSize.X > 0 && framebufferSize.Y > 0 &&
                ((uint)framebufferSize.X != _extent.Width || (uint)framebufferSize.Y != _extent.Height);
        }

        private void RecreateSwapchainResources()
        {
            if (!_services.Target.TryAdmitVulkanDeviceOperation("ImGuiViewport.RecreateSwapchainResources", out _))
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
            if (!_services.Target.TryAdmitVulkanDeviceOperation("ImGuiViewport.RenderSnapshot", out _))
            {
                _resizeRequested = true;
                return;
            }

            int frameSlot = _frameSlot;
            Fence frameFence = _frameFences[frameSlot];
            if (_frameFenceSubmitted[frameSlot])
            {
                _services.Target.ThrowIfVulkanDeviceOperationNotAdmitted("vkWaitForFences.ImGuiViewport");
                ThrowIfFailed(
                    _services.Api.WaitForFences(_services.Device.Device, 1, in frameFence, true, ulong.MaxValue),
                    "wait for detached-window frame fence");
                _services.Target.NotifyVulkanFenceCompleted(frameFence);
                _frameFenceSubmitted[frameSlot] = false;
            }

            uint imageIndex = 0;
            Result acquireResult = _services.Output.Desktop.SwapchainExtension!.AcquireNextImage(
                _services.Device.Device,
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

            Result resetFenceResult = _services.Api.ResetFences(_services.Device.Device, 1, in frameFence);
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
            Result submitResult = _services.SubmitToGraphicsQueue(ref submitInfo, frameFence, "ImGuiViewport");
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
            Result presentResult = _services.PresentViewport(ref presentInfo);
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
            VulkanOutputRuntime output = _services.Output;
            VulkanResourceRuntime resources = _services.Resources;
            VulkanCommandRuntime commands = _services.Commands;
            VulkanDeviceContext device = _services.Device;
            output.GetImGuiFontAtlasResources(resources, commands, device).EnsureCreated();
            output.GetImGuiOutputPipelineService(resources, device).EnsureCreated();

            VulkanTrackedCommandEncoder encoder = new(
                device.Api,
                device,
                commands,
                resources,
                _services.Telemetry);
            VulkanDynamicUiOverlayTarget target = new(
                _images[imageIndex],
                _imageViews[imageIndex],
                _extent,
                HasStreamlineUi: false,
                default,
                default,
                ImageLayout.Undefined);
            VulkanImGuiOverlayRecordingInput input = new(
                (uint)frameSlot,
                commandBuffer,
                default,
                _imagePresented[imageIndex] ? ImageLayout.PresentSrcKhr : ImageLayout.Undefined,
                device.InstanceApiVersion < Vk.Version13,
                target,
                output._imguiResources,
                output._imguiTextureRegistry.DescriptorSets,
                ClearSwapchain: true,
                snapshot);
            if (!new VulkanImGuiPlatformViewportRecorder().TryRecord(
                    encoder,
                    _services.Telemetry,
                    output.GetImGuiDrawBufferResources(resources),
                    in input,
                    out _))
            {
                throw new InvalidOperationException("Failed to record detached ImGui viewport command buffer.");
            }
        }

        private void WaitForViewportQueuesIdle()
        {
            if (!_services.Device.IsReady || !_services.Device.IsOperational)
                return;

            _ = _services.WaitForQueueIdle(_services.Device.GraphicsQueue, "ImGuiViewportDestroy.Graphics");
            if (_services.Device.PresentQueue.Handle != _services.Device.GraphicsQueue.Handle)
                _ = _services.WaitForQueueIdle(_services.Device.PresentQueue, "ImGuiViewportDestroy.Present");
        }

        private void DestroySwapchainResources()
        {
            if (!_services.Device.IsReady)
                return;

            _services.Commands.DestroyImGuiPlatformWindowResources(
                _services.Device,
                _services.Target,
                new VulkanImGuiPlatformWindowCommandResources(
                    _commandPool,
                    _commandBuffers,
                    _frameFences,
                    _frameFenceSubmitted,
                    _imageAvailableSemaphores,
                    _renderFinishedSemaphores),
                ViewportId);

            KhrSwapchain? swapchainApi = _services.Output.Desktop.SwapchainExtension;
            if (swapchainApi is not null)
                _services.Output.ImGuiPlatformWindows.DestroySwapchainGeneration(
                    _services.Device,
                    _services.Commands,
                    _services.Target,
                    swapchainApi,
                    _swapchain,
                    _images,
                    _imageViews,
                    ViewportId);

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
                _services.Target.MarkDeviceLost($"Detached ImGui viewport failed to {operation}", operation, result);
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
        }
    }
