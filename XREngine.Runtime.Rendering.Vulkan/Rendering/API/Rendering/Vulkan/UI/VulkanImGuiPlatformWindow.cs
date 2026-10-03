using ImGuiNET;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.UI;
using XREngine.Data.Vectors;
using XREngine.Input.Devices;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns one detached ImGui native window and the Vulkan WSI resources that present it.
/// </summary>
internal sealed unsafe class VulkanImGuiPlatformWindow : VulkanImGuiPlatformWindowOutputLifetime, IDisposable
    {
        private readonly VulkanImGuiMultiViewportController _owner;
        private readonly IVulkanImGuiOutputHost _outputHost;
        private readonly GCHandle _handle;
        private readonly VulkanImGuiDrawDataCache _drawData = new();
        private readonly VulkanImGuiDrawBufferResources _drawBuffers;
        private readonly List<WindowInputEvent> _uiEvents = new(64);
        private IVector2 _lastPosition;
        private IVector2 _lastSize;
        private bool _disposeStarted;
        private bool _disposed;

        public VulkanImGuiPlatformWindow(
            VulkanImGuiMultiViewportController owner,
            IVulkanImGuiOutputHost outputHost,
            ImGuiViewportPtr viewport)
        {
            _owner = owner;
            _outputHost = outputHost;
            ViewportId = viewport.ID;
            ViewportFlags = viewport.Flags;
            _handle = GCHandle.Alloc(this);
            _drawBuffers = outputHost.CreatePlatformDrawBufferResources();

            IVector2 size = ToWindowSize(viewport.Size);
            IVector2 position = ToWindowPosition(viewport.Pos);
            WindowStartupValues startup = WindowStartupValues.Default with
            {
                Title = "XREngine",
                X = position.X,
                Y = position.Y,
                Width = size.X,
                Height = size.Y,
                UseNativeTitleBar = (viewport.Flags & ImGuiViewportFlags.NoDecoration) == 0,
            };
            RuntimeWindowCreateOptions request = new(
                startup, RuntimeGraphicsApiKind.Vulkan,
                EInteractiveWindowResizeStrategy.Default, RuntimeWindowPurpose.EditorViewport,
                position, size, false, false,
                (viewport.Flags & ImGuiViewportFlags.TopMost) != 0,
                false, false, 24, 24, 8, 4, 6, false, false, false);
            IRuntimeWindowBackend? created = null;
            try
            {
                created = RuntimeWindowBackendRegistry.RequireFactory().Create(in request);
                created.Initialize(new ViewportWindowEventSink());
                ImGuiPlatformWindowBehavior.ConfigureNativeWindow(created.OperatingSystemWindowHandle, (uint)viewport.Flags);
                created.RequestClientScreenPosition(position);
                Window = created;
                _lastPosition = created.ClientScreenPosition;
                WindowSurfaceSnapshot surface = created.Surface;
                _lastSize = new IVector2(surface.ClientWidth, surface.ClientHeight);
            }
            catch
            {
                try
                {
                    if (created is not null)
                    {
                        ImGuiPlatformWindowBehavior.ReleaseNativeWindow(created.OperatingSystemWindowHandle);
                        created.Dispose();
                    }
                }
                finally
                {
                    _handle.Free();
                }
                throw;
            }
            outputHost.RegisterPlatformWindow(this);
        }

        public IRuntimeWindowBackend Window { get; }
        public uint ViewportId { get; }
        public ImGuiViewportFlags ViewportFlags { get; private set; }
        public bool AcceptsInputs => !ImGuiPlatformWindowBehavior.IsInputTransparent((uint)ViewportFlags);
        public bool Focused => Window.Events.IsFocused;
        public bool IsDisposed => _disposeStarted;
        public bool RendererReady => _rendererReady;
        public nint Handle => GCHandle.ToIntPtr(_handle);

        public void SetPosition(IVector2 position)
        {
            Window.RequestClientScreenPosition(position);
            _lastPosition = Window.ClientScreenPosition;
        }

        public void SetSize(IVector2 size)
        {
            Window.RequestSize(size);
            WindowSurfaceSnapshot surface = Window.Surface;
            _lastSize = new IVector2(surface.ClientWidth, surface.ClientHeight);
            RequestRendererResize();
        }

        public void ProcessEvents(ImGuiViewportPtr viewport)
        {
            UpdateViewportFlags(viewport.Flags);
            if (Window.IsClosing)
                viewport.PlatformRequestClose = true;

            IVector2 position = Window.ClientScreenPosition;
            if (position != _lastPosition)
            {
                _lastPosition = position;
                viewport.PlatformRequestMove = true;
            }

            WindowSurfaceSnapshot surface = Window.Surface;
            IVector2 size = new(surface.ClientWidth, surface.ClientHeight);
            if (size != _lastSize)
            {
                _lastSize = size;
                viewport.PlatformRequestResize = true;
                RequestRendererResize();
            }
        }

        public void UpdateViewportFlags(ImGuiViewportFlags flags)
        {
            if (ViewportFlags == flags)
                return;

            ViewportFlags = flags;
            ImGuiPlatformWindowBehavior.ConfigureNativeWindow(Window.OperatingSystemWindowHandle, (uint)flags);
        }

        public void CaptureDrawData(ImDrawDataPtr drawData)
            => _drawData.Store(drawData);

        public void CreateRendererResources()
        {
            if (_rendererReady || _disposed)
                return;

            _outputHost.ThrowIfDeviceOperationNotAdmitted("ImGuiViewport.CreateRendererResources");

            _surface = _outputHost.CreatePlatformSurface(Window);
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

                if ((_resizeRequested || SwapchainExtentChanged()) &&
                    !RecreateSwapchainResources())
                    return;

                if (!_rendererReady || Window.Surface.IsMinimized)
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
            ImGuiPlatformWindowBehavior.ReleaseNativeWindow(Window.OperatingSystemWindowHandle);

            try
            {
                Window.RequestVisibility(false);
            }
            catch
            {
            }
            return true;
        }

        public bool TryReleaseAfterRuntimeClose()
        {
            if (!TryDestroyRendererResources())
                return false;
            if (VulkanImGuiMultiViewportController.ShouldDisposeNativeWindow)
            {
                Dispose();
                return true;
            }

            AbandonNativeWindowForShutdown();
            return true;
        }

        public void Dispose()
        {
            BeginDispose();
            if (_disposed)
                return;
            if (!TryDestroyRendererResources())
                return;

            _disposed = true;
            _drawBuffers.RetireAll();
            try
            {
                Window.Dispose();
            }
            catch
            {
                Window.RetainAbandonedResources();
            }

            if (_handle.IsAllocated)
                _handle.Free();
            _outputHost.UnregisterPlatformWindow(this);
        }

        public void AbandonNativeWindowForShutdown()
        {
            BeginDispose();
            if (_disposed)
                return;
            if (!TryDestroyRendererResources())
                return;

            _disposed = true;
            _drawBuffers.RetireAll();
            if (_handle.IsAllocated)
                _handle.Free();
            Window.RetainAbandonedResources();
            _outputHost.UnregisterPlatformWindow(this);
        }

        internal override void WaitForPresentationReleaseAtShutdown(bool deviceLost)
        {
            if (deviceLost)
            {
                DestroyRendererResources(deviceLost: true);
                return;
            }

            for (int index = 0; index < _acquireFences.Length; index++)
            {
                if (!_acquireFenceSubmitted[index])
                    continue;
                Result result = _outputHost.WaitForPlatformFenceAtShutdown(_acquireFences[index]);
                ThrowIfFailed(result, "wait for detached-window acquire fence at shutdown");
                _acquireFenceSubmitted[index] = false;
            }
            _presentCompletion?.WaitForShutdown();
        }

        public void DestroyRendererResources(bool deviceLost = false)
        {
            if (_surface.Handle == 0 && !_rendererReady)
                return;

            if (deviceLost)
                DestroySwapchainResources(deviceLost: true);
            else if (!TryDestroyRendererResources())
                return;
            if (deviceLost)
                ReleasePlatformSurface();
        }

        private bool TryDestroyRendererResources()
        {
            if (!TryDestroySwapchainResources())
                return false;

            ReleasePlatformSurface();
            return true;
        }

        private bool TryDestroySwapchainResources()
        {
            if (!TryRetireSwapchainResources())
                return false;
            DestroySwapchainResources();
            _rendererReady = false;
            return true;
        }

        private void ReleasePlatformSurface()
        {
            _outputHost.DestroyPlatformSurface(ref _surface);
            _rendererReady = false;
            _resizeRequested = false;
            _drawData.Clear();
        }

        private void ValidatePresentSupport()
        {
            _outputHost.ValidatePlatformPresentSupport(_surface);
        }

        private bool TryCreateSwapchainResources(SwapchainKHR oldSwapchain = default)
        {
            if (!_outputHost.TryAdmitDeviceOperation("ImGuiViewport.CreateSwapchainResources"))
                return false;

            if (!_outputHost.TryCreatePlatformSwapchain(
                    _surface,
                    new Vector2D<int>(Window.Surface.FramebufferWidth, Window.Surface.FramebufferHeight),
                    ViewportId,
                    oldSwapchain,
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
            _presentCompletion = _outputHost.CreatePlatformPresentCompletion(_images.Length);

            VulkanImGuiPlatformWindowCommandResources commandResources =
                _outputHost.CreatePlatformCommandResources(
                    FramesInFlight,
                    _images.Length,
                    ViewportId);
            _commandPool = commandResources.CommandPool;
            _commandBuffers = commandResources.CommandBuffers;
            _frameFences = commandResources.Fences;
            _frameFenceSubmitted = commandResources.FrameFenceSubmitted;
            _acquireFences = commandResources.AcquireFences;
            _acquireFenceSubmitted = commandResources.AcquireFenceSubmitted;
            _imageAvailableSemaphores = commandResources.ImageAvailableSemaphores;
            _renderFinishedSemaphores = commandResources.RenderFinishedSemaphores;
            _frameSlot = 0;
            _resizeRequested = false;
            return true;
        }

        private PresentModeKHR ChoosePresentMode()
        {
            PresentModeKHR[] modes = _outputHost.GetPlatformPresentModes(_surface);
            if (modes.Length == 0)
                throw new NotSupportedException("The detached ImGui surface exposes no Vulkan present modes.");

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
            WindowSurfaceSnapshot surface = Window.Surface;
            Vector2D<int> framebufferSize = new(surface.FramebufferWidth, surface.FramebufferHeight);
            return framebufferSize.X > 0 && framebufferSize.Y > 0 &&
                ((uint)framebufferSize.X != _extent.Width || (uint)framebufferSize.Y != _extent.Height);
        }

        private bool RecreateSwapchainResources()
        {
            if (!_outputHost.TryAdmitDeviceOperation("ImGuiViewport.RecreateSwapchainResources"))
            {
                _rendererReady = false;
                return false;
            }

            if (!TryDestroySwapchainResources())
                return false;
            _rendererReady = TryCreateSwapchainResources();
            _resizeRequested = !_rendererReady;
            return _rendererReady;
        }

        private void RenderSnapshot(VulkanImGuiFrameSnapshot snapshot)
        {
            if (!_outputHost.TryAdmitDeviceOperation("ImGuiViewport.RenderSnapshot"))
            {
                _resizeRequested = true;
                return;
            }

            int frameSlot = _frameSlot;
            Fence frameFence = _frameFences[frameSlot];
            if (_frameFenceSubmitted[frameSlot])
            {
                Result waitResult = _outputHost.WaitForPlatformFence(frameFence);
                if (waitResult is Result.NotReady or Result.Timeout)
                    return;
                ThrowIfFailed(waitResult, "wait for detached-window frame fence");
                _frameFenceSubmitted[frameSlot] = false;
            }

            uint imageIndex = 0;
            if (_acquireFenceSubmitted[frameSlot])
            {
                Result acquireFenceResult = _outputHost.WaitForPlatformFence(_acquireFences[frameSlot]);
                if (acquireFenceResult is Result.NotReady or Result.Timeout)
                    return;
                ThrowIfFailed(acquireFenceResult, "poll detached-window acquire fence");
                _acquireFenceSubmitted[frameSlot] = false;
            }
            VulkanWsiPresentCompletion? presentCompletion = _presentCompletion;
            if (presentCompletion is null || !presentCompletion.TryReserve(out VulkanWsiPresentReservation reservation))
                return;
            Result resetAcquireFenceResult = _outputHost.ResetPlatformFence(_acquireFences[frameSlot]);
            if (resetAcquireFenceResult != Result.Success)
            {
                presentCompletion.Cancel(in reservation);
                _resizeRequested = true;
                ThrowIfFailed(resetAcquireFenceResult, "reset detached-window acquire fence");
            }
            Result acquireResult = _outputHost.AcquirePlatformImage(
                _swapchain, _imageAvailableSemaphores[frameSlot], _acquireFences[frameSlot], out imageIndex);
            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                presentCompletion.Cancel(in reservation);
                _resizeRequested = true;
                return;
            }
            if (acquireResult is Result.NotReady or Result.Timeout)
            {
                presentCompletion.Cancel(in reservation);
                return;
            }
            if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
            {
                presentCompletion.Cancel(in reservation);
                ThrowIfFailed(acquireResult, "acquire detached-window swapchain image");
            }
            _acquireFenceSubmitted[frameSlot] = true;
            if (acquireResult == Result.SuboptimalKhr)
                _resizeRequested = true;

            Result resetFenceResult = _outputHost.ResetPlatformFence(frameFence);
            if (resetFenceResult != Result.Success)
            {
                _resizeRequested = true;
                presentCompletion.Cancel(in reservation);
                ThrowIfFailed(resetFenceResult, "reset detached-window frame fence");
            }

            CommandBuffer commandBuffer = _commandBuffers[frameSlot];
            try
            {
                if (!RecordCommandBuffer(commandBuffer, imageIndex, frameSlot, snapshot))
                {
                    // The dependency generation changed after image acquisition.
                    // Rebuild on the next platform-window tick without submitting
                    // a command buffer whose tracking publication was discarded.
                    _resizeRequested = true;
                    presentCompletion.Cancel(in reservation);
                    return;
                }
            }
            catch
            {
                // An image has already been acquired. Recreate the swapchain on the
                // next attempt so that failed recording cannot strand that image.
                _resizeRequested = true;
                presentCompletion.Cancel(in reservation);
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
            bool submitMayHaveDispatched = false;
            try
            {
                submitMayHaveDispatched = true;
                Result submitResult = _outputHost.SubmitPlatformDraw(ref submitInfo, frameFence);
                if (submitResult != Result.Success)
                {
                    submitMayHaveDispatched = false;
                    _resizeRequested = true;
                    presentCompletion.Cancel(in reservation);
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
                Result presentResult = _outputHost.PresentPlatformViewport(ref presentInfo, in reservation);
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
            catch
            {
                _resizeRequested = true;
                if (submitMayHaveDispatched)
                    _frameFenceSubmitted[frameSlot] = true;
                presentCompletion.Quarantine(in reservation);
                throw;
            }
        }

        private bool RecordCommandBuffer(
            CommandBuffer commandBuffer,
            uint imageIndex,
            int frameSlot,
            VulkanImGuiFrameSnapshot snapshot)
            => _outputHost.RecordPlatformViewport(
                _drawBuffers,
                commandBuffer,
                imageIndex,
                frameSlot,
                snapshot,
                _images,
                _imageViews,
                _extent,
                _imagePresented[imageIndex]);

        private bool TryRetireSwapchainResources()
        {
            for (int index = 0; index < _frameFences.Length; index++)
            {
                if (!_frameFenceSubmitted[index])
                    continue;
                Result result = _outputHost.WaitForPlatformFence(_frameFences[index]);
                if (result is Result.NotReady or Result.Timeout)
                    return false;
                ThrowIfFailed(result, "poll detached-window frame fence for retirement");
                _frameFenceSubmitted[index] = false;
            }

            for (int index = 0; index < _acquireFences.Length; index++)
            {
                if (!_acquireFenceSubmitted[index])
                    continue;
                Result result = _outputHost.WaitForPlatformFence(_acquireFences[index]);
                if (result is Result.NotReady or Result.Timeout)
                    return false;
                ThrowIfFailed(result, "poll detached-window acquire fence for retirement");
                _acquireFenceSubmitted[index] = false;
            }

            _presentCompletion?.Seal();
            return _presentCompletion is null || _presentCompletion.PollRetirement();
        }

        private void DestroySwapchainResources(bool deviceLost = false)
        {
            _presentCompletion?.Destroy(deviceLost);
            _presentCompletion = null;
            _outputHost.DestroyPlatformCommandResources(
                new VulkanImGuiPlatformWindowCommandResources(
                    _commandPool,
                    _commandBuffers,
                    _frameFences,
                    _frameFenceSubmitted,
                    _acquireFences,
                    _acquireFenceSubmitted,
                    _imageAvailableSemaphores,
                    _renderFinishedSemaphores),
                ViewportId);

            _outputHost.DestroyPlatformSwapchain(
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
            _acquireFences = [];
            _acquireFenceSubmitted = [];
            _imageAvailableSemaphores = [];
            _renderFinishedSemaphores = [];
            _rendererReady = false;
        }

        public void DrainInput()
        {
            if (_disposeStarted)
                return;
            WindowInputSnapshot input = Window.ConsumeUiInput(_uiEvents);
            _owner.ReplayViewportInput(ViewportId, Window.ClientScreenPosition, input,
                CollectionsMarshal.AsSpan(_uiEvents));
        }

        private sealed class ViewportWindowEventSink : IRuntimeWindowEventSink
        {
            public void SurfaceChanged(WindowSurfaceSnapshot _) { }
            public void FocusChanged(bool _) { }
            public void FileDropped(string[] _) { }
            public void KeyDown(EKey _) { }
            public bool CloseRequested()
            {
                return true;
            }
            public void InteractiveResizeStarted() { }
            public void InteractiveResizeUpdated(IVector2 _) { }
            public void InteractiveResizeEnded() { }
            public void RepaintRequested() { }
            public void RenderRequested(double _) { }
        }

        private static IVector2 ToWindowSize(Vector2 size)
            => new(Math.Max(1, (int)MathF.Round(size.X)), Math.Max(1, (int)MathF.Round(size.Y)));

        private static IVector2 ToWindowPosition(Vector2 position)
            => new((int)MathF.Round(position.X), (int)MathF.Round(position.Y));

        private void ThrowIfFailed(Result result, string operation)
        {
            if (result == Result.Success)
                return;

            if (result == Result.ErrorDeviceLost)
                _outputHost.MarkPlatformDeviceLost(operation, result);
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
        }
    }
