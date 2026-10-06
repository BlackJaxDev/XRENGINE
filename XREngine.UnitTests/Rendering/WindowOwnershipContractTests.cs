using System;
using System.IO;
using NUnit.Framework;
using Silk.NET.Maths;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class WindowOwnershipContractTests
{
    [Test]
    public void WindowPumpHost_VoidWindowTasksPostWithoutBlockingCaller()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/RuntimeWindowPumpHost.cs");
        int enqueueStart = source.IndexOf("public void EnqueueWindowTask", StringComparison.Ordinal);
        enqueueStart.ShouldBeGreaterThanOrEqualTo(0);
        int invokeStart = source.IndexOf("public T InvokeWindowTask", StringComparison.Ordinal);
        invokeStart.ShouldBeGreaterThan(enqueueStart);

        string enqueueBody = source[enqueueStart..invokeStart];

        enqueueBody.ShouldContain("Post(task, reason);");
        enqueueBody.ShouldNotContain("_blockingWaitCount");
    }

    [Test]
    public void DesktopWindowBackend_PublishesThreadOwnedKeyMouseTextAndScrollEvents()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/DesktopSilkWindowBackend.cs");
        string ownership = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/WindowOwnership/RuntimeWindowOwnership.cs");

        source.ShouldContain("keyboard.KeyDown += OnKeyDown;");
        source.ShouldContain("keyboard.KeyUp += OnKeyUp;");
        source.ShouldContain("keyboard.KeyChar += OnKeyChar;");
        source.ShouldContain("mouse.MouseDown += OnMouseDown;");
        source.ShouldContain("mouse.MouseUp += OnMouseUp;");
        source.ShouldContain("mouse.MouseMove += OnMouseMove;");
        source.ShouldContain("mouse.Scroll += OnScroll;");
        source.ShouldContain("_inputAccumulator.RecordPointerPosition");
        source.ShouldContain("_inputAccumulator.RecordScroll");
        ownership.ShouldContain("PointerDeltaX");
        ownership.ShouldContain("ScrollDeltaY");
        ownership.ShouldContain("TextInputCount");
    }

    [Test]
    public void CollapsedWindowHost_PumpsNativeEventsBeforeEnteringRenderDispatch()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs").Replace("\r\n", "\n");
        string host = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderThreadHost.cs").Replace("\r\n", "\n");

        int renderStart = source.IndexOf("private void RenderFrame()", StringComparison.Ordinal);
        renderStart.ShouldBeGreaterThanOrEqualTo(0);
        int consumeStart = source.IndexOf("ConsumeLatestWindowSurfaceSnapshotForRenderFrame", renderStart, StringComparison.Ordinal);
        consumeStart.ShouldBeGreaterThan(renderStart);
        string renderPumpBody = source[renderStart..consumeStart];

        renderPumpBody.ShouldNotContain("Window.DoEvents()");

        int pumpStart = source.IndexOf("public void PumpNativeWindowEventsFromHost()", StringComparison.Ordinal);
        pumpStart.ShouldBeGreaterThanOrEqualTo(0);
        int nextPumpMethod = source.IndexOf("private void ApplyVSyncModeOnRenderThread", pumpStart, StringComparison.Ordinal);
        nextPumpMethod.ShouldBeGreaterThan(pumpStart);
        string pumpBody = source[pumpStart..nextPumpMethod];
        pumpBody.ShouldContain("_desktopBackend?.PumpEvents();");
        pumpBody.ShouldContain("PublishWindowSurfaceSnapshot(");
        pumpBody.ShouldNotContain("Window.DoEvents();");

        int collapsedLoopStart = host.IndexOf("private void BlockForCollapsedWindowRendering", StringComparison.Ordinal);
        collapsedLoopStart.ShouldBeGreaterThanOrEqualTo(0);
        int pumpMethodStart = host.IndexOf("private void PumpCollapsedWindowEvents()", collapsedLoopStart, StringComparison.Ordinal);
        pumpMethodStart.ShouldBeGreaterThan(collapsedLoopStart);
        string collapsedLoopBody = host[collapsedLoopStart..pumpMethodStart];
        collapsedLoopBody.IndexOf("PumpCollapsedWindowEvents();", StringComparison.Ordinal)
            .ShouldBeLessThan(collapsedLoopBody.IndexOf("_waitToRender();", StringComparison.Ordinal));

        int endTickStart = source.IndexOf("private void EndTick()", StringComparison.Ordinal);
        endTickStart.ShouldBeGreaterThanOrEqualTo(0);
        int swapStart = source.IndexOf("private void SwapBuffers()", endTickStart, StringComparison.Ordinal);
        swapStart.ShouldBeGreaterThan(endTickStart);
        string endTickBody = source[endTickStart..swapStart];

        endTickBody.ShouldNotContain("Window.DoEvents()");
    }

    [Test]
    public void XRWindow_ConsumedInteractiveSnapshotsDeferFullInternalGenerationUntilSettled()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        int consumeStart = source.IndexOf("private void ConsumeLatestWindowSurfaceSnapshotForRenderFrame()", StringComparison.Ordinal);
        consumeStart.ShouldBeGreaterThanOrEqualTo(0);
        int nextMethod = source.IndexOf("private void RecordAllRenderExtents", consumeStart, StringComparison.Ordinal);
        nextMethod.ShouldBeGreaterThan(consumeStart);

        string consumeBody = source[consumeStart..nextMethod];

        consumeBody.ShouldContain("ApplyInteractivePresentationResize");
        consumeBody.ShouldContain("if (snapshot.IsInteractiveResize)");
        consumeBody.ShouldContain("return;");
        consumeBody.ShouldContain("QueueFullInternalResize");
        consumeBody.ShouldContain("force: true");
        consumeBody.ShouldContain("\"native-snapshot-consumed-settled\"");
        consumeBody.ShouldNotContain("native-snapshot-consumed-live-policy");
    }

    [Test]
    public void XRWindow_InteractiveResizeGuardClearsActiveFlagWhenNormalRenderIsActive()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs").Replace("\r\n", "\n");
        int renderStart = source.IndexOf("internal void RenderInteractiveResizeFrame(string reason, bool allowCurrentThread, bool deferWhenOnRenderThread)", StringComparison.Ordinal);
        renderStart.ShouldBeGreaterThanOrEqualTo(0);
        int nextMethod = source.IndexOf("private void ProcessPendingInteractivePresentationResize()", renderStart, StringComparison.Ordinal);
        nextMethod.ShouldBeGreaterThan(renderStart);

        string renderBody = source[renderStart..nextMethod];

        renderBody.ShouldContain("bool isRenderOwnerThread = currentThreadId == RenderOwnerThreadId;");
        renderBody.ShouldContain("bool canRenderOnCurrentThread = isRenderOwnerThread &&");
        renderBody.ShouldNotContain("Window.API.API == ContextAPI.OpenGL || isRenderOwnerThread");
        renderBody.ShouldNotContain("Interlocked.CompareExchange(ref _interactiveResizeRenderActive, 1, 0) != 0 ||");
        renderBody.ShouldContain("InteractiveResizeDiagnostics.RecordSuppressedRender(\"interactive-active\");");
        renderBody.ShouldContain("Volatile.Write(ref _interactiveResizeRenderActive, 0);\n                InteractiveResizeDiagnostics.RecordSuppressedRender(\"normal-render-active\");");
        renderBody.ShouldContain("RuntimeRenderingHostServices.Scheduling.TryDispatchInteractiveResizeFrame(presentationPackageId)");
        renderBody.ShouldNotContain("Window.DoRender()");
        renderBody.ShouldNotContain("ProcessPendingInteractivePresentationResize()");
    }

    [Test]
    public void EngineTimer_InteractiveResizeDispatchUsesNormalFrameAndCollectPublication()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Host/Core/Time/EngineTimer.cs");
        int dispatchStart = source.IndexOf("public XREngine.Rendering.InteractiveResizeDispatchResult TryDispatchInteractiveResizeFrame(", StringComparison.Ordinal);
        dispatchStart.ShouldBeGreaterThanOrEqualTo(0);
        int normalDispatchStart = source.IndexOf("public bool DispatchRender()", dispatchStart, StringComparison.Ordinal);
        normalDispatchStart.ShouldBeGreaterThan(dispatchStart);

        string interactiveDispatch = source[dispatchStart..normalDispatchStart];
        interactiveDispatch.ShouldContain("Engine.IsDispatchingRenderFrame");
        interactiveDispatch.ShouldContain("IsRenderDispatchDue()");
        interactiveDispatch.ShouldContain("DispatchRender(processMainThreadTasks: false, out dispatchReason)");
        interactiveDispatch.ShouldContain("PresentFrameId != previousPresentFrameId");
        interactiveDispatch.ShouldNotContain("DispatchRender(processMainThreadTasks: true");
        interactiveDispatch.ShouldNotContain("_visibilityGenerationGate");
    }

    [Test]
    public void InteractiveResizeStrategies_UseHostRenderCadenceInsteadOfFixedSixtyHertz()
    {
        string win32 = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/DesktopWin32ModalResizeHook.cs");
        string glfw = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/DesktopGlfwResizeHook.cs");
        string backend = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/DesktopSilkWindowBackend.cs");

        win32.ShouldContain("case WmPaint when _inSizeMove:");
        win32.ShouldContain("case WmSizing:");
        win32.ShouldContain("case WmTimer when wParam == TimerId:");
        win32.ShouldContain("_window?.UpdateNativeResize();");
        win32.ShouldContain("RequestPaint();");
        win32.ShouldContain("_window?.BeginNativeResize();");
        win32.ShouldContain("_window?.EndNativeResize();");
        backend.ShouldContain("_sink?.RepaintRequested();");
        win32.ShouldNotContain("ActiveSizingRenderHz");
        glfw.ShouldContain("window.NativeWindow.Resize += OnResize;");
        glfw.ShouldContain("_window?.UpdateNativeResize()");
        glfw.ShouldNotContain("TargetRenderHz");
    }

    [Test]
    public void RenderPipeline_FreezesAutomaticInternalResolutionDuringInteractiveResize()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        int generationStart = source.IndexOf(
            "ShouldDeferResourceGenerationForInteractiveWindowResize(viewport) &&",
            StringComparison.Ordinal);
        generationStart.ShouldBeGreaterThanOrEqualTo(0);
        int drainStart = source.IndexOf("DrainRetiredGenerations();", generationStart, StringComparison.Ordinal);
        drainStart.ShouldBeGreaterThan(generationStart);
        string generationPolicy = source[generationStart..drainStart];
        generationPolicy.ShouldContain("IsResizeOnlyGenerationDelta(dragGeneration.Key, key)");
        generationPolicy.ShouldContain("DiscardPendingGeneration(\"InteractiveResize\")");
    }

    [Test]
    public void XRWindow_CommitsFullInternalResizeOnlyAfterRenderResourcesAreReady()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");

        int applyStart = source.IndexOf("private void ApplyFramebufferResize", StringComparison.Ordinal);
        applyStart.ShouldBeGreaterThanOrEqualTo(0);
        int applyEnd = source.IndexOf("#endregion", applyStart, StringComparison.Ordinal);
        applyEnd.ShouldBeGreaterThan(applyStart);
        string applyBody = source[applyStart..applyEnd];

        applyBody.ShouldContain("RecordPresentationAndOutputExtent(obj);");
        applyBody.ShouldNotContain("RecordAllRenderExtents(obj);");
        applyBody.ShouldContain("vp.SetFullInternalExtent");

        source.ShouldContain("private void TryCommitPendingFullInternalResizeAfterRender");
        source.ShouldContain("AreFullInternalResizeResourcesReady(pending)");
        source.ShouldContain("if (!pipelineInstance.IsCurrentResourceProfileReady(viewport))");
        source.ShouldContain("TryCommitPendingFullInternalExtent(");
        source.ShouldContain("XRWindow.CommitPendingFullInternalResize");
    }

    [Test]
    public void XRWindow_AdmittedFullInternalResizeAlwaysRefreshesQueuedGeneration()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        int queueStart = source.IndexOf("private void QueueFullInternalResize", StringComparison.Ordinal);
        queueStart.ShouldBeGreaterThanOrEqualTo(0);
        int beginResizeStart = source.IndexOf("internal void BeginInteractiveResize", queueStart, StringComparison.Ordinal);
        beginResizeStart.ShouldBeGreaterThan(queueStart);

        string queueBody = source[queueStart..beginResizeStart];

        queueBody.ShouldContain("if (!requestAccepted)");
        queueBody.ShouldContain("Volatile.Write(ref _pendingFullInternalResizeGeneration");
        queueBody.ShouldNotContain("currentPending");
        queueBody.ShouldNotContain("currentWidth");
        queueBody.ShouldNotContain("currentHeight");
    }

    [Test]
    public void VulkanFrameSlotRetirementDrainsSwapchainDependentResourcesAfterSlotWait()
    {
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Retirement/VulkanRenderer.ResourceRetirement.cs");
        string frameSlotRetirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.FrameSlots.Retirement.cs");
        string framebuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string renderbuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkRenderBuffer.cs");

        retirement.ShouldContain("ResourceRuntime.DrainRetiredFramebuffers(");
        retirement.ShouldContain("ResourceRuntime.DrainRetiredImages(");
        framebuffer.ShouldContain("BackendContext.Resources.Framebuffers.RetireFramebuffer(_frameBuffer");
        renderbuffer.ShouldContain("BackendContext.Resources.Images.RetireOwnedResources(new RetiredImageResources(");

        int waitStart = frameSlotRetirement.IndexOf("private bool TryWaitCurrentFrameSlotAndDrainRetiredResources", StringComparison.Ordinal);
        waitStart.ShouldBeGreaterThanOrEqualTo(0);
        string waitBody = frameSlotRetirement[waitStart..];

        waitBody.ShouldContain("int frameSlot");
        waitBody.ShouldNotContain("_desktopFrameSlot");
        waitBody.ShouldContain("WaitForTimelineValue(_commandRuntime.Synchronization._graphicsTimelineSemaphore, slotWaitValue);");
        waitBody.ShouldContain("ResourceRuntime.DrainRetiredDescriptorPools(");
        waitBody.ShouldContain("ResourceRuntime.DrainRetiredPipelines(");
        waitBody.ShouldContain("ResourceRuntime.DrainRetiredBuffers(");
        waitBody.ShouldContain("ResourceRuntime.DrainRetiredFramebuffers(");
        waitBody.ShouldContain("ResourceRuntime.DrainRetiredImages(");
    }

    [Test]
    public void VulkanMismatchedSwapchainPresentUsesValidatedPresentScaling()
    {
        string preflight = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Preflight.cs");
        string swapchainPolicy = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.SwapchainPolicy.cs");
        string acquire = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Acquire.cs");
        string presentation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Presentation.cs");
        string presentScaling = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Swapchain/VulkanRenderer.PresentScaling.cs");
        string swapchain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanDesktopSwapchainService.cs");
        string extensions = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.Instance.cs");
        string logicalDevice = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");

        extensions.ShouldContain("VK_KHR_get_surface_capabilities2");
        extensions.ShouldContain("VK_EXT_surface_maintenance1");
        logicalDevice.ShouldContain("VK_EXT_swapchain_maintenance1");
        logicalDevice.ShouldContain("PhysicalDeviceSwapchainMaintenance1FeaturesEXT");
        logicalDevice.ShouldContain("OutputRuntime.Desktop.Maintenance1Enabled = enableSwapchainMaintenance1Feature;");

        swapchain.ShouldContain("SurfacePresentScalingCapabilitiesEXT");
        swapchain.ShouldContain("PresentScalingFlagsKHR.StretchBitExt");
        swapchain.ShouldContain("SwapchainPresentScalingCreateInfoEXT");
        swapchain.ShouldContain("TryGetPresentScalingConfiguration(");
        swapchain.ShouldContain("createInfo.PNext = &presentScalingCreateInfo;");
        swapchain.ShouldContain("_output.Desktop.PresentScalingActive = usePresentScaling;");
        presentScaling.ShouldContain("_outputRuntime.Desktop.PresentScalingActive");

        swapchainPolicy.ShouldContain("private bool CanPresentMismatchedSwapchainExtent(");
        preflight.ShouldContain("attempt.CanPresentMismatchedSwapchainExtent =");
        swapchainPolicy.ShouldContain("OutputRuntime.Desktop.IsPresentScalingExtentSupported(");
        preflight.ShouldContain("!attempt.CanPresentMismatchedSwapchainExtent");
        swapchainPolicy.ShouldContain("internal bool ShouldKeepDesktopPresentScalingSwapchainCore(Result result, bool interactiveResize)");
        acquire.ShouldContain("if (!ShouldKeepDesktopPresentScalingSwapchainCore(");
        presentation.ShouldContain("if (!ShouldKeepDesktopPresentScalingSwapchainCore(");
    }

    [Test]
    public void VulkanScaledPresentMapsLiveSceneAndImGuiToFixedSwapchainRasterSpace()
    {
        var presentationExtent = new Vector2D<int>(1142, 724);
        var backbufferExtent = new Vector2D<int>(1338, 794);
        const int sharedPresentationEdge = 371;

        BoundingRectangle full = VulkanFrameLoop.ScalePresentationRegionToBackbuffer(
            new BoundingRectangle(0, 0, presentationExtent.X, presentationExtent.Y),
            presentationExtent,
            backbufferExtent);
        BoundingRectangle left = VulkanFrameLoop.ScalePresentationRegionToBackbuffer(
            new BoundingRectangle(0, 0, sharedPresentationEdge, presentationExtent.Y),
            presentationExtent,
            backbufferExtent);
        BoundingRectangle right = VulkanFrameLoop.ScalePresentationRegionToBackbuffer(
            new BoundingRectangle(
                sharedPresentationEdge,
                0,
                presentationExtent.X - sharedPresentationEdge,
                presentationExtent.Y),
            presentationExtent,
            backbufferExtent);

        full.ShouldBe(new BoundingRectangle(0, 0, backbufferExtent.X, backbufferExtent.Y));
        left.X.ShouldBe(0);
        left.Y.ShouldBe(0);
        left.Height.ShouldBe(backbufferExtent.Y);
        right.Y.ShouldBe(0);
        right.Height.ShouldBe(backbufferExtent.Y);
        (left.X + left.Width).ShouldBe(right.X);
        (right.X + right.Width).ShouldBe(backbufferExtent.X);

        string presentCommand = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs");
        string viewportRenderArea = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/State/VPRC_PushViewportRenderArea.cs");
        string imgui = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiOverlayCommandRecorder.cs");

        presentCommand.ShouldContain("renderer.MapWindowPresentationRegionToBackbuffer(region)");
        viewportRenderArea.ShouldContain("!UseInternalResolution &&");
        viewportRenderArea.ShouldContain("!externalRegion.HasValue &&");
        viewportRenderArea.ShouldContain("!outputRegion.HasValue &&");
        viewportRenderArea.ShouldContain("res = renderer.MapWindowPresentationRegionToBackbuffer(res);");
        imgui.ShouldContain("uint width = input.Target.Extent.Width;");
        imgui.ShouldContain("uint height = input.Target.Extent.Height;");
        imgui.ShouldContain("Vector2 scale = input.Snapshot.FramebufferScale * new Vector2(");
        imgui.ShouldContain("width / (float)input.Snapshot.FramebufferWidth");
        imgui.ShouldContain("height / (float)input.Snapshot.FramebufferHeight");
        imgui.ShouldContain("(clip.X - input.Snapshot.DisplayPos.X) * scale.X");
    }

    [Test]
    public void FailedRenderResourceGenerationFenceRetainsResourcesUntilReplacementCompletes()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");

        int failureStart = source.IndexOf(
            "if (fenceStatus == EGpuFenceStatus.Failed)",
            StringComparison.Ordinal);
        failureStart.ShouldBeGreaterThanOrEqualTo(0);
        int dequeueStart = source.IndexOf(
            "_retiredGenerations.Dequeue();",
            failureStart,
            StringComparison.Ordinal);
        dequeueStart.ShouldBeGreaterThan(failureStart);
        string failurePath = source[failureStart..dequeueStart];

        failurePath.ShouldContain("renderer?.InsertGpuFence()");
        failurePath.ShouldContain("if (replacementFence is null)");
        failurePath.ShouldContain("retired.ReplaceFailedRetirementFence(replacementFence)");
        failurePath.ShouldContain("return;");
        failurePath.ShouldNotContain("DisposeGeneration(retired");
        source[dequeueStart..].ShouldContain("DisposeGeneration(retired, retired.RetirementReason");
    }

    [Test]
    public void VulkanBlitRegionsClampToLiveSourceAndDestinationExtents()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.DrawComputeBlitServices.cs");

        int buildStart = source.IndexOf("private static bool TryBuildPreparedImageBlit", StringComparison.Ordinal);
        buildStart.ShouldBeGreaterThanOrEqualTo(0);
        int transitionStart = source.IndexOf("internal unsafe void TransitionPreparedImageForBlit", buildStart, StringComparison.Ordinal);
        transitionStart.ShouldBeGreaterThan(buildStart);
        string buildBody = source[buildStart..transitionStart];

        buildBody.ShouldContain("int sourceWidth = (int)Math.Max(source.Extent.Width, 1u);");
        buildBody.ShouldContain("int destinationWidth = (int)Math.Max(destination.Extent.Width, 1u);");
        buildBody.ShouldContain("int srcX0 = ClampPreparedBlitOffset(inX, sourceWidth);");
        buildBody.ShouldContain("int srcX1 = ClampPreparedBlitOffset((long)inX + inW, sourceWidth);");
        buildBody.ShouldContain("int dstX0 = ClampPreparedBlitOffset(outX, destinationWidth);");
        buildBody.ShouldContain("int dstX1 = ClampPreparedBlitOffset((long)outX + outW, destinationWidth);");
        buildBody.ShouldContain("if (srcX1 <= srcX0 || srcY1 <= srcY0 || dstX1 <= dstX0 || dstY1 <= dstY0 || layerCount == 0)");
        buildBody.ShouldContain("private static int ClampPreparedBlitOffset(long value, int extent)");
    }

    [Test]
    public void WindowPumpHost_StopFlushesMailboxBeforeCompletingQueue()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Platform.Desktop/Windowing/RuntimeWindowPumpHost.cs");
        int stopStart = source.IndexOf("public void Stop()", StringComparison.Ordinal);
        stopStart.ShouldBeGreaterThanOrEqualTo(0);
        int flushStart = source.IndexOf("public bool Flush", stopStart, StringComparison.Ordinal);
        flushStart.ShouldBeGreaterThan(stopStart);

        string stopBody = source[stopStart..flushStart];

        stopBody.ShouldContain("Flush(TimeSpan.FromSeconds(2), \"WindowPumpHost.Stop\")");
        stopBody.ShouldContain("_queue?.CompleteAdding();");
        stopBody.IndexOf("Flush(TimeSpan.FromSeconds(2), \"WindowPumpHost.Stop\")", StringComparison.Ordinal)
            .ShouldBeLessThan(stopBody.IndexOf("_queue?.CompleteAdding();", StringComparison.Ordinal));
    }

    [Test]
    public void XRWindow_ExternalPumpDisposeExecutesRenderTeardownInlineOnRenderThread()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        int disposeStart = source.IndexOf("private bool TryBeginExternalPumpDispose", StringComparison.Ordinal);
        disposeStart.ShouldBeGreaterThanOrEqualTo(0);
        int disposeResourcesStart = source.IndexOf("private void DisposeExternalPumpRenderResources", disposeStart, StringComparison.Ordinal);
        disposeResourcesStart.ShouldBeGreaterThan(disposeStart);

        string disposeBody = source[disposeStart..disposeResourcesStart];

        disposeBody.ShouldContain("if (RuntimeEngine.IsRenderThread)");
        disposeBody.ShouldContain("DisposeExternalPumpRenderResources(reason);");
        disposeBody.ShouldContain("RuntimeEngine.EnqueueRenderThreadTask(");
    }

    [Test]
    public void XRWindow_CollapsedHostApprovedCloseArmsPumpCompletionInsteadOfRenderThreadJob()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        int handlerStart = source.IndexOf("private bool HandleDesktopCloseRequested()", StringComparison.Ordinal);
        handlerStart.ShouldBeGreaterThanOrEqualTo(0);
        int completionStart = source.IndexOf("private bool TryCompleteApprovedNativeClose()", handlerStart, StringComparison.Ordinal);
        completionStart.ShouldBeGreaterThan(handlerStart);
        string handlerBody = source[handlerStart..completionStart];

        int quiesce = handlerBody.IndexOf("QuiesceForWindowRendererTeardown(this)", StringComparison.Ordinal);
        int closeInProgress = handlerBody.IndexOf("_approvedNativeCloseInProgress = true;", StringComparison.Ordinal);
        int collapsedStart = handlerBody.IndexOf("if (!IsNativeEventPumpExternallyOwned)", StringComparison.Ordinal);
        quiesce.ShouldBeGreaterThanOrEqualTo(0);
        closeInProgress.ShouldBeGreaterThan(quiesce);
        collapsedStart.ShouldBeGreaterThan(closeInProgress);

        // The collapsed branch must end with its own approval return, before the external-pump job.
        int collapsedReturn = handlerBody.IndexOf("return true;", collapsedStart, StringComparison.Ordinal);
        collapsedReturn.ShouldBeGreaterThan(collapsedStart);
        string collapsedBranch = handlerBody[collapsedStart..collapsedReturn];
        string externalPumpPath = handlerBody[collapsedReturn..];

        collapsedBranch.ShouldContain("Interlocked.Exchange(ref _approvedNativeCloseCompletionPending, 1);");
        collapsedBranch.ShouldNotContain("EnqueueRenderThreadTask");
        collapsedBranch.ShouldNotContain("Dispose(");
        collapsedBranch.ShouldNotContain("RemoveWindow(");
        handlerBody[..collapsedStart].ShouldNotContain("EnqueueRenderThreadTask");

        externalPumpPath.ShouldContain("RuntimeEngine.EnqueueRenderThreadTask(");
        externalPumpPath.ShouldContain("() => TryBeginExternalPumpDispose(\"DesktopClose\")");
        externalPumpPath.ShouldContain("RenderThreadJobKind.RequiresGraphicsContext");

        // The native close callback never disposes or unregisters the window inline.
        handlerBody.ShouldNotContain("Dispose();");
        handlerBody.ShouldNotContain("RemoveWindow(");
    }

    [Test]
    public void XRWindow_HostPumpCompletesApprovedCloseBeforeAndAfterNativeEvents()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        int pumpStart = source.IndexOf("public void PumpNativeWindowEventsFromHost()", StringComparison.Ordinal);
        pumpStart.ShouldBeGreaterThanOrEqualTo(0);
        int nextMethod = source.IndexOf("private void ApplyVSyncModeOnRenderThread", pumpStart, StringComparison.Ordinal);
        nextMethod.ShouldBeGreaterThan(pumpStart);
        string pumpBody = source[pumpStart..nextMethod];

        const string completionCheck = "if (TryCompleteApprovedNativeClose())\n                return;";
        const string disposedGuard = "if (_isDisposed || _isDisposing)";
        int firstCompletion = pumpBody.IndexOf(completionCheck, StringComparison.Ordinal);
        int firstDisposedGuard = pumpBody.IndexOf(disposedGuard, StringComparison.Ordinal);
        int nativePump = pumpBody.IndexOf("_desktopBackend?.PumpEvents();", StringComparison.Ordinal);
        firstCompletion.ShouldBeGreaterThanOrEqualTo(0);
        firstDisposedGuard.ShouldBeGreaterThan(firstCompletion);
        nativePump.ShouldBeGreaterThan(firstDisposedGuard);

        // A close approved inside PumpEvents completes after the native callback unwinds.
        int secondCompletion = pumpBody.IndexOf(completionCheck, nativePump, StringComparison.Ordinal);
        int secondDisposedGuard = pumpBody.IndexOf(disposedGuard, nativePump, StringComparison.Ordinal);
        int snapshotPublish = pumpBody.IndexOf("PublishWindowSurfaceSnapshot(", StringComparison.Ordinal);
        secondCompletion.ShouldBeGreaterThan(nativePump);
        secondDisposedGuard.ShouldBeGreaterThan(secondCompletion);
        snapshotPublish.ShouldBeGreaterThan(secondDisposedGuard);
        pumpBody.IndexOf(completionCheck, secondCompletion + completionCheck.Length, StringComparison.Ordinal)
            .ShouldBe(-1);
    }

    [Test]
    public void XRWindow_ApprovedCloseCompletionQuiescesBeforeDisposeAndAlwaysRemovesWindow()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        int completionStart = source.IndexOf("private bool TryCompleteApprovedNativeClose()", StringComparison.Ordinal);
        completionStart.ShouldBeGreaterThanOrEqualTo(0);
        int nextMethod = source.IndexOf("private void OnFocusChanged", completionStart, StringComparison.Ordinal);
        nextMethod.ShouldBeGreaterThan(completionStart);
        string completionBody = source[completionStart..nextMethod];

        int idleFastPath = completionBody.IndexOf("Volatile.Read(ref _approvedNativeCloseCompletionPending) == 0", StringComparison.Ordinal);
        int consume = completionBody.IndexOf("Interlocked.Exchange(ref _approvedNativeCloseCompletionPending, 0) == 0", StringComparison.Ordinal);
        int notPending = completionBody.IndexOf("return false;", StringComparison.Ordinal);
        int tryStart = completionBody.IndexOf("            try\n            {", StringComparison.Ordinal);
        int abandonedGuard = completionBody.IndexOf("!_renderer.IsShutdownTeardownAbandoned &&", StringComparison.Ordinal);
        int quiesce = completionBody.IndexOf("RuntimeRenderingHostServices.Factories.QuiesceForWindowRendererTeardown(this)", StringComparison.Ordinal);
        int abandonTeardown = completionBody.IndexOf("_renderer.AbandonShutdownTeardown();", StringComparison.Ordinal);
        int dispose = completionBody.IndexOf("Dispose();", StringComparison.Ordinal);
        int catchStart = completionBody.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        int logException = completionBody.IndexOf("Debug.LogException(ex,", StringComparison.Ordinal);
        int finallyStart = completionBody.IndexOf("            finally\n            {", StringComparison.Ordinal);
        const string removeWindowCall = "RuntimeRenderingHostServices.Factories.RemoveWindow(this);";
        int removeWindow = completionBody.IndexOf(removeWindowCall, StringComparison.Ordinal);
        int completed = completionBody.LastIndexOf("return true;", StringComparison.Ordinal);

        idleFastPath.ShouldBeGreaterThanOrEqualTo(0);
        consume.ShouldBeGreaterThan(idleFastPath);
        notPending.ShouldBeGreaterThan(consume);
        tryStart.ShouldBeGreaterThan(notPending);
        abandonedGuard.ShouldBeGreaterThan(tryStart);
        quiesce.ShouldBeGreaterThan(abandonedGuard);
        abandonTeardown.ShouldBeGreaterThan(quiesce);
        dispose.ShouldBeGreaterThan(abandonTeardown);
        catchStart.ShouldBeGreaterThan(dispose);
        logException.ShouldBeGreaterThan(catchStart);
        finallyStart.ShouldBeGreaterThan(logException);
        removeWindow.ShouldBeGreaterThan(finallyStart);
        completed.ShouldBeGreaterThan(removeWindow);
        completionBody.IndexOf("RemoveWindow(", removeWindow + removeWindowCall.Length, StringComparison.Ordinal).ShouldBe(-1);
    }

    [Test]
    public void RuntimeLocalPlayerViewport_ExposesSnapshotInputBindingWithoutThreadAffinedDeviceEscape()
    {
        string contract = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimePlayerViewportContracts.cs");
        string viewport = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRViewport.cs");
        string xrWindow = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        string localPlayerController = ReadWorkspaceFile("XREngine.Runtime.InputIntegration/Input/LocalPlayerController.cs");
        string localInputInterface = ReadWorkspaceFile("XREngine.Input/Devices/InputInterfaces/LocalInputInterface.cs");
        string snapshotInputDevices = ReadWorkspaceFile("XREngine.Runtime.InputIntegration/Input/WindowSnapshotInputDevices.cs");
        string editorPlayMode = ReadWorkspaceFile("XREngine.Editor/EditorPlayModeController.cs");
        string inspectorPanel = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.InspectorPanel.cs");

        xrWindow.ShouldContain("public IRuntimeWindowBackend? DesktopWindowBackend => _desktopBackend;");
        xrWindow.ShouldContain("public IRuntimeWindowGlContext? DesktopGlContext => _desktopBackend?.GlContext;");
        xrWindow.ShouldContain("public IRuntimeWindowVulkanSurface? DesktopVulkanSurface => _desktopBackend?.VulkanSurface;");
        xrWindow.ShouldNotContain("public IWindow ThreadAffinedNativeWindow");
        xrWindow.ShouldNotContain("public IInputContext? Input");
        contract.ShouldContain("WindowInputSnapshot ConsumeInputSnapshot();");
        contract.ShouldContain("void RequestMouseCapture(bool captured);");
        contract.ShouldNotContain("GetThreadAffinedDeviceSourceForBinding");
        contract.ShouldNotContain("InputContext { get; }");
        viewport.ShouldContain("IRuntimeLocalPlayerViewport.ConsumeInputSnapshot()");
        viewport.ShouldContain("Window?.ConsumeLatestWindowInputSnapshot() ?? default");
        viewport.ShouldContain("Window?.RequestMouseCapture(captured);");
        viewport.ShouldNotContain("GetThreadAffinedDeviceSourceForBinding");
        viewport.ShouldNotContain("IRuntimeLocalPlayerViewport.InputContext");
        xrWindow.ShouldContain("public void RequestMouseCapture(bool captured)");
        xrWindow.ShouldContain("SetMouseCaptureOnWindowThread(captured)");
        localInputInterface.ShouldContain("public void UpdateDevices(");
        localInputInterface.ShouldContain("BaseKeyboard? keyboard");
        snapshotInputDevices.ShouldContain("WindowSnapshotKeyboard");
        snapshotInputDevices.ShouldContain("WindowSnapshotMouse");
        snapshotInputDevices.ShouldContain("SetCaptureRequest(Action<bool>? captureRequest)");
        localPlayerController.ShouldContain("RefreshViewportInputBinding();");
        localPlayerController.ShouldContain("WindowInputSnapshot snapshot = _viewport.ConsumeInputSnapshot();");
        localPlayerController.ShouldContain("_viewport?.RequestMouseCapture(captured)");
        localPlayerController.ShouldNotContain("Silk.NET.Input");
        localPlayerController.ShouldNotContain("GetThreadAffinedDeviceSourceForBinding");
        editorPlayMode.ShouldContain("localPlayer.RefreshViewportInputBinding();");
        editorPlayMode.ShouldNotContain("GetThreadAffinedDeviceSourceForBinding()");
        editorPlayMode.ShouldNotContain("ensuredViewport.Window?.Input");
        inspectorPanel.ShouldContain("localPlayer.RefreshViewportInputBinding();");
        inspectorPanel.ShouldNotContain("GetThreadAffinedDeviceSourceForBinding()");
        inspectorPanel.ShouldNotContain("ensuredViewport.Window?.Input");
    }

    [Test]
    public void EditorPreviewTextureInteropUsesRenderThreadCapabilityService()
    {
        string previewService = ReadWorkspaceFile("XREngine.Editor/Rendering/EditorTexturePreviewService.cs");
        string materialInspector = ReadWorkspaceFile("XREngine.Editor/AssetEditors/XRMaterialInspector.cs");
        string renderPipelineInspector = ReadWorkspaceFile("XREngine.Editor/AssetEditors/RenderPipelineInspector.cs");
        string viewportPanel = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.ViewportPanel.cs");

        previewService.ShouldContain("if (!Engine.IsRenderThread)");
        previewService.ShouldContain("EditorRendererCapabilityResolver.TryGet");
        previewService.ShouldContain("capability.TryGetTexturePreviewHandle");
        materialInspector.ShouldContain("EditorTexturePreviewService.TryGetHandle(");
        renderPipelineInspector.ShouldContain("EditorTexturePreviewService.TryGetHandle(");
        viewportPanel.ShouldContain("EditorTexturePreviewService.TryGetHandle(");
    }

    [Test]
    public void EditorAndAppWindowAccessUseXRWindowSnapshotsAndMailboxWrappers()
    {
        string xrWindow = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        string fileDrop = ReadWorkspaceFile("XREngine.Editor/EditorFileDropHandler.cs");
        string fileBrowser = ReadWorkspaceFile("XREngine.Editor/UI/ImGuiFileBrowser.cs");
        string statePanel = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.StatePanel.cs");
        string vrState = ReadWorkspaceFile("XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs");

        xrWindow.ShouldContain("public event Action<XRWindow, string[]>? FileDropped;");
        xrWindow.ShouldContain("public event Action<XRWindow>? ClosingRequested;");
        xrWindow.ShouldContain("public event Action<XRWindow, Vector2D<int>>? FramebufferResized;");
        xrWindow.ShouldContain("public string WindowTitle");
        xrWindow.ShouldContain("public Vector2D<int> WindowSizeSnapshot");
        xrWindow.ShouldContain("RuntimeRenderingHostServices.Scheduling.EnqueueWindowThreadTask(");

        fileDrop.ShouldContain("window.FileDropped += HandleFileDrop;");
        fileDrop.ShouldNotContain("window.Window.FileDrop");
        fileBrowser.ShouldContain("window.ClosingRequested += state.WindowClosingHandler;");
        fileBrowser.ShouldContain("window.FramebufferResized += state.FramebufferResizeHandler;");
        fileBrowser.ShouldContain("xrWindow?.RequestClose();");
        fileBrowser.ShouldNotContain("window.Window.Closing");
        fileBrowser.ShouldNotContain("window.Window.FramebufferResize");
        fileBrowser.ShouldNotContain("silkWindow.Close()");
        statePanel.ShouldContain("window.WindowTitle");
        statePanel.ShouldContain("window.WindowSizeSnapshot");
        vrState.ShouldContain("window?.EffectiveFramebufferSize");
        vrState.ShouldContain("window?.WindowSizeSnapshot");
        vrState.ShouldNotContain("window?.Window.FramebufferSize");
        vrState.ShouldNotContain("window?.Window.Size");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);
}
