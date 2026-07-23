using System;
using System.Diagnostics;
using System.Threading;
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

        /// <summary>
        /// Inserts a memory barrier into the current frame, ensuring proper synchronization of memory accesses based on the specified barrier mask.
        /// </summary>
        /// <param name="mask">The memory barrier mask specifying the types of memory accesses to synchronize.</param>
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
                ActiveState.RegisterMemoryBarrier(mask);
                MarkCommandBuffersDirty();
                return;
            }

            EnqueueFrameOp(new MemoryBarrierOp(passIndex, mask, context));
        }

        /// <summary>
        /// Publishes the attachments of the specified frame buffer for sampling in subsequent rendering passes.
        /// </summary>
        /// <param name="frameBuffer">The frame buffer whose attachments are to be published for sampling.</param>
        public override void PublishFrameBufferAttachmentsForSampling(XRFrameBuffer frameBuffer)
        {
            ArgumentNullException.ThrowIfNull(frameBuffer);

            FrameOpContext context;
            int passIndex;
            if (TryGetLastFrameOpForTarget(frameBuffer, out FrameOp lastWriter))
            {
                context = lastWriter.Context;
                passIndex = lastWriter.PassIndex;
            }
            else
            {
                context = CaptureFrameOpContextOrLastActive();
                passIndex = EnsureValidPassIndex(
                    RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
                    "PublishFrameBufferAttachmentsForSampling",
                    context.PassMetadata);
            }

            EnqueueFrameOp(new PublishFramebufferForSamplingOp(passIndex, frameBuffer, context));
        }

        /// <summary>
        /// Sets the color mask for rendering, specifying which color channels are writable.
        /// </summary>
        /// <param name="red">Indicates whether the red color channel is writable.</param>
        /// <param name="green">Indicates whether the green color channel is writable.</param>
        /// <param name="blue">Indicates whether the blue color channel is writable.</param>
        /// <param name="alpha">Indicates whether the alpha color channel is writable.</param>
        public override void ColorMask(bool red, bool green, bool blue, bool alpha)
        {
            ActiveState.SetColorMask(red, green, blue, alpha);
        }

        public override void ClearColor(ColorF4 color)
        {
            ActiveState.SetClearColor(color);
        }

        public override void CropRenderArea(BoundingRectangle region)
        {
            ActiveState.SetScissor(region);
        }
        public override void SetRenderArea(BoundingRectangle region)
        {
            ActiveState.SetViewport(region);
        }

        public override void ClearRenderArea()
        {
            ActiveState.ClearViewport();
        }

        public override bool SetIndexedViewportScissors(
            ReadOnlySpan<BoundingRectangle> viewports,
            ReadOnlySpan<BoundingRectangle> scissors)
        {
            int count = Math.Min(viewports.Length, scissors.Length);
            if (count <= 0 ||
                !RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray ||
                count > RuntimeEngine.Rendering.State.MaxOpenGLViewports)
                return false;

            ActiveState.SetIndexedViewportScissors(viewports[..count], scissors[..count]);
            return true;
        }

        public override void ClearIndexedViewportScissors(int count)
        {
            if (count <= 0)
                return;

            ActiveState.ClearIndexedViewportScissors();
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
        private int _windowRenderCallbackInProgress;
        private long _swapchainRecreateRequestedAt;
        private long _swapchainResizeLastChangedAt;
        private uint _pendingSurfaceWidth;
        private uint _pendingSurfaceHeight;
        private long _lastFrameCompletedTimestamp;
        private long _lastInteractiveSwapchainRecreateTimestamp;
        private int _consecutiveNotReadyCount;
        private long _resourceCatchUpStartedAt;
        private ulong _resourceCatchUpBlockedFrames;
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

        private Result SubmitAcquireSemaphoreBridge(Semaphore acquireSemaphore, ulong signalTimelineValue)
            => SubmitAcquireSemaphoreBridge(acquireSemaphore, signalTimelineValue, default, null, 0);

        private Result SubmitAcquireSemaphoreBridge(
            Semaphore acquireSemaphore,
            ulong signalTimelineValue,
            Semaphore signalPresentSemaphore,
            CommandBuffer* commandBuffers,
            uint commandBufferCount)
        {
            uint signalSemaphoreCount = signalPresentSemaphore.Handle != 0 ? 2u : 1u;
            ulong* signalValues = stackalloc ulong[2] { signalTimelineValue, 0UL };
            ulong* waitValues = stackalloc ulong[1] { 0UL };
            Semaphore* waitSemaphores = stackalloc Semaphore[1] { acquireSemaphore };
            Semaphore* signalSemaphores = stackalloc Semaphore[2] { _graphicsTimelineSemaphore, signalPresentSemaphore };
            PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[1] { PipelineStageFlags.TopOfPipeBit };

            TimelineSemaphoreSubmitInfo timelineInfo = new()
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = 1,
                PWaitSemaphoreValues = waitValues,
                SignalSemaphoreValueCount = signalSemaphoreCount,
                PSignalSemaphoreValues = signalValues,
            };

            SubmitInfo submit = new()
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = commandBufferCount,
                PCommandBuffers = commandBufferCount == 0 ? null : commandBuffers,
                SignalSemaphoreCount = signalSemaphoreCount,
                PSignalSemaphores = signalSemaphores,
            };

            return SubmitToQueueTracked(graphicsQueue, ref submit, default);
        }

        private void DrainSkippedResizeFrameOps(string reason)
        {
            FrameOp[] droppedOps = DrainFrameOps(out _);
            FailUnsubmittedSubmissionMarkers(droppedOps);
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

        private static void RecordOverlayFrameOutput(
            EFrameOutputKind outputKind,
            string name,
            bool rendered,
            int commandCount,
            long elapsedTicks)
        {
            double cpuMs = elapsedTicks <= 0L ? 0.0 : elapsedTicks * 1000.0 / Stopwatch.Frequency;
            IRuntimeRenderingHostServices services = RuntimeRenderingHostServices.Current;
            EVrOutputViewKind viewKind = services.IsInVR && services.VrMirrorMode != EVrMirrorMode.FullIndependentRender
                ? EVrOutputViewKind.CyclopeanDesktop
                : EVrOutputViewKind.DesktopEditor;
            bool mirror = services.IsInVR &&
                viewKind == EVrOutputViewKind.CyclopeanDesktop &&
                services.VrMirrorMode is EVrMirrorMode.BlitSubmittedEye or EVrMirrorMode.CyclopeanReconstruct;
            var pacing = FrameOutputPacingDecision.Due(viewKind, outputKind, RuntimeEngine.Rendering.State.RenderFrameId);
            var telemetry = new FrameOutputTelemetry(
                outputKind,
                viewKind,
                EFrameOutputPhase.Overlay,
                pacing,
                name,
                string.Empty,
                true,
                rendered,
                false,
                mirror,
                false,
                viewKind == EVrOutputViewKind.CyclopeanDesktop && services.VrMirrorMode != EVrMirrorMode.FullIndependentRender,
                commandCount,
                0,
                0,
                0,
                cpuMs,
                0.0);
            services.RecordRenderFrameOutput(telemetry);
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

            DrainInvalidatedCommandBufferRecordings();
            DrainRetiredSwapchainGenerations();
            DrainRetiredDescriptorPools();
            DrainRetiredPipelines();
            DrainRetiredPipelineLayouts();
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
                    RecordResourceCatchUpProgress(viewport, instance.ActiveGeneration, instance.PendingGeneration, reason);
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
                    RecordResourceCatchUpProgress(viewport, activeGeneration, pendingGeneration, reason);
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

                if (pendingMatchesCurrent)
                {
                    reason =
                        $"VP[{viewport.Index}] swapchain extent converged; presentation remains paused while generation catches up. " +
                        $"Active={key} Pending={pendingGeneration!.Key}";
                    RecordResourceCatchUpProgress(viewport, activeGeneration, pendingGeneration, reason);
                    return true;
                }

                reason =
                    $"VP[{viewport.Index}] active={key.DisplayWidth}x{key.DisplayHeight}/{key.InternalWidth}x{key.InternalHeight} " +
                    $"current={displayWidth}x{displayHeight}/{internalWidth}x{internalHeight} pending={pendingGeneration?.Key.ToString() ?? "<none>"}";
                RecordResourceCatchUpProgress(viewport, activeGeneration, pendingGeneration, reason);
                return true;
            }

            _resourceCatchUpStartedAt = 0;
            _resourceCatchUpBlockedFrames = 0;
            return false;
        }

        private void RecordResourceCatchUpProgress(
            XRViewport viewport,
            RenderResourceGeneration? activeGeneration,
            RenderResourceGeneration? pendingGeneration,
            string reason)
        {
            long now = Stopwatch.GetTimestamp();
            if (_resourceCatchUpStartedAt == 0)
                _resourceCatchUpStartedAt = now;

            ulong blockedFrames = ++_resourceCatchUpBlockedFrames;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_resourceCatchUpStartedAt, now);
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.ResourceCatchUpProgress.{viewport.Index}",
                TimeSpan.FromMilliseconds(250),
                "[Vulkan][ResizeConvergence] Managed resources catching up after swapchain convergence. VP={0} BlockedFrames={1} ElapsedMs={2:F1} Swapchain={3}x{4} Active={5} Pending={6} PendingStatus={7} Reason={8}",
                viewport.Index,
                blockedFrames,
                elapsed.TotalMilliseconds,
                swapChainExtent.Width,
                swapChainExtent.Height,
                activeGeneration?.Key.ToString() ?? "<none>",
                pendingGeneration?.Key.ToString() ?? "<none>",
                pendingGeneration?.Status.ToString() ?? "<none>",
                reason);
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

        private bool CanPresentMismatchedSwapchainExtent(
            uint liveSurfaceWidth,
            uint liveSurfaceHeight,
            uint swapchainWidth,
            uint swapchainHeight)
        {
            _ = liveSurfaceWidth;
            _ = liveSurfaceHeight;
            _ = swapchainWidth;
            _ = swapchainHeight;

            // Keep the normal Windows Vulkan contract strict until
            // VkSwapchainPresentScalingCreateInfoKHR support is queried,
            // configured, and validated end-to-end for this backend.
            return false;
        }

        /// <summary>
        /// Renders a frame for the window, using the specified time delta since the last frame.
        /// </summary>
        /// <param name="delta">The time delta since the last frame, in seconds.</param>
        /// <exception cref="InvalidOperationException">Thrown if the Vulkan device is lost or the window render callback is reentrant.</exception>
        /// <exception cref="Exception">Thrown if an unexpected error occurs during the rendering of the frame.</exception>
        protected override void WindowRenderCallback(double delta)
        {
            if (Interlocked.CompareExchange(ref _windowRenderCallbackInProgress, 1, 0) != 0)
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.ReentrantWindowRenderSkipped",
                    TimeSpan.FromMilliseconds(250),
                    "[Vulkan] Skipping reentrant desktop window render callback while a previous desktop frame is still active. ActiveFrame={0} CurrentFrameSlot={1}",
                    _vkDebugFrameCounter,
                    currentFrame);
                return;
            }

            try
            {
                if (_deviceLost)
                    throw CreateDeviceLostException("RenderWindow", Result.ErrorDeviceLost);

                ulong frameNumber = ++_vkDebugFrameCounter;
                BeginDescriptorHeapFrame(frameNumber);

                long frameStartTimestamp = Stopwatch.GetTimestamp();

                // Log large gaps between render frames — helps identify CPU-side stalls that
                // could lead to stale GPU state or TDR timeouts.
                if (_lastFrameCompletedTimestamp != 0)
                {
                    TimeSpan gap = Stopwatch.GetElapsedTime(_lastFrameCompletedTimestamp, frameStartTimestamp);
                    if (gap > TimeSpan.FromSeconds(5))
                    {
                        Debug.VulkanWarning(
                            $"[Vulkan] Frame {frameNumber}: {gap.TotalSeconds:F1}s gap since last frame completed. " +
                            $"Slot={currentFrame} SlotTimelineValue={_frameSlotTimelineValues?[currentFrame]}");
                    }
                }

                TimeSpan waitFenceTime = TimeSpan.Zero;
                TimeSpan acquireImageTime = TimeSpan.Zero;
                TimeSpan recordCommandBufferTime = TimeSpan.Zero;
                TimeSpan snapshotImGuiOverlayTime = TimeSpan.Zero;
                TimeSpan recordSceneCommandBufferTime = TimeSpan.Zero;
                TimeSpan recordImGuiOverlayTime = TimeSpan.Zero;
                TimeSpan recordDynamicUiTextOverlayTime = TimeSpan.Zero;
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
                    bool canPresentMismatchedSwapchainExtent = liveSurfaceValid &&
                        !surfaceMatchesSwapchain &&
                        CanPresentMismatchedSwapchainExtent(
                            liveSurfaceWidth,
                            liveSurfaceHeight,
                            swapChainExtent.Width,
                            swapChainExtent.Height);

                    if (liveSurfaceValid && !surfaceMatchesSwapchain)
                    {
                        if (interactiveResize)
                            ScheduleSwapchainRecreate("Interactive resize surface/swapchain size mismatch");
                        else
                            ScheduleSwapchainRecreate("Surface/swapchain size mismatch");

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

                    if (_frameBufferInvalidated || (!surfaceMatchesSwapchain && !canPresentMismatchedSwapchainExtent))
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

                    bool frameGenerationProxyRequired = _streamlineFrameGenerationProvisioned;
                    bool frameGenerationProxyIncludesDlss = frameGenerationProxyRequired
                        && _streamlineDlssProvisioned;
                    if (_streamlineFrameGenerationSwapchainActive != frameGenerationProxyRequired
                        || (_streamlineFrameGenerationSwapchainActive
                            && _streamlineFrameGenerationSwapchainIncludesDlss != frameGenerationProxyIncludesDlss))
                    {
                        RecreateSwapchainImmediately(
                            frameGenerationProxyRequired
                                ? frameGenerationProxyIncludesDlss
                                    ? "NVIDIA DLSS/DLSS-G capability provisioned; recreating Streamline swapchain with DLSS + DLSS-G"
                                    : "NVIDIA DLSS-G capability provisioned; recreating swapchain through Streamline"
                                : "NVIDIA DLSS-G capability unavailable; recreating swapchain without Streamline");
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
                        DrainInvalidatedCommandBufferRecordings();
                        DrainRetiredSwapchainGenerations();
                        DrainRetiredCommandBuffers(currentFrame);
                        DrainRetiredDescriptorSets(currentFrame);
                        DrainRetiredDescriptorPools();
                        DrainRetiredPipelines();
                        DrainRetiredPipelineLayouts();
                        DrainRetiredQueryPools(currentFrame);
                        DrainRetiredBufferViews(currentFrame);
                        DrainRetiredBuffers();
                        DrainRetiredFramebuffers();
                        DrainRetiredImages();
                        DrainCompletedRecordedTextureUploadPublications();
                    }
                    drainRetiredResourcesTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

                    // Helpful when tracking down DPI / resize issues.
                    if (VulkanFrameDiagnosticsTraceEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.Frame.{GetHashCode()}.Sizes",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Frame={0} WindowFB={1}x{2} Swapchain={3}x{4}",
                            frameNumber,
                            liveFramebufferSize.X,
                            liveFramebufferSize.Y,
                            swapChainExtent.Width,
                            swapChainExtent.Height);
                    }

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

                    if (VulkanFrameDiagnosticsTraceEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.Frame.{GetHashCode()}.Acquire",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Frame={0} InFlightSlot={1} AcquiredImage={2} LastPresented={3}",
                            frameNumber,
                            currentFrame,
                            imageIndex,
                            _lastPresentedImageIndex);
                    }

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

                    // 3. Serialize image reuse by the last graphics timeline value for this image.
                    // The acquired binary semaphore is consumed directly by the draw submit below,
                    // which avoids a zero-command-buffer QueueSubmit on every desktop frame.
                    _acquireTimelineValue = _graphicsTimelineValue;
                    bool acquireSemaphoreConsumed = false;
                    Semaphore presentSemaphore = presentBridgeSemaphores![imageIndex];
                    CommandBuffer textureUploadCommandBuffer = default;
                    CommandPool textureUploadCommandPool = default;
                    bool textureUploadCommandBufferSubmitted = false;

                    void ReleaseUnsubmittedTextureUploadCommandBuffer(string reason)
                    {
                        CancelRecordedTextureUploadSubmitBatch(reason);

                        if (textureUploadCommandBuffer.Handle == 0 || textureUploadCommandBufferSubmitted)
                            return;

                        CommandBuffer uploadCommandBuffer = textureUploadCommandBuffer;
                        if (textureUploadCommandPool.Handle != 0 && !_deviceLost)
                            FreeVulkanCommandBufferTracked(textureUploadCommandPool, ref uploadCommandBuffer, "FrameLoop.UploadAbort");

                        RemoveCommandBufferBindState(textureUploadCommandBuffer);
                        textureUploadCommandBuffer = default;
                        textureUploadCommandPool = default;
                    }

                    void ConsumeAcquireSemaphoreForAbortedFrame(string reason)
                    {
                        if (acquireSemaphoreConsumed || acquireSemaphore.Handle == 0 || _deviceLost)
                            return;

                        ulong abortBridgeSignalValue = Math.Max(_graphicsTimelineValue + 1, _acquireTimelineValue + 1);
                        long abortBridgeStartTimestamp = Stopwatch.GetTimestamp();
                        Result abortBridgeResult;
                        using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.AcquireAbortBridgeSubmit"))
                        {
                            abortBridgeResult = SubmitAcquireSemaphoreBridge(acquireSemaphore, abortBridgeSignalValue);
                        }
                        acquireBridgeSubmitTime += Stopwatch.GetElapsedTime(abortBridgeStartTimestamp);

                        if (abortBridgeResult == Result.Success)
                        {
                            _graphicsTimelineValue = Math.Max(_graphicsTimelineValue, abortBridgeSignalValue);
                            acquireSemaphoreConsumed = true;
                            return;
                        }

                        Debug.VulkanWarningEvery(
                            $"Vulkan.Frame.{GetHashCode()}.AcquireAbortBridgeFailed.{reason}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Failed to consume acquired swapchain semaphore after aborted frame ({0}): {1}. Swapchain recreate will continue.",
                            reason,
                            abortBridgeResult);
                    }

                    int ResolveRecordedSwapchainWriteCount(CommandBuffer commandBuffer)
                    {
                        if (commandBuffer.Handle == 0 ||
                            _commandBufferVariants is null ||
                            imageIndex >= _commandBufferVariants.Length)
                        {
                            return 0;
                        }

                        var variants = _commandBufferVariants[imageIndex];
                        for (int i = 0; i < variants.Count; i++)
                        {
                            CommandBufferCacheVariant variant = variants[i];
                            if (variant.PrimaryCommandBuffer.Handle == commandBuffer.Handle)
                                return variant.RecordedSwapchainWriteCount;
                        }

                        return 0;
                    }

                    bool TryPresentAbortedDirtyFrame(
                        bool commandBufferDirtyFlagSet,
                        bool commandBuffersDirtiedAfterSceneRecord,
                        int recordedSwapchainWriteCount,
                        string rejectionStage,
                        Result? rejectedSubmitResult)
                    {
                        bool imageWasEverPresented = IsSwapchainImageEverPresented(imageIndex);
                        bool imageHasValidPresentedContent =
                            _swapchainImageHasValidPresentedContent is not null &&
                            imageIndex < _swapchainImageHasValidPresentedContent.Length &&
                            _swapchainImageHasValidPresentedContent[imageIndex];
                        uint lastCompletedImageBeforeRejection = _lastPresentedImageIndex;
                        bool acquireAvailable = !acquireSemaphoreConsumed && acquireSemaphore.Handle != 0;
                        RejectedDesktopFramePolicyDecision policy = ResolveRejectedDesktopFramePolicy(
                            acquireAvailable,
                            _deviceLost,
                            imageWasEverPresented,
                            imageHasValidPresentedContent);
                        if (!policy.ShouldPresent &&
                            acquireAvailable &&
                            !_deviceLost &&
                            string.Equals(rejectionStage, "RecordDeferred", StringComparison.Ordinal))
                        {
                            // Descriptor/program readiness is checked only after the desktop image
                            // has been acquired. When no prior completed contents exist yet, publish
                            // one explicit initialization clear so ownership returns to the
                            // presentation engine without rebuilding the swapchain. The scene output
                            // remains deferred and is rebuilt on the next frame.
                            policy = new RejectedDesktopFramePolicyDecision(
                                ERejectedDesktopFrameDisposition.PresentInitializationClear,
                                ERejectedDesktopFramePolicyReason.DeferredInitializationClear);
                        }
                        bool isPhase524bInjectedRejection = string.Equals(
                            rejectionStage,
                            Phase524bInjectedDesktopRejectionStage,
                            StringComparison.Ordinal);

                        FrameOpContext? exposureContext =
                            _lastWindowPresentFrameOpContext ?? ActiveLastActiveFrameOpContext;
                        EVulkanFrameOpContextKind exposureOwnerKind =
                            exposureContext?.ContextKind ?? EVulkanFrameOpContextKind.Unknown;
                        int exposureOwnerPipelineIdentity = exposureContext?.PipelineIdentity ?? 0;
                        bool exposureResourceRegistered =
                            exposureContext?.ResourceRegistry?.TextureRecords.ContainsKey(
                                DefaultRenderPipeline.AutoExposureTextureName) == true;
                        bool exposurePhysicalAllocated =
                            ResourceAllocator.TryGetPhysicalGroupForResource(
                                DefaultRenderPipeline.AutoExposureTextureName,
                                out VulkanPhysicalImageGroup? exposureGroup) &&
                            exposureGroup?.IsAllocated == true;
                        bool exposureOwnedByDesktop =
                            exposureOwnerKind == EVulkanFrameOpContextKind.MainViewport &&
                            exposureResourceRegistered;
                        ulong exposureImageHandle = exposureGroup?.Image.Handle ?? 0UL;
                        ImageLayout exposureLayout = exposureGroup?.LastKnownLayout ?? ImageLayout.Undefined;
                        string submitResultLabel = rejectedSubmitResult?.ToString() ?? "not-submitted";

                        if (acquireAvailable && !_deviceLost)
                            ReleaseUnsubmittedTextureUploadCommandBuffer("desktop frame rejected before submit");

                        if (!policy.ShouldPresent)
                        {
                            // Publishing an acquired-but-unwritten image can expose undefined or
                            // cleared contents. Returning false lets the caller consume/recreate the
                            // acquired image while the compositor keeps its last completed image.
                            Debug.VulkanWarningEvery(
                                $"Vulkan.Frame.{GetHashCode()}.RejectedDesktopFrame.{policy.Reason}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan][FrameFailure][RejectedDesktopFrame] policy=SkipPresent reason={0} frame={1} image={2} lastCompletedImageBeforeRejection={3} finalTargetValid={4} swapchainWrites={5} imageEverPresented={6} presentAttempted=false rejectionStage={7} submitResult={8} dirtyFlag={9} generationChanged={10} plannerRev={11} plan=0x{12:X16} allocation=0x{13:X16} lastReplacementRev={14} lastReplacementPlan=0x{15:X16} lastReplacementAllocation=0x{16:X16} retiredImages={17} retiredBuffers={18} exposureRegistered={19} exposurePhysicalAllocated={20} exposureOwnerKind={21} exposureOwnerPipeline={22} exposureOwnedByDesktop={23} exposureImage=0x{24:X} exposureLayout={25} exposureHistoryRetained={26}",
                                policy.Reason,
                                frameNumber,
                                imageIndex,
                                lastCompletedImageBeforeRejection,
                                imageHasValidPresentedContent,
                                recordedSwapchainWriteCount,
                                imageWasEverPresented,
                                rejectionStage,
                                submitResultLabel,
                                commandBufferDirtyFlagSet,
                                commandBuffersDirtiedAfterSceneRecord,
                                ResourcePlannerRevision,
                                ActiveResourcePlannerSignature,
                                ActiveResourceAllocationSignature,
                                _lastResourcePlanReplacementRevision,
                                _lastResourcePlanReplacementSignature,
                                _lastResourcePlanReplacementAllocationSignature,
                                _lastResourcePlanReplacementRetiredImageCount,
                                _lastResourcePlanReplacementRetiredBufferCount,
                                exposureResourceRegistered,
                                exposurePhysicalAllocated,
                                exposureOwnerKind,
                                exposureOwnerPipelineIdentity,
                                exposureOwnedByDesktop,
                                exposureImageHandle,
                                exposureLayout,
                                _retainedAutoExposureHistoryGroup is not null);
                            if (isPhase524bInjectedRejection && exposureContext.HasValue)
                            {
                                RecordPhase524bInjectedDesktopRejection(
                                    exposureContext.Value,
                                    in policy,
                                    presentAccepted: false,
                                    renderFrameId: frameNumber);
                            }
                            return false;
                        }

                        int clearedLayoutCount = ClearAllTrackedImageLayouts();
                        CommandPool abortCommandPool = default;
                        CommandBuffer abortCommandBuffer = default;
                        uint abortCommandBufferCount = 0;

                        try
                        {
                            abortCommandPool = GetThreadCommandPool();
                            abortCommandBuffer = AllocateCommandBuffer(
                                CommandBufferLevel.Primary,
                                "swapchain abort present transition command buffer",
                                abortCommandPool);
                            RegisterCommandBufferImageIndex(abortCommandBuffer, imageIndex);

                            CommandBufferBeginInfo beginInfo = new()
                            {
                                SType = StructureType.CommandBufferBeginInfo,
                                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                            };

                            if (Api!.BeginCommandBuffer(abortCommandBuffer, ref beginInfo) != Result.Success)
                                throw new Exception("Failed to begin swapchain abort-present transition command buffer.");

                            ResetCommandBufferBindState(abortCommandBuffer);

                            if (policy.ShouldClearBeforePresent)
                            {
                                Image swapchainImage = swapChainImages![imageIndex];
                                ImageSubresourceRange clearRange = new()
                                {
                                    AspectMask = ImageAspectFlags.ColorBit,
                                    BaseMipLevel = 0,
                                    LevelCount = 1,
                                    BaseArrayLayer = 0,
                                    LayerCount = 1,
                                };
                                ImageMemoryBarrier toTransfer = new()
                                {
                                    SType = StructureType.ImageMemoryBarrier,
                                    SrcAccessMask = 0,
                                    DstAccessMask = AccessFlags.TransferWriteBit,
                                    OldLayout = imageWasEverPresented
                                        ? ImageLayout.PresentSrcKhr
                                        : ImageLayout.Undefined,
                                    NewLayout = ImageLayout.TransferDstOptimal,
                                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                                    Image = swapchainImage,
                                    SubresourceRange = clearRange,
                                };
                                CmdPipelineBarrierTracked(
                                    abortCommandBuffer,
                                    PipelineStageFlags.AllCommandsBit,
                                    PipelineStageFlags.TransferBit,
                                    0,
                                    0,
                                    null,
                                    0,
                                    null,
                                    1,
                                    &toTransfer);

                                ClearColorValue clearColor = new(0.0f, 0.0f, 0.0f, 1.0f);
                                CmdClearColorImageTracked(
                                    abortCommandBuffer,
                                    swapchainImage,
                                    ImageLayout.TransferDstOptimal,
                                    ref clearColor,
                                    1,
                                    ref clearRange);

                                ImageMemoryBarrier toPresent = new()
                                {
                                    SType = StructureType.ImageMemoryBarrier,
                                    SrcAccessMask = AccessFlags.TransferWriteBit,
                                    DstAccessMask = 0,
                                    OldLayout = ImageLayout.TransferDstOptimal,
                                    NewLayout = ImageLayout.PresentSrcKhr,
                                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                                    Image = swapchainImage,
                                    SubresourceRange = clearRange,
                                };
                                CmdPipelineBarrierTracked(
                                    abortCommandBuffer,
                                    PipelineStageFlags.TransferBit,
                                    PipelineStageFlags.BottomOfPipeBit,
                                    0,
                                    0,
                                    null,
                                    0,
                                    null,
                                    1,
                                    &toPresent);
                            }

                            if (EndCommandBufferTracked(abortCommandBuffer, cacheVariant: false) != Result.Success)
                                throw new Exception("Failed to end swapchain abort-present transition command buffer.");

                            abortCommandBufferCount = 1;

                            CommandBuffer submittedAbortCommandBuffer = abortCommandBuffer;
                            CommandBuffer* abortCommandBuffers = null;
                            if (abortCommandBufferCount != 0)
                                abortCommandBuffers = &submittedAbortCommandBuffer;

                            ulong abortSignalValue = Math.Max(_graphicsTimelineValue + 1, _acquireTimelineValue + 1);
                            long abortBridgeStartTimestamp = Stopwatch.GetTimestamp();
                            Result abortBridgeResult;
                            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.DirtyAbortPresentSubmit"))
                            {
                                abortBridgeResult = SubmitAcquireSemaphoreBridge(
                                    acquireSemaphore,
                                    abortSignalValue,
                                    presentSemaphore,
                                    abortCommandBuffers,
                                    abortCommandBufferCount);
                            }
                            acquireBridgeSubmitTime += Stopwatch.GetElapsedTime(abortBridgeStartTimestamp);

                            if (abortBridgeResult != Result.Success)
                            {
                                if (abortBridgeResult == Result.ErrorDeviceLost)
                                    throw CreateDeviceLostException("Dirty abort QueueSubmit", abortBridgeResult);

                                if (abortCommandBuffer.Handle != 0)
                                {
                                    FreeVulkanCommandBufferTracked(abortCommandPool, ref abortCommandBuffer, "FrameLoop.AbortBridge");
                                    RemoveCommandBufferBindState(abortCommandBuffer);
                                }

                                Debug.VulkanWarningEvery(
                                    $"Vulkan.Frame.{GetHashCode()}.DirtyAbortPresentSubmitFailed",
                                    TimeSpan.FromSeconds(1),
                                    "[Vulkan] Failed to submit skipped-frame present for dirtied command buffer on image {0}: {1}.",
                                    imageIndex,
                                    abortBridgeResult);
                                return false;
                            }

                            acquireSemaphoreConsumed = true;
                            _graphicsTimelineValue = Math.Max(_graphicsTimelineValue, abortSignalValue);
                            _frameSlotTimelineValues![currentFrame] = abortSignalValue;
                            if (_swapchainImageTimelineValues is not null && imageIndex < _swapchainImageTimelineValues.Length)
                                _swapchainImageTimelineValues[imageIndex] = abortSignalValue;

                            if (abortCommandBuffer.Handle != 0)
                                DeferSecondaryCommandBufferFree(imageIndex, abortCommandPool, abortCommandBuffer);

                            RuntimeRenderingHostServices.Current.MarkRenderFrameReadyForCollect(XRWindow);

                            Semaphore skippedFramePresentSemaphore = presentSemaphore;
                            uint skippedFrameImageIndex = imageIndex;
                            var swapChains = stackalloc[] { swapChain };
                            PresentInfoKHR presentInfo = new()
                            {
                                SType = StructureType.PresentInfoKhr,
                                WaitSemaphoreCount = 1,
                                PWaitSemaphores = &skippedFramePresentSemaphore,
                                SwapchainCount = 1,
                                PSwapchains = swapChains,
                                PImageIndices = &skippedFrameImageIndex
                            };

                            long presentStartTimestamp = Stopwatch.GetTimestamp();
                            using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.DirtyAbortQueuePresent"))
                            {
                                // A rejected frame has no current DLSS-G tags or constants. Disable
                                // generation for this fallback present; the next successfully recorded
                                // DLSS-G op re-enables it after publishing that frame's metadata.
                                DisableStreamlineFrameGenerationBeforeSwapchainMutation(
                                    $"presenting rejected Vulkan frame {frameNumber} ({rejectionStage})");
                                DrainStreamlineFrameGenerationDisableBeforePresent();
                                MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.PresentStart);
                                if (!TryPresentToQueueTracked(
                                    presentQueue,
                                    ref presentInfo,
                                    out result,
                                    out string failureReason))
                                {
                                    if (result == Result.ErrorDeviceLost)
                                        throw CreateDeviceLostException("Streamline dirty abort QueuePresent", result);

                                    string message = $"NVIDIA DLSS frame generation failed to present skipped frame through Streamline: {failureReason}";
                                    Debug.RenderingError(message);
                                    throw new InvalidOperationException(message);
                                }
                                MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.PresentEnd);
                            }
                            presentQueueTime += Stopwatch.GetElapsedTime(presentStartTimestamp);

                            bool skippedPresentAccepted = result == Result.Success || result == Result.SuboptimalKhr;
                            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPresentResult((int)result, skippedPresentAccepted);
                            if (skippedPresentAccepted)
                            {
                                _lastPresentedImageIndex = imageIndex;
                                if (_swapchainImageEverPresented is not null && imageIndex < _swapchainImageEverPresented.Length)
                                    _swapchainImageEverPresented[imageIndex] = true;
                            }

                            if (result == Result.ErrorDeviceLost)
                                throw CreateDeviceLostException("Dirty abort QueuePresent", result);
                            if (result == Result.ErrorOutOfDateKhr)
                                ScheduleSwapchainRecreate("Dirty abort QueuePresent returned ErrorOutOfDateKhr");
                            else if (result == Result.SuboptimalKhr)
                                ScheduleSwapchainRecreate("Dirty abort QueuePresent returned SuboptimalKhr");
                            else if (result == Result.ErrorSurfaceLostKhr)
                                RecreateSwapchainImmediately("Dirty abort QueuePresent returned ErrorSurfaceLostKhr");
                            else if (result != Result.Success)
                                throw new Exception($"Failed to present skipped swap chain image ({result}).");

                            Debug.VulkanWarningEvery(
                                $"Vulkan.Frame.{GetHashCode()}.DirtyBeforeSubmit",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan][FrameFailure][RejectedDesktopFrame] policy=PresentLastCompletedContent reason={0} frame={1} image={2} lastCompletedImageBeforeRejection={3} finalTargetValid={4} swapchainWrites={5} imageEverPresented={6} presentAttempted=true presentResult={7} rejectionStage={8} submitResult={9} dirtyFlag={10} generationChanged={11} clearedLayouts={12} plannerRev={13} plan=0x{14:X16} allocation=0x{15:X16} lastReplacementRev={16} lastReplacementPlan=0x{17:X16} lastReplacementAllocation=0x{18:X16} retiredImages={19} retiredBuffers={20} exposureRegistered={21} exposurePhysicalAllocated={22} exposureOwnerKind={23} exposureOwnerPipeline={24} exposureOwnedByDesktop={25} exposureImage=0x{26:X} exposureLayout={27} exposureHistoryRetained={28} presentAccepted={29}. Attempted presentation of previously completed content to release the acquired image.",
                                policy.Reason,
                                frameNumber,
                                imageIndex,
                                lastCompletedImageBeforeRejection,
                                imageHasValidPresentedContent,
                                recordedSwapchainWriteCount,
                                imageWasEverPresented,
                                result,
                                rejectionStage,
                                submitResultLabel,
                                commandBufferDirtyFlagSet,
                                commandBuffersDirtiedAfterSceneRecord,
                                clearedLayoutCount,
                                ResourcePlannerRevision,
                                ActiveResourcePlannerSignature,
                                ActiveResourceAllocationSignature,
                                _lastResourcePlanReplacementRevision,
                                _lastResourcePlanReplacementSignature,
                                _lastResourcePlanReplacementAllocationSignature,
                                _lastResourcePlanReplacementRetiredImageCount,
                                _lastResourcePlanReplacementRetiredBufferCount,
                                exposureResourceRegistered,
                                exposurePhysicalAllocated,
                                exposureOwnerKind,
                                exposureOwnerPipelineIdentity,
                                exposureOwnedByDesktop,
                                exposureImageHandle,
                                exposureLayout,
                                _retainedAutoExposureHistoryGroup is not null,
                                skippedPresentAccepted);

                            if (isPhase524bInjectedRejection && exposureContext.HasValue)
                            {
                                RecordPhase524bInjectedDesktopRejection(
                                    exposureContext.Value,
                                    in policy,
                                    presentAccepted: skippedPresentAccepted,
                                    renderFrameId: frameNumber);
                            }

                            currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
                            _lastFrameCompletedTimestamp = Stopwatch.GetTimestamp();
                            return true;
                        }
                        catch
                        {
                            if (abortCommandBuffer.Handle != 0 && abortCommandBufferCount == 0 && abortCommandPool.Handle != 0 && !_deviceLost)
                            {
                                FreeVulkanCommandBufferTracked(abortCommandPool, ref abortCommandBuffer, "FrameLoop.AbortPresent");
                                RemoveCommandBufferBindState(abortCommandBuffer);
                            }

                            throw;
                        }
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
                    snapshotImGuiOverlayTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

                    bool preserveSwapchainForImGuiOverlay = hasPendingImGuiOverlay && UseDynamicRenderingRenderTargets;

                    // 6. Record the scene command buffer. ImGui is recorded into a separate
                    // per-frame overlay buffer below so cached scene primaries do not freeze UI.
                    CommandBuffer submitCommandBuffer;
                    CommandBuffer dynamicUiBatchTextSecondaryCommandBuffer;
                    int dynamicUiBatchTextOverlayOpCount;
                    FrameOp[] dynamicUiBatchTextOverlayOps;
                    ulong dynamicUiBatchTextOverlaySignature;
                    CommandBufferCacheVariant? dynamicUiBatchTextOverlayVariant;
                    ImageLayout swapchainLayoutAfterScene;
                    long sceneCommandBufferDirtyGeneration;
                    int sceneSwapchainWriteCount;
                    stageStartTimestamp = Stopwatch.GetTimestamp();
                    using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.RecordCommandBuffer"))
                    {
                        long recordAllocationStart = GC.GetAllocatedBytesForCurrentThread();
                        string recordingDeferredReason = string.Empty;
                        try
                        {
                            submitCommandBuffer = EnsureCommandBufferRecorded(
                                imageIndex,
                                preserveSwapchainForImGuiOverlay,
                                out recordingDeferredReason,
                                out dynamicUiBatchTextSecondaryCommandBuffer,
                                out dynamicUiBatchTextOverlayOpCount,
                                out dynamicUiBatchTextOverlayOps,
                                out dynamicUiBatchTextOverlaySignature,
                                out dynamicUiBatchTextOverlayVariant,
                                out textureUploadCommandBuffer,
                                out textureUploadCommandPool,
                                out swapchainLayoutAfterScene,
                                out sceneCommandBufferDirtyGeneration);
                            sceneSwapchainWriteCount = ResolveRecordedSwapchainWriteCount(submitCommandBuffer);

                            if (!string.IsNullOrEmpty(recordingDeferredReason))
                            {
                                bool swapchainAttachmentRetired =
                                    IsSwapchainResourceRetirementRecordingFailure(
                                        recordingDeferredReason);
                                if (TryPresentAbortedDirtyFrame(
                                        commandBufferDirtyFlagSet: false,
                                        commandBuffersDirtiedAfterSceneRecord: true,
                                        recordedSwapchainWriteCount: sceneSwapchainWriteCount,
                                        rejectionStage: "RecordDeferred",
                                        rejectedSubmitResult: null))
                                {
                                    if (swapchainAttachmentRetired)
                                    {
                                        ScheduleSwapchainRecreate(
                                            "A generation-bound swapchain attachment retired during command recording");
                                    }
                                    return;
                                }

                                currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
                                ConsumeAcquireSemaphoreForAbortedFrame("RecordDeferred");

                                Debug.VulkanWarningEvery(
                                    $"Vulkan.Frame.{GetHashCode()}.RecordDeferred",
                                    TimeSpan.FromSeconds(1),
                                    "[Vulkan] Command buffer recording deferred before vkBeginCommandBuffer; retrying the output on its next frame. {0}",
                                    recordingDeferredReason);

                                ScheduleSwapchainRecreate(
                                    "Deferred-recording fallback could not return acquired image ownership");
                                return;
                            }
                        }
                        catch (InvalidOperationException recordEx) when (IsTransientResourceRetirementRecordingFailure(recordEx))
                        {
                            ReleaseUnsubmittedTextureUploadCommandBuffer("command buffer resource generation retired during recording");
                            MarkCommandBuffersDirty("command buffer resource generation retired during recording");

                            if (TryPresentAbortedDirtyFrame(
                                    commandBufferDirtyFlagSet: true,
                                    commandBuffersDirtiedAfterSceneRecord: true,
                                    recordedSwapchainWriteCount: 0,
                                    rejectionStage: "RecordResourceRetired",
                                    rejectedSubmitResult: null))
                                return;

                            currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
                            ConsumeAcquireSemaphoreForAbortedFrame("RecordResourceRetired");
                            ScheduleSwapchainRecreate(
                                "Retired-resource recording fallback could not return acquired image ownership");
                            return;
                        }
                        catch (Exception recordEx)
                        {
                        // Recording failed (e.g. OOM during resource allocation). The acquire bridge
                        // already consumed the binary semaphore and advanced the timeline, but we have
                        // no valid command buffer to submit. Advance currentFrame so the next attempt
                        // uses the other in-flight slot, and schedule a swapchain recreate which calls
                        // DeviceWaitIdle + destroys/recreates all swapchain objects — this returns the
                        // acquired image to the presentation engine and resets semaphore state.
                            currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;

                            ReleaseUnsubmittedTextureUploadCommandBuffer("command buffer recording failed");
                            ConsumeAcquireSemaphoreForAbortedFrame("RecordFailed");

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
                            TimeSpan sceneRecordElapsed = Stopwatch.GetElapsedTime(stageStartTimestamp);
                            recordSceneCommandBufferTime += sceneRecordElapsed;
                            recordCommandBufferTime += sceneRecordElapsed;
                            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - recordAllocationStart;
                            if (_lastEnsureCommandBufferRecordedPrimary)
                                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRecordCommandBufferAllocation(allocatedBytes);
                        }
                    }
                    bool scenePrimaryRecordedThisFrame = _lastEnsureCommandBufferRecordedPrimary;
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
                                    submitCommandBuffer,
                                    out imguiOverlayCommandBuffer);
                            if (preserveSwapchainForImGuiOverlay && !hasImGuiOverlayCommandBuffer)
                                throw new InvalidOperationException("Scene primary preserved the swapchain for ImGui, but the overlay command buffer was not recorded.");
                        }
                        catch (Exception overlayEx)
                        {
                            TimeSpan failedOverlayElapsed = Stopwatch.GetElapsedTime(stageStartTimestamp);
                            recordImGuiOverlayTime += failedOverlayElapsed;
                            recordCommandBufferTime += failedOverlayElapsed;
                            currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;

                            ReleaseUnsubmittedTextureUploadCommandBuffer("ImGui overlay command buffer recording failed");
                            ConsumeAcquireSemaphoreForAbortedFrame("RecordImGuiOverlayFailed");

                            Debug.VulkanWarningEvery(
                                $"Vulkan.Frame.{GetHashCode()}.RecordImGuiOverlayFailed",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] ImGui overlay command buffer recording failed. Scheduling swapchain recreate to recover. {0}",
                                overlayEx.Message);

                            RecreateSwapchainImmediately("ImGui overlay command buffer recording failed - recovering timeline/present state");
                            throw;
                        }
                    }
                    long imguiOverlayElapsedTicks = Stopwatch.GetTimestamp() - stageStartTimestamp;
                    recordImGuiOverlayTime += TimeSpan.FromTicks(
                        imguiOverlayElapsedTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
                    recordCommandBufferTime += recordImGuiOverlayTime;
                    RecordOverlayFrameOutput(
                        EFrameOutputKind.ImGuiOverlay,
                        "Vulkan ImGui overlay command buffer",
                        hasImGuiOverlayCommandBuffer,
                        hasImGuiOverlayCommandBuffer ? 1 : 0,
                        imguiOverlayElapsedTicks);

                    if (dynamicUiBatchTextOverlayOpCount > 0)
                    {
                        if (VulkanFrameDiagnosticsTraceEnabled)
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
                                    imguiOverlayCommandBuffer,
                                    dynamicUiBatchTextOverlayVariant,
                                    dynamicUiBatchTextOverlayOps,
                                    dynamicUiBatchTextOverlaySignature,
                                    out dynamicUiBatchTextOverlayCommandBuffer);
                            }
                            catch (Exception overlayEx)
                            {
                                TimeSpan failedOverlayElapsed = Stopwatch.GetElapsedTime(stageStartTimestamp);
                                recordDynamicUiTextOverlayTime += failedOverlayElapsed;
                                recordCommandBufferTime += failedOverlayElapsed;
                                currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;

                                ReleaseUnsubmittedTextureUploadCommandBuffer("dynamic UI text overlay command buffer recording failed");
                                ConsumeAcquireSemaphoreForAbortedFrame("RecordDynamicUiTextOverlayFailed");

                                Debug.VulkanWarningEvery(
                                    $"Vulkan.Frame.{GetHashCode()}.RecordDynamicUiTextOverlayFailed",
                                    TimeSpan.FromSeconds(1),
                                    "[Vulkan] Dynamic UI text overlay command buffer recording failed. Scheduling swapchain recreate to recover. {0}",
                                    overlayEx.Message);

                                RecreateSwapchainImmediately("Dynamic UI text overlay command buffer recording failed - recovering timeline/present state");
                                throw;
                            }
                        }
                        long dynamicTextOverlayElapsedTicks = Stopwatch.GetTimestamp() - stageStartTimestamp;
                        recordDynamicUiTextOverlayTime += TimeSpan.FromTicks(
                            dynamicTextOverlayElapsedTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
                        recordCommandBufferTime += recordDynamicUiTextOverlayTime;
                        RecordOverlayFrameOutput(
                            EFrameOutputKind.DynamicTextOverlay,
                            "Vulkan dynamic text overlay command buffer",
                            hasDynamicUiBatchTextOverlayCommandBuffer,
                            hasDynamicUiBatchTextOverlayCommandBuffer ? 1 : 0,
                            dynamicTextOverlayElapsedTicks);
                    }

                    FrameOpContext? phase524bDesktopContext =
                        _lastWindowPresentFrameOpContext ?? ActiveLastActiveFrameOpContext;
                    if (phase524bDesktopContext.HasValue &&
                        TryPreparePhase524bInjectedDesktopRejection(
                            phase524bDesktopContext.Value,
                            imageIndex))
                    {
                        if (TryPresentAbortedDirtyFrame(
                                commandBufferDirtyFlagSet: false,
                                commandBuffersDirtiedAfterSceneRecord: false,
                                recordedSwapchainWriteCount: sceneSwapchainWriteCount,
                                rejectionStage: Phase524bInjectedDesktopRejectionStage,
                                rejectedSubmitResult: null))
                        {
                            return;
                        }

                        throw new InvalidOperationException(
                            "The controlled Phase 5.2.4b desktop rejection could not apply its last-completed-image policy.");
                    }

                    bool commandBufferDirtyFlagSet =
                        _commandBufferDirtyFlags is not null &&
                        imageIndex < (uint)_commandBufferDirtyFlags.Length &&
                        _commandBufferDirtyFlags[imageIndex];
                    bool commandBuffersDirtiedAfterSceneRecord =
                        HaveCommandBuffersDirtiedSince(sceneCommandBufferDirtyGeneration);
                    if (scenePrimaryRecordedThisFrame && commandBufferDirtyFlagSet && !commandBuffersDirtiedAfterSceneRecord)
                    {
                        if (_commandBufferDirtyFlags is not null && imageIndex < (uint)_commandBufferDirtyFlags.Length)
                            _commandBufferDirtyFlags[imageIndex] = false;

                        Debug.VulkanEvery(
                            $"Vulkan.Frame.{GetHashCode()}.FreshPrimaryDirtiedBeforeSubmit",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Continuing with freshly recorded command buffer for image {0} after clearing its pre-existing dirty flag. Cached reuse remains disabled for the affected variant. flag={1} generationChanged={2}",
                            imageIndex,
                            commandBufferDirtyFlagSet,
                            commandBuffersDirtiedAfterSceneRecord);
                    }
                    else if (commandBufferDirtyFlagSet || commandBuffersDirtiedAfterSceneRecord)
                    {
                        if (TryPresentAbortedDirtyFrame(
                                commandBufferDirtyFlagSet,
                                commandBuffersDirtiedAfterSceneRecord,
                                recordedSwapchainWriteCount: sceneSwapchainWriteCount,
                                rejectionStage: "CommandBufferDirtiedBeforeSubmit",
                                rejectedSubmitResult: null))
                            return;

                        Debug.VulkanWarningEvery(
                            $"Vulkan.Frame.{GetHashCode()}.DirtyBeforeSubmitFallback",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Command buffer for image {0} was dirtied after recording and before submit, and skipped-frame present failed. Recreating swapchain to recover. flag={1} generationChanged={2}",
                            imageIndex,
                            commandBufferDirtyFlagSet,
                            commandBuffersDirtiedAfterSceneRecord);

                        RecreateSwapchainImmediately("Command buffer dirtied before submit - recovering timeline/present state");
                        return;
                    }

                    // 5. Submit the command buffer with timeline sync.
                    _graphicsTimelineValue = Math.Max(_graphicsTimelineValue + 1, _acquireTimelineValue + 1);
                    ulong graphicsSignalValue = _graphicsTimelineValue;

                    ulong* waitTimelineValues = stackalloc ulong[1] { 0UL };
                    ulong* signalTimelineValues = stackalloc ulong[2] { graphicsSignalValue, 0UL };
                    Semaphore* waitSemaphores = stackalloc Semaphore[1] { acquireSemaphore };
                    var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };
                    CommandBuffer* submitCommandBuffers = stackalloc CommandBuffer[4];
                    uint submitCommandBufferCount = 0;
                    if (textureUploadCommandBuffer.Handle != 0)
                        submitCommandBuffers[submitCommandBufferCount++] = textureUploadCommandBuffer;
                    submitCommandBuffers[submitCommandBufferCount++] = submitCommandBuffer;
                    if (hasImGuiOverlayCommandBuffer && imguiOverlayCommandBuffer.Handle != 0)
                        submitCommandBuffers[submitCommandBufferCount++] = imguiOverlayCommandBuffer;
                    if (hasDynamicUiBatchTextOverlayCommandBuffer && dynamicUiBatchTextOverlayCommandBuffer.Handle != 0)
                        submitCommandBuffers[submitCommandBufferCount++] = dynamicUiBatchTextOverlayCommandBuffer;
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
                        using VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.Submission);
                        MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.RenderSubmitStart);
                        lock (_oneTimeSubmitLock)
                        {
                            ulong frameOpsSignature =
                                _commandBufferFrameOpSignatures is not null &&
                                imageIndex < (uint)_commandBufferFrameOpSignatures.Length
                                    ? _commandBufferFrameOpSignatures[imageIndex]
                                    : 0UL;
                            _ = TryGetCommandBufferDiagnosticMetadata(
                                imageIndex,
                                submitCommandBuffer,
                                out ulong plannerRevision,
                                out ulong frameOpContextId,
                                out ulong resourceGeneration,
                                out ulong descriptorGeneration);
                            VulkanSubmissionDiagnosticContext diagnosticContext =
                                CreateSwapchainSubmissionDiagnosticContext(
                                    "SwapchainDraw",
                                    imageIndex,
                                    frameNumber,
                                    0UL,
                                    graphicsSignalValue,
                                    sceneCommandBufferDirtyGeneration,
                                    frameOpsSignature,
                                    plannerRevision,
                                    frameOpContextId,
                                    resourceGeneration,
                                    descriptorGeneration);
                            submitResult = SubmitToQueueTracked(graphicsQueue, ref submitInfo, default, diagnosticContext);
                        }
                        MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.RenderSubmitEnd);
                    }

                    if (submitResult != Result.Success)
                    {
                        if (submitResult != Result.ErrorDeviceLost)
                        {
                            ReleaseUnsubmittedTextureUploadCommandBuffer($"graphics frame submit failed with {submitResult}");
                            MarkCommandBuffersDirty($"graphics frame submit rejected with {submitResult}");
                            if (TryPresentAbortedDirtyFrame(
                                    commandBufferDirtyFlagSet: true,
                                    commandBuffersDirtiedAfterSceneRecord: true,
                                    recordedSwapchainWriteCount: sceneSwapchainWriteCount,
                                    rejectionStage: "DrawSubmitRejected",
                                    rejectedSubmitResult: submitResult))
                                return;
                        }

                        if (submitResult == Result.ErrorDeviceLost)
                            throw CreateDeviceLostException("Draw QueueSubmit", submitResult);

                        ConsumeAcquireSemaphoreForAbortedFrame($"DrawSubmitFailed:{submitResult}");
                        RecreateSwapchainImmediately($"Draw submit failed with {submitResult} - recovering acquired image state");
                        throw new Exception($"Failed to submit draw command buffer ({submitResult}).");
                    }
                    acquireSemaphoreConsumed = true;
                    submitQueueTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
                    MarkFrameTimingSubmitted(unchecked((int)Math.Min(imageIndex, int.MaxValue)));

                    _frameSlotTimelineValues[currentFrame] = graphicsSignalValue;
                    if (_swapchainImageTimelineValues is not null && imageIndex < _swapchainImageTimelineValues.Length)
                        _swapchainImageTimelineValues[imageIndex] = graphicsSignalValue;
                    QueueRecordedTextureUploadsForTimeline(graphicsSignalValue, "graphics frame");
                    if (textureUploadCommandBuffer.Handle != 0)
                    {
                        textureUploadCommandBufferSubmitted = true;
                        DeferSecondaryCommandBufferFree(imageIndex, textureUploadCommandPool, textureUploadCommandBuffer);
                        textureUploadCommandBuffer = default;
                        textureUploadCommandPool = default;
                    }

                    // QueuePresent can block on desktop swapchain pacing. The submitted commands have
                    // consumed this frame's render buffers, so release CollectVisible before that wait.
                    RuntimeRenderingHostServices.Current.MarkRenderFrameReadyForCollect(XRWindow);

                    // Trim idle staging buffers so the pool does not grow unbounded.
                    stageStartTimestamp = Stopwatch.GetTimestamp();
                    using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.TrimStaging"))
                    {
                        _stagingManager.Trim(this);
                    }
                    trimStagingTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

                    if (VulkanFrameDiagnosticsTraceEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.Frame.{GetHashCode()}.Submit",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Frame={0} SubmittedImage={1}",
                            frameNumber,
                            imageIndex);
                    }

                    // 6. Present the image
                    Semaphore queuedPresentSemaphore = presentSemaphore;
                    uint queuedImageIndex = imageIndex;
                    var swapChains = stackalloc[] { swapChain };
                    PresentInfoKHR presentInfo = new()
                    {
                        SType = StructureType.PresentInfoKhr,
                        WaitSemaphoreCount = 1,
                        PWaitSemaphores = &queuedPresentSemaphore,
                        SwapchainCount = 1,
                        PSwapchains = swapChains,
                        PImageIndices = &queuedImageIndex
                    };

                    stageStartTimestamp = Stopwatch.GetTimestamp();
                    using (RuntimeRenderingHostServices.Current.StartProfileScope("Vulkan.FrameLifecycle.QueuePresent"))
                    {
                        DrainStreamlineFrameGenerationDisableBeforePresent();
                        MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.PresentStart);
                        if (!TryPresentToQueueTracked(
                            presentQueue,
                            ref presentInfo,
                            out result,
                            out string failureReason))
                        {
                            if (result == Result.ErrorDeviceLost)
                                throw CreateDeviceLostException("Streamline QueuePresent", result);

                            string message = $"NVIDIA DLSS frame generation failed to present through Streamline: {failureReason}";
                            Debug.RenderingError(message);
                            throw new InvalidOperationException(message);
                        }
                        MarkDlssFrameGenerationPclMarker(NvidiaDlssManager.Native.StreamlinePclMarker.PresentEnd);
                    }
                    presentQueueTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
                    bool presentAccepted = result == Result.Success || result == Result.SuboptimalKhr;
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPresentResult((int)result, presentAccepted);
                    if (presentAccepted)
                    {
                        _lastPresentedImageIndex = imageIndex;

                        // Track presentation layout separately from known-good contents. Rejected-
                        // frame recovery may re-present only an image with a completed prior write,
                        // never an uninitialized or clear-only recovery image.
                        if (_swapchainImageEverPresented is not null && imageIndex < _swapchainImageEverPresented.Length)
                            _swapchainImageEverPresented[imageIndex] = true;
                        if (_swapchainImageHasValidPresentedContent is not null && imageIndex < _swapchainImageHasValidPresentedContent.Length)
                        {
                            bool submittedFrameWroteSwapchain =
                                sceneSwapchainWriteCount > 0 ||
                                hasImGuiOverlayCommandBuffer ||
                                hasDynamicUiBatchTextOverlayCommandBuffer;
                            if (submittedFrameWroteSwapchain)
                            {
                                _swapchainImageHasValidPresentedContent[imageIndex] = true;
                            }
                            else if (!_swapchainImageHasValidPresentedContent[imageIndex])
                            {
                                Debug.VulkanWarningEvery(
                                    $"Vulkan.Frame.{GetHashCode()}.PresentedWithoutValidFinalWrite",
                                    TimeSpan.FromSeconds(1),
                                    "[Vulkan][FrameFailure] Presented swapchain image {0} without a recorded final write or valid prior contents. plannerRev={1} plan=0x{2:X16} allocation=0x{3:X16} sceneWrites={4} imgui={5} dynamicUi={6}",
                                    imageIndex,
                                    ResourcePlannerRevision,
                                    ActiveResourcePlannerSignature,
                                    ActiveResourceAllocationSignature,
                                    sceneSwapchainWriteCount,
                                    hasImGuiOverlayCommandBuffer,
                                    hasDynamicUiBatchTextOverlayCommandBuffer);
                            }
                        }
                    }

                    if (VulkanFrameDiagnosticsTraceEnabled)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.Frame.{GetHashCode()}.Present",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Frame={0} PresentedImage={1} Result={2}",
                            frameNumber,
                            imageIndex,
                            result);
                    }

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
                        resetDynamicUniformRingTime,
                        snapshotImGuiOverlayTime,
                        recordSceneCommandBufferTime,
                        recordImGuiOverlayTime,
                        recordDynamicUiTextOverlayTime);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _windowRenderCallbackInProgress, 0);
            }
        }

        private static bool IsTransientResourceRetirementRecordingFailure(InvalidOperationException exception)
            => IsTransientResourceRetirementRecordingFailure(exception.Message);

        private static bool IsTransientResourceRetirementRecordingFailure(string failureReason)
            => failureReason.Contains(
                "attempted to record retired Vulkan resource",
                StringComparison.Ordinal);

        private static bool IsSwapchainResourceRetirementRecordingFailure(string failureReason)
            => IsTransientResourceRetirementRecordingFailure(failureReason) &&
               (failureReason.Contains("Swapchain.Color", StringComparison.Ordinal) ||
                failureReason.Contains("Swapchain.Depth", StringComparison.Ordinal));
    }
}
