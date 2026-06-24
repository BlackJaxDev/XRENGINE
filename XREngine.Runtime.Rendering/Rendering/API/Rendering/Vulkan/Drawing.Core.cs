using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Resources;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== Basic Render State ===========

        public override void MemoryBarrier(EMemoryBarrierMask mask)
        {
            if (mask == EMemoryBarrierMask.None)
                return;

            FrameOpContext context = CaptureFrameOpContextOrLastActive();
            int passIndex = EnsureValidPassIndex(
                RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
                "MemoryBarrier",
                context.PassMetadata);

            if (passIndex == int.MinValue)
            {
                _state.RegisterMemoryBarrier(mask);
                MarkCommandBuffersDirty();
                return;
            }

            EnqueueFrameOp(new MemoryBarrierOp(passIndex, mask, context));
        }
        public override void ColorMask(bool red, bool green, bool blue, bool alpha)
        {
            _state.SetColorMask(red, green, blue, alpha);
        }

        public override void ClearColor(ColorF4 color)
        {
            _state.SetClearColor(color);
        }

        public override void CropRenderArea(BoundingRectangle region)
        {
            _state.SetScissor(region);
        }
        public override void SetRenderArea(BoundingRectangle region)
        {
            _state.SetViewport(region);
        }

        public override bool SetIndexedViewportScissors(
            ReadOnlySpan<BoundingRectangle> viewports,
            ReadOnlySpan<BoundingRectangle> scissors)
        {
            int count = Math.Min(viewports.Length, scissors.Length);
            if (count <= 0 ||
                !RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray ||
                count > RuntimeEngine.Rendering.State.MaxOpenGLViewports)
            {
                return false;
            }

            _state.SetIndexedViewportScissors(viewports[..count], scissors[..count]);
            MarkCommandBuffersDirty();
            return true;
        }

        public override void ClearIndexedViewportScissors(int count)
        {
            if (count <= 0)
                return;

            _state.ClearIndexedViewportScissors();
            MarkCommandBuffersDirty();
        }

        // =========== API Render Object Factory ===========

        protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject)
            => renderObject switch
            {
                //Meshes
                XRMaterial data => new VkMaterial(this, data),
                XRMeshRenderer.BaseVersion data => new VkMeshRenderer(this, data),
                XRRenderProgramPipeline data => new VkRenderProgramPipeline(this, data),
                XRRenderProgram data => new VkRenderProgram(this, data),
                XRDataBuffer data => new VkDataBuffer(this, data),
                XRSampler s => new VkSampler(this, s),
                XRShader s => new VkShader(this, s),

                //FBOs
                XRRenderBuffer data => new VkRenderBuffer(this, data),
                XRFrameBuffer data => new VkFrameBuffer(this, data),

                //Texture 1D
                XRTexture1D data => new VkTexture1D(this, data),
                XRTexture1DArray data => new VkTexture1DArray(this, data),
                XRTextureViewBase data => new VkTextureView(this, data),

                //Texture 2D
                XRTexture2D data => new VkTexture2D(this, data),
                XRTexture2DArray data => new VkTexture2DArray(this, data),
                XRTextureRectangle data => new VkTextureRectangle(this, data),

                //Texture 3D
                XRTexture3D data => new VkTexture3D(this, data),

                //Texture Cube
                XRTextureCube data => new VkTextureCube(this, data),
                XRTextureCubeArray data => new VkTextureCubeArray(this, data),

                //Texture Buffer
                XRTextureBuffer data => new VkTextureBuffer(this, data),

                //Feedback
                XRRenderQuery data => new VkRenderQuery(this, data),
                XRTransformFeedback data => new VkTransformFeedback(this, data),

                _ => throw new InvalidOperationException($"Render object type {renderObject.GetType()} is not supported.")
            };

        // =========== Frame Loop ===========

        private const int MAX_FRAMES_IN_FLIGHT = 2;
        private static readonly TimeSpan SwapchainRecreateDebounce = TimeSpan.FromMilliseconds(16);
        private static readonly TimeSpan SwapchainResizeSettleDelay = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan InteractiveSwapchainRecreateMinInterval = TimeSpan.FromMilliseconds(16);
        private const ulong BlockingAcquireTimeoutNanoseconds = ulong.MaxValue;
        private const ulong InteractiveResizeAcquireTimeoutNanoseconds = 0UL;

        private int currentFrame = 0;
        private ulong _vkDebugFrameCounter = 0;
        internal ulong VulkanFrameCounter => _vkDebugFrameCounter;
        private long _swapchainRecreateRequestedAt;
        private long _swapchainResizeLastChangedAt;
        private uint _pendingSurfaceWidth;
        private uint _pendingSurfaceHeight;
        private long _lastFrameCompletedTimestamp;
        private long _lastInteractiveSwapchainRecreateTimestamp;
        private int _consecutiveNotReadyCount;
        private const int MaxConsecutiveNotReadyBeforeRecreate = 3;

        private void MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker marker)
        {
            if (!_streamlineFrameGenerationSwapchainActive)
                return;

            uint frameIndex = unchecked((uint)Math.Min(uint.MaxValue, _vkDebugFrameCounter));
            if (NvidiaDlssManager.Native.TryMarkFrameGenerationPclMarker(this, marker, frameIndex, out string failureReason))
                return;

            string message = $"NVIDIA DLSS frame generation failed to set Streamline PCL marker {marker}: {failureReason}";
            Debug.RenderingError(message);
            throw new InvalidOperationException(message);
        }

        private void DrainSkippedResizeFrameOps(string reason)
        {
            FrameOp[] droppedOps = DrainFrameOps(out _);
            var liveFramebufferSize = XRWindow.EffectiveFramebufferSize;
            var resizeExtents = XRWindow.ResizeExtents;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.ResizeSkip",
                TimeSpan.FromMilliseconds(500),
                "[Vulkan] Skipping present tick while resize/presentation resources settle. Reason={0} DroppedFrameOps={1} Live={2}x{3} Swapchain={4}x{5} Presentation={6}x{7} Output={8}x{9} Internal={10}x{11}",
                reason,
                droppedOps.Length,
                liveFramebufferSize.X,
                liveFramebufferSize.Y,
                swapChainExtent.Width,
                swapChainExtent.Height,
                resizeExtents.PresentationExtent.X,
                resizeExtents.PresentationExtent.Y,
                resizeExtents.PipelineOutputExtent.X,
                resizeExtents.PipelineOutputExtent.Y,
                resizeExtents.FullInternalExtent.X,
                resizeExtents.FullInternalExtent.Y);
        }

        private void MarkSkippedResizeFrameObserved(long frameStartTimestamp)
        {
            _lastFrameCompletedTimestamp = frameStartTimestamp;
        }

        private void WaitCurrentFrameSlotAndDrainRetiredResources()
            => TryWaitCurrentFrameSlotAndDrainRetiredResources(interactiveResize: false, "blocking skipped-frame cleanup");

        private bool TryWaitCurrentFrameSlotAndDrainRetiredResources(bool interactiveResize, string reason)
        {
            if (_frameSlotTimelineValues is not null &&
                currentFrame >= 0 &&
                currentFrame < _frameSlotTimelineValues.Length)
            {
                ulong slotWaitValue = _frameSlotTimelineValues[currentFrame];
                if (interactiveResize && !HasTimelineValueCompleted(_graphicsTimelineSemaphore, slotWaitValue))
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.InteractiveResizeBusySlot",
                        TimeSpan.FromMilliseconds(500),
                        "[Vulkan] Skipping retired-resource cleanup during interactive resize because frame slot {0} is still busy. Reason={1} TimelineValue={2}",
                        currentFrame,
                        reason,
                        slotWaitValue);
                    return false;
                }

                WaitForTimelineValue(_graphicsTimelineSemaphore, slotWaitValue);
                SampleFrameTimingQueries(currentFrame);
            }

            DrainRetiredDescriptorPools();
            DrainRetiredPipelines();
            DrainRetiredBuffers();
            DrainRetiredFramebuffers();
            DrainRetiredImages();
            return true;
        }

        private bool TryGetViewportResourceBlocker(bool allowInteractiveDisplayMismatch, out string reason)
        {
            reason = string.Empty;

            var viewports = XRWindow.Viewports;
            for (int i = 0; i < viewports.Count; i++)
            {
                XRViewport viewport = viewports[i];
                if (viewport.Width <= 0 || viewport.Height <= 0)
                    continue;

                XRRenderPipelineInstance instance = viewport.RenderPipelineInstance;
                if (instance.SkippedResizeCatchUpThisFrame)
                {
                    reason = $"VP[{viewport.Index}] skipped command-chain execution this frame while resize resources catch up";
                    return true;
                }

                RenderResourceGeneration? activeGeneration = instance.ActiveGeneration;
                RenderResourceGeneration? pendingGeneration = instance.PendingGeneration;
                uint displayWidth = (uint)Math.Max(1, viewport.Width);
                uint displayHeight = (uint)Math.Max(1, viewport.Height);
                uint internalWidth = (uint)Math.Max(1, viewport.InternalWidth);
                uint internalHeight = (uint)Math.Max(1, viewport.InternalHeight);

                if (activeGeneration is null)
                {
                    reason = $"VP[{viewport.Index}] has no active resource generation; pending={pendingGeneration?.Key.ToString() ?? "<none>"}";
                    return true;
                }

                ResourceGenerationKey key = activeGeneration.Key;
                if (key.DisplayWidth == displayWidth &&
                    key.DisplayHeight == displayHeight &&
                    key.InternalWidth == internalWidth &&
                    key.InternalHeight == internalHeight)
                {
                    continue;
                }

                bool internalMatches =
                    key.InternalWidth == internalWidth &&
                    key.InternalHeight == internalHeight;

                if (allowInteractiveDisplayMismatch && internalMatches)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.InteractiveDisplayResourceMismatch.{viewport.Index}",
                        TimeSpan.FromMilliseconds(500),
                        "[Vulkan] Allowing presentation-only display mismatch during interactive resize. VP[{0}] activeDisplay={1}x{2} currentDisplay={3}x{4} internal={5}x{6}",
                        viewport.Index,
                        key.DisplayWidth,
                        key.DisplayHeight,
                        displayWidth,
                        displayHeight,
                        internalWidth,
                        internalHeight);
                    continue;
                }

                bool pendingMatchesCurrent =
                    pendingGeneration is not null &&
                    pendingGeneration.Key.DisplayWidth == displayWidth &&
                    pendingGeneration.Key.DisplayHeight == displayHeight &&
                    pendingGeneration.Key.InternalWidth == internalWidth &&
                    pendingGeneration.Key.InternalHeight == internalHeight;

                if (!pendingMatchesCurrent)
                {
                    _ = instance.RequestResourceGeneration(
                        (int)displayWidth,
                        (int)displayHeight,
                        (int)internalWidth,
                        (int)internalHeight,
                        "VulkanResizeResourceMismatch");

                    pendingGeneration = instance.PendingGeneration;
                    pendingMatchesCurrent =
                        pendingGeneration is not null &&
                        pendingGeneration.Key.DisplayWidth == displayWidth &&
                        pendingGeneration.Key.DisplayHeight == displayHeight &&
                        pendingGeneration.Key.InternalWidth == internalWidth &&
                        pendingGeneration.Key.InternalHeight == internalHeight;
                }

                if (pendingMatchesCurrent && internalMatches)
                {
                    RenderResourceGeneration pending = pendingGeneration!;
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.PendingResizeResourceCatchUp.{viewport.Index}",
                        TimeSpan.FromMilliseconds(500),
                        "[Vulkan] Allowing active presentation-size mismatch while pending generation catches up. VP[{0}] active={1}x{2}/{3}x{4} pending={5} current={6}x{7}/{8}x{9}",
                        viewport.Index,
                        key.DisplayWidth,
                        key.DisplayHeight,
                        key.InternalWidth,
                        key.InternalHeight,
                        pending.Key,
                        displayWidth,
                        displayHeight,
                        internalWidth,
                        internalHeight);
                    continue;
                }

                if (pendingMatchesCurrent)
                    continue;

                reason =
                    $"VP[{viewport.Index}] active={key.DisplayWidth}x{key.DisplayHeight}/{key.InternalWidth}x{key.InternalHeight} " +
                    $"current={displayWidth}x{displayHeight}/{internalWidth}x{internalHeight} pending={pendingGeneration?.Key.ToString() ?? "<none>"}";
                return true;
            }

            return false;
        }

        private void ScheduleSwapchainRecreate(string reason)
        {
            long now = Stopwatch.GetTimestamp();
            bool wasInvalidated = _frameBufferInvalidated;
            _frameBufferInvalidated = true;

            if (!wasInvalidated || _swapchainRecreateRequestedAt == 0)
                _swapchainRecreateRequestedAt = now;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateScheduled",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Scheduled debounced swapchain recreate. Reason={0} RequestedAtTicks={1} WasInvalidated={2}",
                reason,
                _swapchainRecreateRequestedAt,
                wasInvalidated);
        }

        private void RecreateSwapchainImmediately(string reason)
        {
            long recreateStart = Stopwatch.GetTimestamp();
            uint previousWidth = swapChainExtent.Width;
            uint previousHeight = swapChainExtent.Height;
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateImmediate",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Recreating swapchain immediately. Reason={0}",
                reason);

            if (!RecreateSwapChain())
            {
                TimeSpan failedElapsed = Stopwatch.GetElapsedTime(recreateStart);
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecreateResult",
                    TimeSpan.FromMilliseconds(500),
                    "[Vulkan] Swapchain recreate deferred/failed. Reason={0} ElapsedMs={1:F3} Previous={2}x{3} Current={4}x{5}",
                    reason,
                    failedElapsed.TotalMilliseconds,
                    previousWidth,
                    previousHeight,
                    swapChainExtent.Width,
                    swapChainExtent.Height);
                ScheduleSwapchainRecreate($"{reason}; surface not presentable yet");
                return;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(recreateStart);
            _frameBufferInvalidated = false;
            _swapchainRecreateRequestedAt = 0;
            _swapchainResizeLastChangedAt = 0;
            _pendingSurfaceWidth = 0;
            _pendingSurfaceHeight = 0;
            ResetImGuiFrameMarker();

            var liveFramebufferSize = XRWindow.EffectiveFramebufferSize;
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateResult",
                TimeSpan.FromMilliseconds(500),
                "[Vulkan] Swapchain recreate completed. Reason={0} ElapsedMs={1:F3} Previous={2}x{3} Current={4}x{5} Live={6}x{7} Divergence={8}x{9}",
                reason,
                elapsed.TotalMilliseconds,
                previousWidth,
                previousHeight,
                swapChainExtent.Width,
                swapChainExtent.Height,
                liveFramebufferSize.X,
                liveFramebufferSize.Y,
                (int)liveFramebufferSize.X - (int)swapChainExtent.Width,
                (int)liveFramebufferSize.Y - (int)swapChainExtent.Height);
        }

        private bool ShouldRunSwapchainRecreate(bool interactiveResize)
        {
            if (!_frameBufferInvalidated)
                return false;

            if (interactiveResize)
                return true;

            if (_swapchainRecreateRequestedAt == 0)
                return true;

            return Stopwatch.GetElapsedTime(_swapchainRecreateRequestedAt) >= SwapchainRecreateDebounce;
        }

        private bool ShouldRunInteractiveSwapchainRecreate()
        {
            long last = _lastInteractiveSwapchainRecreateTimestamp;
            return last == 0 ||
                Stopwatch.GetElapsedTime(last) >= InteractiveSwapchainRecreateMinInterval;
        }

        protected override void WindowRenderCallback(double delta)
        {
            if (_deviceLost)
                throw CreateDeviceLostException("RenderWindow", Result.ErrorDeviceLost);

            _vkDebugFrameCounter++;

            long frameStartTimestamp = Stopwatch.GetTimestamp();

            // Log large gaps between render frames — helps identify CPU-side stalls that
            // could lead to stale GPU state or TDR timeouts.
            if (_lastFrameCompletedTimestamp != 0)
            {
                TimeSpan gap = Stopwatch.GetElapsedTime(_lastFrameCompletedTimestamp, frameStartTimestamp);
                if (gap > TimeSpan.FromSeconds(5))
                {
                    Debug.VulkanWarning(
                        $"[Vulkan] Frame {_vkDebugFrameCounter}: {gap.TotalSeconds:F1}s gap since last frame completed. " +
                        $"Slot={currentFrame} SlotTimelineValue={_frameSlotTimelineValues?[currentFrame]}");
                }
            }

            TimeSpan waitFenceTime = TimeSpan.Zero;
            TimeSpan acquireImageTime = TimeSpan.Zero;
            TimeSpan recordCommandBufferTime = TimeSpan.Zero;
            TimeSpan submitQueueTime = TimeSpan.Zero;
            TimeSpan trimStagingTime = TimeSpan.Zero;
            TimeSpan presentQueueTime = TimeSpan.Zero;
            TimeSpan sampleTimingQueriesTime = TimeSpan.Zero;
            TimeSpan drainRetiredResourcesTime = TimeSpan.Zero;
            TimeSpan acquireBridgeSubmitTime = TimeSpan.Zero;
            TimeSpan waitSwapchainImageTime = TimeSpan.Zero;
            TimeSpan resetDynamicUniformRingTime = TimeSpan.Zero;

            try
            {
            bool interactiveResize = XRWindow.IsInteractiveResizeInProgress;

            // Some platforms/drivers do not reliably emit out-of-date/suboptimal or resize callbacks
            // on every size transition. Proactively compare the live framebuffer size to the current
            // swapchain extent and trigger a rebuild when they diverge.
            var liveFramebufferSize = XRWindow.EffectiveFramebufferSize;
            var liveWindowSize = Window.Size;
            uint liveSurfaceWidth = liveFramebufferSize.X > 0
                ? (uint)liveFramebufferSize.X
                : (uint)Math.Max(liveWindowSize.X, 0);
            uint liveSurfaceHeight = liveFramebufferSize.Y > 0
                ? (uint)liveFramebufferSize.Y
                : (uint)Math.Max(liveWindowSize.Y, 0);

            bool liveSurfaceValid = liveSurfaceWidth > 0 && liveSurfaceHeight > 0;

            if (liveSurfaceValid)
            {
                if (_pendingSurfaceWidth != liveSurfaceWidth || _pendingSurfaceHeight != liveSurfaceHeight)
                {
                    _pendingSurfaceWidth = liveSurfaceWidth;
                    _pendingSurfaceHeight = liveSurfaceHeight;
                    _swapchainResizeLastChangedAt = Stopwatch.GetTimestamp();
                }
            }
            else
            {
                _pendingSurfaceWidth = 0;
                _pendingSurfaceHeight = 0;
                _swapchainResizeLastChangedAt = 0;
            }

            bool surfaceMatchesSwapchain = liveSurfaceValid &&
                liveSurfaceWidth == swapChainExtent.Width &&
                liveSurfaceHeight == swapChainExtent.Height;

            if (liveSurfaceValid && !surfaceMatchesSwapchain)
            {
                if (interactiveResize)
                {
                    ScheduleSwapchainRecreate("Interactive resize surface/swapchain size mismatch");
                }
                else
                {
                    ScheduleSwapchainRecreate("Surface/swapchain size mismatch");
                }

                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.SizeMismatch",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Detected surface/swapchain size mismatch: WindowFB={0}x{1} Window={2}x{3} LiveSurface={4}x{5} Swapchain={6}x{7}. Scheduling swapchain recreate.",
                    liveFramebufferSize.X,
                    liveFramebufferSize.Y,
                    liveWindowSize.X,
                    liveWindowSize.Y,
                    liveSurfaceWidth,
                    liveSurfaceHeight,
                    swapChainExtent.Width,
                    swapChainExtent.Height);
            }
            else if (_pendingSurfaceWidth == swapChainExtent.Width && _pendingSurfaceHeight == swapChainExtent.Height)
            {
                _pendingSurfaceWidth = 0;
                _pendingSurfaceHeight = 0;
                _swapchainResizeLastChangedAt = 0;
            }

            // If the window resized (or other framebuffer-dependent state changed), rebuild swapchain resources
            // before we acquire/record/submit. Waiting until after present can cause visible stretching/borders.
            if (ShouldRunSwapchainRecreate(interactiveResize))
            {
                bool hasPendingSurfaceSize = _pendingSurfaceWidth > 0 && _pendingSurfaceHeight > 0;
                bool pendingMatchesLive = !hasPendingSurfaceSize ||
                    (_pendingSurfaceWidth == liveSurfaceWidth && _pendingSurfaceHeight == liveSurfaceHeight);
                bool resizeSettled = !hasPendingSurfaceSize ||
                    (_swapchainResizeLastChangedAt != 0 &&
                     Stopwatch.GetElapsedTime(_swapchainResizeLastChangedAt) >= SwapchainResizeSettleDelay);

                if (interactiveResize)
                {
                    if (pendingMatchesLive && ShouldRunInteractiveSwapchainRecreate())
                    {
                        RecreateSwapchainImmediately("Interactive resize presentation extent");
                        _lastInteractiveSwapchainRecreateTimestamp = Stopwatch.GetTimestamp();
                        surfaceMatchesSwapchain = liveSurfaceValid &&
                            liveSurfaceWidth == swapChainExtent.Width &&
                            liveSurfaceHeight == swapChainExtent.Height;
                    }
                    else
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForInteractiveResize",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Deferring interactive swapchain recreate. Pending={0}x{1} Live={2}x{3} Swapchain={4}x{5} PendingMatchesLive={6}",
                            _pendingSurfaceWidth,
                            _pendingSurfaceHeight,
                            liveSurfaceWidth,
                            liveSurfaceHeight,
                            swapChainExtent.Width,
                            swapChainExtent.Height,
                            pendingMatchesLive);
                    }
                }
                else if (pendingMatchesLive && resizeSettled)
                {
                    _lastInteractiveSwapchainRecreateTimestamp = 0;
                    RecreateSwapchainImmediately("Debounce elapsed before frame acquire (resize settled)");
                    surfaceMatchesSwapchain = liveSurfaceValid &&
                        liveSurfaceWidth == swapChainExtent.Width &&
                        liveSurfaceHeight == swapChainExtent.Height;
                }
                else
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForResizeSettle",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Debounce elapsed but resize is still active. Deferring swapchain recreate. Pending={0}x{1} Live={2}x{3} Settled={4}",
                        _pendingSurfaceWidth,
                        _pendingSurfaceHeight,
                        liveSurfaceWidth,
                        liveSurfaceHeight,
                        resizeSettled);
                }
            }

            if (!liveSurfaceValid)
            {
                _ = TryWaitCurrentFrameSlotAndDrainRetiredResources(interactiveResize, "Live surface size is zero");
                DrainSkippedResizeFrameOps("Live surface size is zero");
                MarkSkippedResizeFrameObserved(frameStartTimestamp);
                return;
            }

            if (_frameBufferInvalidated || !surfaceMatchesSwapchain)
            {
                _ = TryWaitCurrentFrameSlotAndDrainRetiredResources(interactiveResize, "Swapchain resize/recreate pending");
                DrainSkippedResizeFrameOps(
                    $"Swapchain resize/recreate pending. Pending={_pendingSurfaceWidth}x{_pendingSurfaceHeight} " +
                    $"Live={liveSurfaceWidth}x{liveSurfaceHeight} Swapchain={swapChainExtent.Width}x{swapChainExtent.Height}");
                MarkSkippedResizeFrameObserved(frameStartTimestamp);
                return;
            }

            if (TryGetViewportResourceBlocker(interactiveResize, out string resourceMismatchReason))
            {
                _ = TryWaitCurrentFrameSlotAndDrainRetiredResources(interactiveResize, resourceMismatchReason);
                DrainSkippedResizeFrameOps(resourceMismatchReason);
                MarkSkippedResizeFrameObserved(frameStartTimestamp);
                return;
            }

            bool frameGenerationRequested = NvidiaDlssManager.IsFrameGenerationRequested;
            if (_streamlineFrameGenerationSwapchainActive != frameGenerationRequested)
            {
                RecreateSwapchainImmediately(
                    frameGenerationRequested
                        ? "NVIDIA DLSS frame generation enabled; recreating swapchain through Streamline"
                        : "NVIDIA DLSS frame generation disabled; recreating swapchain without Streamline");
                return;
            }

            // 1. Wait for the previous submission associated with this in-flight slot.
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.WaitFrameSlot"))
            {
                ulong slotWaitValue = _frameSlotTimelineValues![currentFrame];
                if (interactiveResize && !HasTimelineValueCompleted(_graphicsTimelineSemaphore, slotWaitValue))
                {
                    DrainSkippedResizeFrameOps(
                        $"Interactive resize frame slot {currentFrame} is still busy. TimelineValue={slotWaitValue}");
                    MarkSkippedResizeFrameObserved(frameStartTimestamp);
                    return;
                }

                WaitForTimelineValue(_graphicsTimelineSemaphore, slotWaitValue);
            }
            waitFenceTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            // Now that the GPU has finished all work for this frame slot, destroy
            // resources that were retired during its previous recording.
            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.DrainRetiredResources"))
            {
                DrainRetiredDescriptorPools();
                DrainRetiredPipelines();
                DrainRetiredBuffers();
                DrainRetiredFramebuffers();
                DrainRetiredImages();
            }
            drainRetiredResourcesTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            // Helpful when tracking down DPI / resize issues.
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.Sizes",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Frame={0} WindowFB={1}x{2} Swapchain={3}x{4}",
                _vkDebugFrameCounter,
                liveFramebufferSize.X,
                liveFramebufferSize.Y,
                swapChainExtent.Width,
                swapChainExtent.Height);

            // 2. Acquire the next image from the swap chain
            uint imageIndex = 0;
            stageStartTimestamp = Stopwatch.GetTimestamp();
            Semaphore acquireSemaphore = acquireBridgeSemaphores![currentFrame];
            Result result;
            ulong acquireTimeoutNanoseconds = interactiveResize
                ? InteractiveResizeAcquireTimeoutNanoseconds
                : BlockingAcquireTimeoutNanoseconds;
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.AcquireNextImage"))
            {
                if (_streamlineFrameGenerationSwapchainActive)
                {
                    if (!NvidiaDlssManager.Native.TryAcquireProxyNextImage(this, swapChain, acquireTimeoutNanoseconds, acquireSemaphore, default, ref imageIndex, out result, out string failureReason))
                    {
                        if (result == Result.ErrorDeviceLost)
                            throw CreateDeviceLostException("Streamline AcquireNextImage", result);

                        string message = $"NVIDIA DLSS frame generation failed to acquire the swapchain image through Streamline: {failureReason}";
                        Debug.RenderingError(message);
                        throw new InvalidOperationException(message);
                    }
                }
                else
                {
                    result = khrSwapChain!.AcquireNextImage(device, swapChain, acquireTimeoutNanoseconds, acquireSemaphore, default, ref imageIndex);
                }
            }
            acquireImageTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.Acquire",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Frame={0} InFlightSlot={1} AcquiredImage={2} LastPresented={3}",
                _vkDebugFrameCounter,
                currentFrame,
                imageIndex,
                _lastPresentedImageIndex);

            if (result == Result.ErrorDeviceLost)
            {
                throw CreateDeviceLostException("AcquireNextImage", result);
            }
            else if (result == Result.ErrorOutOfDateKhr)
            {
                ScheduleSwapchainRecreate("AcquireNextImage returned ErrorOutOfDateKhr");
                return;
            }
            else if (result == Result.ErrorSurfaceLostKhr)
            {
                RecreateSwapchainImmediately("AcquireNextImage returned ErrorSurfaceLostKhr");
                return;
            }
            else if (result == Result.SuboptimalKhr)
            {
                ScheduleSwapchainRecreate("AcquireNextImage returned SuboptimalKhr");
                return;
            }
            else if (result == Result.NotReady || result == Result.Timeout)
            {
                if (interactiveResize)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.InteractiveAcquireNotReady",
                        TimeSpan.FromMilliseconds(500),
                        "[Vulkan] AcquireNextImage returned {0} during interactive resize; skipping this repaint tick.",
                        result);
                    DrainSkippedResizeFrameOps($"AcquireNextImage returned {result} during interactive resize");
                    MarkSkippedResizeFrameObserved(frameStartTimestamp);
                    return;
                }

                _consecutiveNotReadyCount++;
                if (_consecutiveNotReadyCount >= MaxConsecutiveNotReadyBeforeRecreate)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Frame.{GetHashCode()}.AcquireNotReady.Recreate",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] AcquireNextImage returned NotReady {0} consecutive times. Recreating swapchain to recover.",
                        _consecutiveNotReadyCount);
                    _consecutiveNotReadyCount = 0;
                    RecreateSwapchainImmediately("Persistent NotReady after failed frame");
                }
                else
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Frame.{GetHashCode()}.AcquireNotReady",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] AcquireNextImage returned NotReady ({0}/{1}). Skipping this frame.",
                        _consecutiveNotReadyCount,
                        MaxConsecutiveNotReadyBeforeRecreate);
                }
                return;
            }
            else if (result != Result.Success && result != Result.SuboptimalKhr)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Frame.{GetHashCode()}.AcquireFailure.{(int)result}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] AcquireNextImage failed with {0}.",
                    result);

                throw new Exception($"Failed to acquire swap chain image ({result}).");
            }

            // Successful acquire — reset the NotReady counter.
            _consecutiveNotReadyCount = 0;

            // 3. Bridge the binary acquire semaphore into the timeline and serialize image reuse by timeline value.
            _acquireTimelineValue = Math.Max(_acquireTimelineValue + 1, _graphicsTimelineValue + 1);

            ulong* acquireSignalValues = stackalloc ulong[1] { _acquireTimelineValue };
            ulong* acquireWaitValues = stackalloc ulong[1] { 0UL };
            Semaphore* acquireWaitSemaphores = stackalloc Semaphore[1] { acquireSemaphore };
            Semaphore* acquireSignalSemaphores = stackalloc Semaphore[1] { _graphicsTimelineSemaphore };
            PipelineStageFlags* acquireWaitStages = stackalloc PipelineStageFlags[1] { PipelineStageFlags.TopOfPipeBit };

            TimelineSemaphoreSubmitInfo acquireBridgeTimelineInfo = new()
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = 1,
                PWaitSemaphoreValues = acquireWaitValues,
                SignalSemaphoreValueCount = 1,
                PSignalSemaphoreValues = acquireSignalValues,
            };

            SubmitInfo acquireBridgeSubmit = new()
            {
                SType = StructureType.SubmitInfo,
                PNext = &acquireBridgeTimelineInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = acquireWaitSemaphores,
                PWaitDstStageMask = acquireWaitStages,
                CommandBufferCount = 0,
                PCommandBuffers = null,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = acquireSignalSemaphores,
            };

            Result bridgeResult;
            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.AcquireBridgeSubmit"))
            {
                bridgeResult = SubmitToQueueTracked(graphicsQueue, ref acquireBridgeSubmit, default);
            }
            acquireBridgeSubmitTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
            if (bridgeResult != Result.Success)
            {
                if (bridgeResult == Result.ErrorDeviceLost)
                    throw CreateDeviceLostException("Acquire bridge QueueSubmit", bridgeResult);

                throw new Exception($"Failed to bridge swapchain acquire semaphore to timeline ({bridgeResult}).");
            }

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.WaitSwapchainImage"))
            {
                if (_swapchainImageTimelineValues is not null && imageIndex < _swapchainImageTimelineValues.Length)
                    WaitForTimelineValue(_graphicsTimelineSemaphore, _swapchainImageTimelineValues[imageIndex]);
            }
            waitSwapchainImageTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.SampleTimingQueries"))
            {
                SampleFrameTimingQueries(unchecked((int)Math.Min(imageIndex, int.MaxValue)));
            }
            sampleTimingQueriesTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            // 4. Reset per-frame dynamic uniform ring buffer for this image.
            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.ResetDynamicUniformRing"))
            {
                ResetDynamicUniformRingBuffer(imageIndex);
            }
            resetDynamicUniformRingTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            // 5. Snapshot ImGui before recording the scene primary so the primary can
            // leave the swapchain in color-attachment layout when an overlay will own
            // the final transition to PresentSrcKhr.
            ImGuiFrameSnapshot? imguiOverlaySnapshot = null;
            bool hasPendingImGuiOverlay = false;
            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.SnapshotImGuiOverlay"))
            {
                bool canRecordImGuiOverlay = CanRecordImGuiOverlayCommandBuffer(imageIndex);
                if (canRecordImGuiOverlay)
                    hasPendingImGuiOverlay = TryConsumeRenderableImGuiOverlaySnapshot(out imguiOverlaySnapshot);
            }
            recordCommandBufferTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            bool preserveSwapchainForImGuiOverlay = hasPendingImGuiOverlay && UseDynamicRenderingRenderTargets;

            // 6. Record the scene command buffer. ImGui is recorded into a separate
            // per-frame overlay buffer below so cached scene primaries do not freeze UI.
            CommandBuffer submitCommandBuffer;
            CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer;
            int dynamicUiBatchTextOverlayOpCount;
            ImageLayout swapchainLayoutAfterScene;
            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.RecordCommandBuffer"))
            {
                long recordAllocationStart = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    submitCommandBuffer = EnsureCommandBufferRecorded(
                        imageIndex,
                        preserveSwapchainForImGuiOverlay,
                        out dynamicUiBatchTextSecondaryCommandBuffer,
                        out dynamicUiBatchTextOverlayOpCount,
                        out swapchainLayoutAfterScene);
                }
                catch (Exception recordEx)
                {
                    recordCommandBufferTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

                // Recording failed (e.g. OOM during resource allocation). The acquire bridge
                // already consumed the binary semaphore and advanced the timeline, but we have
                // no valid command buffer to submit. Advance currentFrame so the next attempt
                // uses the other in-flight slot, and schedule a swapchain recreate which calls
                // DeviceWaitIdle + destroys/recreates all swapchain objects — this returns the
                // acquired image to the presentation engine and resets semaphore state.
                    currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;

                    Debug.VulkanWarningEvery(
                        $"Vulkan.Frame.{GetHashCode()}.RecordFailed",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Command buffer recording failed. Scheduling swapchain recreate to recover. {0}",
                        recordEx.Message);

                    RecreateSwapchainImmediately("Command buffer recording failed — recovering timeline/present state");
                    throw; // Re-throw so XRWindow's circuit breaker can track failure count
                }
                finally
                {
                    long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - recordAllocationStart;
                    if (_lastEnsureCommandBufferRecordedPrimary)
                        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRecordCommandBufferAllocation(allocatedBytes);
                }
            }
            recordCommandBufferTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            CommandBuffer imguiOverlayCommandBuffer = default;
            bool hasImGuiOverlayCommandBuffer = false;
            CommandBuffer dynamicUiBatchTextOverlayCommandBuffer = default;
            bool hasDynamicUiBatchTextOverlayCommandBuffer = false;
            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.RecordImGuiOverlay"))
            {
                try
                {
                    hasImGuiOverlayCommandBuffer = imguiOverlaySnapshot is not null &&
                        TryRecordImGuiOverlayCommandBuffer(
                            imageIndex,
                            imguiOverlaySnapshot,
                            swapchainLayoutAfterScene,
                            out imguiOverlayCommandBuffer);
                    if (preserveSwapchainForImGuiOverlay && !hasImGuiOverlayCommandBuffer)
                        throw new InvalidOperationException("Scene primary preserved the swapchain for ImGui, but the overlay command buffer was not recorded.");
                }
                catch (Exception overlayEx)
                {
                    recordCommandBufferTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
                    currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;

                    Debug.VulkanWarningEvery(
                        $"Vulkan.Frame.{GetHashCode()}.RecordImGuiOverlayFailed",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] ImGui overlay command buffer recording failed. Scheduling swapchain recreate to recover. {0}",
                        overlayEx.Message);

                    RecreateSwapchainImmediately("ImGui overlay command buffer recording failed - recovering timeline/present state");
                    throw;
                }
            }
            recordCommandBufferTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            if (dynamicUiBatchTextOverlayOpCount > 0)
            {
                Debug.VulkanEvery(
                    $"Vulkan.DynamicUiText.LateOverlayDecision.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Dynamic UI text late-overlay decision: preserveForImGui={0} hasImGui={1} ops={2} secondary=0x{3:X}",
                    preserveSwapchainForImGuiOverlay,
                    hasImGuiOverlayCommandBuffer,
                    dynamicUiBatchTextOverlayOpCount,
                    dynamicUiBatchTextSecondaryCommandBuffer.Handle);
            }

            if (preserveSwapchainForImGuiOverlay &&
                hasImGuiOverlayCommandBuffer &&
                dynamicUiBatchTextOverlayOpCount > 0)
            {
                stageStartTimestamp = Stopwatch.GetTimestamp();
                using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.RecordDynamicUiTextOverlay"))
                {
                    try
                    {
                        hasDynamicUiBatchTextOverlayCommandBuffer = TryRecordDynamicUiBatchTextOverlayCommandBuffer(
                            imageIndex,
                            dynamicUiBatchTextSecondaryCommandBuffer,
                            dynamicUiBatchTextOverlayOpCount,
                            ImageLayout.PresentSrcKhr,
                            out dynamicUiBatchTextOverlayCommandBuffer);
                    }
                    catch (Exception overlayEx)
                    {
                        recordCommandBufferTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
                        currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;

                        Debug.VulkanWarningEvery(
                            $"Vulkan.Frame.{GetHashCode()}.RecordDynamicUiTextOverlayFailed",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Dynamic UI text overlay command buffer recording failed. Scheduling swapchain recreate to recover. {0}",
                            overlayEx.Message);

                        RecreateSwapchainImmediately("Dynamic UI text overlay command buffer recording failed - recovering timeline/present state");
                        throw;
                    }
                }
                recordCommandBufferTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
            }

            // 5. Submit the command buffer with timeline sync.
            _graphicsTimelineValue = Math.Max(_graphicsTimelineValue + 1, _acquireTimelineValue + 1);
            ulong graphicsSignalValue = _graphicsTimelineValue;

            ulong* waitTimelineValues = stackalloc ulong[1] { _acquireTimelineValue };
            ulong* signalTimelineValues = stackalloc ulong[2] { graphicsSignalValue, 0UL };
            Semaphore* waitSemaphores = stackalloc Semaphore[1] { _graphicsTimelineSemaphore };
            var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };
            CommandBuffer* submitCommandBuffers = stackalloc CommandBuffer[3];
            submitCommandBuffers[0] = submitCommandBuffer;
            uint submitCommandBufferCount = 1;
            if (hasImGuiOverlayCommandBuffer && imguiOverlayCommandBuffer.Handle != 0)
                submitCommandBuffers[submitCommandBufferCount++] = imguiOverlayCommandBuffer;
            if (hasDynamicUiBatchTextOverlayCommandBuffer && dynamicUiBatchTextOverlayCommandBuffer.Handle != 0)
                submitCommandBuffers[submitCommandBufferCount++] = dynamicUiBatchTextOverlayCommandBuffer;
            Semaphore presentSemaphore = presentBridgeSemaphores![imageIndex];
            Semaphore* signalSemaphores = stackalloc Semaphore[2] { _graphicsTimelineSemaphore, presentSemaphore };

            TimelineSemaphoreSubmitInfo timelineSubmitInfo = new()
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = 1,
                PWaitSemaphoreValues = waitTimelineValues,
                SignalSemaphoreValueCount = 2,
                PSignalSemaphoreValues = signalTimelineValues,
            };

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineSubmitInfo,
            };

            submitInfo = submitInfo with
            {
                WaitSemaphoreCount = 1,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = submitCommandBufferCount,
                PCommandBuffers = submitCommandBuffers
            };

            submitInfo = submitInfo with
            {
                SignalSemaphoreCount = 2,
                PSignalSemaphores = signalSemaphores,
            };

            stageStartTimestamp = Stopwatch.GetTimestamp();
            Result submitResult;
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.Submit"))
            {
                MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.RenderSubmitStart);
                lock (_oneTimeSubmitLock)
                {
                    submitResult = SubmitToQueueTracked(graphicsQueue, ref submitInfo, default);
                }
                MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.RenderSubmitEnd);
            }

            if (submitResult != Result.Success)
            {
                if (submitResult == Result.ErrorDeviceLost)
                    throw CreateDeviceLostException("Draw QueueSubmit", submitResult);

                throw new Exception($"Failed to submit draw command buffer ({submitResult}).");
            }
            submitQueueTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
            MarkFrameTimingSubmitted(unchecked((int)Math.Min(imageIndex, int.MaxValue)));

            _frameSlotTimelineValues[currentFrame] = graphicsSignalValue;
            if (_swapchainImageTimelineValues is not null && imageIndex < _swapchainImageTimelineValues.Length)
                _swapchainImageTimelineValues[imageIndex] = graphicsSignalValue;

            // Trim idle staging buffers so the pool does not grow unbounded.
            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.TrimStaging"))
            {
                _stagingManager.Trim(this);
            }
            trimStagingTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.Submit",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Frame={0} SubmittedImage={1}",
                _vkDebugFrameCounter,
                imageIndex);

            // 6. Present the image
            var swapChains = stackalloc[] { swapChain };
            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &presentSemaphore,
                SwapchainCount = 1,
                PSwapchains = swapChains,
                PImageIndices = &imageIndex
            };

            stageStartTimestamp = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.QueuePresent"))
            {
                MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.PresentStart);
                lock (_oneTimeSubmitLock)
                {
                    if (_streamlineFrameGenerationSwapchainActive)
                    {
                        if (!NvidiaDlssManager.Native.TryQueueProxyPresent(this, presentQueue, ref presentInfo, out result, out string failureReason))
                        {
                            if (result == Result.ErrorDeviceLost)
                                throw CreateDeviceLostException("Streamline QueuePresent", result);

                            string message = $"NVIDIA DLSS frame generation failed to present through Streamline: {failureReason}";
                            Debug.RenderingError(message);
                            throw new InvalidOperationException(message);
                        }
                    }
                    else
                    {
                        result = khrSwapChain!.QueuePresent(presentQueue, ref presentInfo);
                    }
                }
                MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.PresentEnd);
            }
            presentQueueTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
            _lastPresentedImageIndex = imageIndex;

            // Track that this swapchain image has been presented at least once,
            // so future frames use PresentSrcKhr (not Undefined) as old layout.
            if (_swapchainImageEverPresented is not null && imageIndex < _swapchainImageEverPresented.Length)
                _swapchainImageEverPresented[imageIndex] = true;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.Present",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Frame={0} PresentedImage={1} Result={2}",
                _vkDebugFrameCounter,
                imageIndex,
                result);

            if (result == Result.ErrorDeviceLost)
            {
                throw CreateDeviceLostException("QueuePresent", result);
            }
            else if (result == Result.ErrorOutOfDateKhr)
            {
                ScheduleSwapchainRecreate("QueuePresent returned ErrorOutOfDateKhr");
            }
            else if (result == Result.SuboptimalKhr)
            {
                ScheduleSwapchainRecreate("QueuePresent returned SuboptimalKhr");
            }
            else if (result == Result.ErrorSurfaceLostKhr)
            {
                RecreateSwapchainImmediately("QueuePresent returned ErrorSurfaceLostKhr");
            }
            else if (result != Result.Success)
                throw new Exception($"Failed to present swap chain image ({result}).");

            if (!interactiveResize && ShouldRunSwapchainRecreate(interactiveResize: false))
                RecreateSwapchainImmediately("Debounce elapsed after present");

            currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
            _lastFrameCompletedTimestamp = Stopwatch.GetTimestamp();
            }
            finally
            {
                TimeSpan totalFrameTime = Stopwatch.GetElapsedTime(frameStartTimestamp);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameLifecycleTiming(
                    waitFenceTime,
                    acquireImageTime,
                    recordCommandBufferTime,
                    submitQueueTime,
                    trimStagingTime,
                    presentQueueTime,
                    totalFrameTime);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameLifecycleDetailTiming(
                    sampleTimingQueriesTime,
                    drainRetiredResourcesTime,
                    acquireBridgeSubmitTime,
                    waitSwapchainImageTime,
                    resetDynamicUniformRingTime);
            }
        }
    }
}
