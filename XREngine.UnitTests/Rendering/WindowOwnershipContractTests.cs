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
        string source = ReadWorkspaceFile("XRENGINE/Engine/Engine.WindowPumpHost.cs");
        int enqueueStart = source.IndexOf("public void EnqueueWindowTask", StringComparison.Ordinal);
        enqueueStart.ShouldBeGreaterThanOrEqualTo(0);
        int invokeStart = source.IndexOf("public T InvokeWindowTask", StringComparison.Ordinal);
        invokeStart.ShouldBeGreaterThan(enqueueStart);

        string enqueueBody = source[enqueueStart..invokeStart];

        enqueueBody.ShouldContain("Post(task, reason);");
        enqueueBody.ShouldNotContain("_blockingWaitCount");
    }

    [Test]
    public void XRWindow_InputSnapshotPublishesThreadOwnedKeyMouseTextAndScrollEvents()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        string ownership = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/WindowOwnership/RuntimeWindowOwnership.cs");

        source.ShouldContain("keyboard.KeyDown += InputSnapshot_KeyDown;");
        source.ShouldContain("keyboard.KeyUp += InputSnapshot_KeyUp;");
        source.ShouldContain("keyboard.KeyChar += InputSnapshot_KeyChar;");
        source.ShouldContain("mouse.MouseDown += InputSnapshot_MouseDown;");
        source.ShouldContain("mouse.MouseUp += InputSnapshot_MouseUp;");
        source.ShouldContain("mouse.MouseMove += InputSnapshot_MouseMove;");
        source.ShouldContain("mouse.Scroll += InputSnapshot_Scroll;");
        source.ShouldContain("_inputSnapshotAccumulator.RecordPointerPosition");
        source.ShouldContain("_inputSnapshotAccumulator.RecordScroll");
        ownership.ShouldContain("PointerDeltaX");
        ownership.ShouldContain("ScrollDeltaY");
        ownership.ShouldContain("TextInputCount");
    }

    [Test]
    public void CollapsedWindowHost_PumpsNativeEventsBeforeEnteringRenderDispatch()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs").Replace("\r\n", "\n");
        string host = ReadWorkspaceFile("XRENGINE/Engine/Engine.RenderThreadHost.cs").Replace("\r\n", "\n");

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
        pumpBody.ShouldContain("Window.DoEvents();");
        pumpBody.ShouldContain("PublishWindowSurfaceSnapshot(");
        pumpBody.ShouldContain("PublishWindowInputSnapshot();");

        int collapsedLoopStart = host.IndexOf("private void BlockForCollapsedWindowRendering", StringComparison.Ordinal);
        collapsedLoopStart.ShouldBeGreaterThanOrEqualTo(0);
        int pumpMethodStart = host.IndexOf("private void PumpCollapsedWindowEvents()", collapsedLoopStart, StringComparison.Ordinal);
        pumpMethodStart.ShouldBeGreaterThan(collapsedLoopStart);
        string collapsedLoopBody = host[collapsedLoopStart..pumpMethodStart];
        collapsedLoopBody.IndexOf("PumpCollapsedWindowEvents();", StringComparison.Ordinal)
            .ShouldBeLessThan(collapsedLoopBody.IndexOf("Engine.Time.Timer.WaitToRender();", StringComparison.Ordinal));

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
        renderBody.ShouldContain("Window.API.API == ContextAPI.OpenGL || isRenderOwnerThread");
        renderBody.ShouldNotContain("Interlocked.CompareExchange(ref _interactiveResizeRenderActive, 1, 0) != 0 ||");
        renderBody.ShouldContain("InteractiveResizeDiagnostics.RecordSuppressedRender(reason + \":interactive-active\");");
        renderBody.ShouldContain("Volatile.Write(ref _interactiveResizeRenderActive, 0);\n                InteractiveResizeDiagnostics.RecordSuppressedRender(reason + \":normal-render-active\");");
        renderBody.ShouldContain("RuntimeRenderingHostServices.Scheduling.TryDispatchInteractiveResizeFrame()");
        renderBody.ShouldNotContain("Window.DoRender()");
        renderBody.ShouldNotContain("ProcessPendingInteractivePresentationResize()");
    }

    [Test]
    public void EngineTimer_InteractiveResizeDispatchUsesNormalFrameAndCollectPublication()
    {
        string source = ReadWorkspaceFile("XRENGINE/Core/Time/EngineTimer.cs");
        int dispatchStart = source.IndexOf("public bool TryDispatchInteractiveResizeFrame()", StringComparison.Ordinal);
        dispatchStart.ShouldBeGreaterThanOrEqualTo(0);
        int normalDispatchStart = source.IndexOf("public bool DispatchRender()", dispatchStart, StringComparison.Ordinal);
        normalDispatchStart.ShouldBeGreaterThan(dispatchStart);

        string interactiveDispatch = source[dispatchStart..normalDispatchStart];
        interactiveDispatch.ShouldContain("Engine.IsDispatchingRenderFrame");
        interactiveDispatch.ShouldContain("WaitToRender()");
        interactiveDispatch.ShouldContain("PresentFrameId != previousPresentFrameId");
        interactiveDispatch.ShouldNotContain("DispatchRender()");
        interactiveDispatch.ShouldNotContain("_visibilityGenerationGate");
    }

    [Test]
    public void InteractiveResizeStrategies_UseHostRenderCadenceInsteadOfFixedSixtyHertz()
    {
        string win32 = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/InteractiveResize/Win32ModalLoopTimerInteractiveResizeStrategy.cs");
        string glfw = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/InteractiveResize/GlfwResizeCallbackInteractiveResizeStrategy.cs");

        win32.ShouldContain("case WM_PAINT:");
        win32.ShouldContain("RecordCallbackAndRenderImmediate(\"win32-paint\")");
        win32.ShouldContain("RequestInteractiveResizePaint()");
        win32.ShouldContain("case WM_SIZING:");
        win32.ShouldContain("ApplyCoalescedClientPresentationResize(\"win32-sizing-live\")");
        win32.ShouldContain("RecordCallbackAndRenderImmediate(\"win32-sizing-live\")");
        win32.ShouldContain("EngineTimer.WaitToRender remains");
        win32.ShouldNotContain("RecordCallbackAndRenderImmediate(\"win32-timer\")");
        win32.ShouldNotContain("RecordCallbackAndRenderImmediate(\"win32-windowposchanged-live\")");
        win32.ShouldNotContain("ActiveSizingRenderHz");
        win32.ShouldNotContain("ShouldRenderByRateLimit");
        glfw.ShouldNotContain("TargetRenderHz");
        glfw.ShouldNotContain("ShouldRenderByRateLimit");
    }

    [Test]
    public void RenderPipeline_FreezesAutomaticInternalResolutionDuringInteractiveResize()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        int automaticResizeStart = source.IndexOf(
            "if (viewport.AllowAutomaticInternalResolution &&",
            StringComparison.Ordinal);
        automaticResizeStart.ShouldBeGreaterThanOrEqualTo(0);
        int renderScopeStart = source.IndexOf(
            "using (hostServices.PushRenderingPipeline(this))",
            automaticResizeStart,
            StringComparison.Ordinal);
        renderScopeStart.ShouldBeGreaterThan(automaticResizeStart);

        string automaticResizeBody = source[automaticResizeStart..renderScopeStart];

        automaticResizeBody.ShouldContain(
            "!ShouldDeferResourceGenerationForInteractiveWindowResize(viewport)");
        automaticResizeBody.ShouldContain("viewport.SetInternalResolution");
    }

    [Test]
    public void XRWindow_CommitsFullInternalResizeOnlyAfterRenderResourcesAreReady()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");

        int applyStart = source.IndexOf("private void ApplyFramebufferResize", StringComparison.Ordinal);
        applyStart.ShouldBeGreaterThanOrEqualTo(0);
        int inputStart = source.IndexOf("private void Input_ConnectionChanged", applyStart, StringComparison.Ordinal);
        inputStart.ShouldBeGreaterThan(applyStart);
        string applyBody = source[applyStart..inputStart];

        applyBody.ShouldContain("RecordPresentationAndOutputExtent(obj);");
        applyBody.ShouldNotContain("RecordAllRenderExtents(obj);");
        applyBody.ShouldContain("vp.SetFullInternalExtent");

        source.ShouldContain("private void TryCommitPendingFullInternalResizeAfterRender");
        source.ShouldContain("AreFullInternalResizeResourcesReady(pending)");
        source.ShouldContain("pipelineInstance.PendingGeneration is not null");
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
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");
        string frameLoop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string framebuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string renderbuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkRenderBuffer.cs");

        retirement.ShouldContain("private readonly List<RetiredFramebuffer>[] _retiredFramebuffers");
        retirement.ShouldContain("private readonly List<RetiredImageResourceEntry>[] _retiredImages");
        retirement.ShouldContain("internal void RetireFramebuffer(Framebuffer framebuffer)");
        retirement.ShouldContain("internal void RetireImageResources(");
        retirement.ShouldContain("in RetiredImageResources resources");
        retirement.ShouldContain("private void DrainRetiredFramebuffers");
        retirement.ShouldContain("private void DrainRetiredImages");
        framebuffer.ShouldContain("Renderer.RetireFramebuffer(_frameBuffer);");
        renderbuffer.ShouldContain("Renderer.RetireImageResources(new RetiredImageResources(");

        int waitStart = frameLoop.IndexOf("private bool TryWaitCurrentFrameSlotAndDrainRetiredResources", StringComparison.Ordinal);
        waitStart.ShouldBeGreaterThanOrEqualTo(0);
        int blockerStart = frameLoop.IndexOf("private bool TryGetViewportResourceBlocker", waitStart, StringComparison.Ordinal);
        blockerStart.ShouldBeGreaterThan(waitStart);
        string waitBody = frameLoop[waitStart..blockerStart];

        waitBody.ShouldContain("WaitForTimelineValue(_graphicsTimelineSemaphore, slotWaitValue);");
        waitBody.ShouldContain("DrainRetiredDescriptorPools();");
        waitBody.ShouldContain("DrainRetiredPipelines();");
        waitBody.ShouldContain("DrainRetiredBuffers();");
        waitBody.ShouldContain("DrainRetiredFramebuffers();");
        waitBody.ShouldContain("DrainRetiredImages();");
    }

    [Test]
    public void VulkanMismatchedSwapchainPresentUsesValidatedPresentScaling()
    {
        string frameLoop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string presentScaling = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.PresentScaling.cs");
        string swapchain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");
        string extensions = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");
        string logicalDevice = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

        presentScaling.ShouldContain("VK_KHR_get_surface_capabilities2");
        presentScaling.ShouldContain("VK_EXT_surface_maintenance1");
        presentScaling.ShouldContain("VK_EXT_swapchain_maintenance1");
        extensions.ShouldContain("GetSurfaceCapabilities2ExtensionName");
        extensions.ShouldContain("SurfaceMaintenance1ExtensionName");
        extensions.ShouldContain("SwapchainMaintenance1ExtensionName");
        logicalDevice.ShouldContain("PhysicalDeviceSwapchainMaintenance1FeaturesEXT");
        logicalDevice.ShouldContain("_swapchainMaintenance1Enabled = enableSwapchainMaintenance1Feature;");

        presentScaling.ShouldContain("SurfacePresentScalingCapabilitiesEXT");
        presentScaling.ShouldContain("PresentScalingFlagsKHR.StretchBitExt");
        presentScaling.ShouldContain("SwapchainPresentScalingCreateInfoEXT");
        presentScaling.ShouldContain("IsSwapchainPresentScalingExtentSupported");
        swapchain.ShouldContain("TryGetSwapchainPresentScalingConfiguration(");
        swapchain.ShouldContain("createInfo.PNext = &presentScalingCreateInfo;");
        swapchain.ShouldContain("_swapchainPresentScalingActive = usePresentScaling;");

        frameLoop.ShouldContain("private bool CanPresentMismatchedSwapchainExtent(");
        frameLoop.ShouldContain("bool canPresentMismatchedSwapchainExtent = liveSurfaceValid &&");
        frameLoop.ShouldContain("if (interactiveResize && canPresentMismatchedSwapchainExtent)");
        frameLoop.ShouldContain("Presenting through validated WSI scaling during interactive resize.");
        frameLoop.ShouldContain("if (_frameBufferInvalidated || (!surfaceMatchesSwapchain && !canPresentMismatchedSwapchainExtent))");
        frameLoop.ShouldContain("private bool ShouldKeepPresentScalingSwapchain(Result result, bool interactiveResize)");
        frameLoop.ShouldContain("if (!ShouldKeepPresentScalingSwapchain(result, interactiveResize))");
    }

    [Test]
    public void VulkanScaledPresentMapsLiveSceneAndImGuiToFixedSwapchainRasterSpace()
    {
        var presentationExtent = new Vector2D<int>(1142, 724);
        var backbufferExtent = new Vector2D<int>(1338, 794);
        const int sharedPresentationEdge = 371;

        BoundingRectangle full = VulkanRenderer.ScalePresentationRegionToBackbuffer(
            new BoundingRectangle(0, 0, presentationExtent.X, presentationExtent.Y),
            presentationExtent,
            backbufferExtent);
        BoundingRectangle left = VulkanRenderer.ScalePresentationRegionToBackbuffer(
            new BoundingRectangle(0, 0, sharedPresentationEdge, presentationExtent.Y),
            presentationExtent,
            backbufferExtent);
        BoundingRectangle right = VulkanRenderer.ScalePresentationRegionToBackbuffer(
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
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");

        presentCommand.ShouldContain("renderer.MapWindowPresentationRegionToBackbuffer(region)");
        viewportRenderArea.ShouldContain("!UseInternalResolution &&");
        viewportRenderArea.ShouldContain("!externalRegion.HasValue &&");
        viewportRenderArea.ShouldContain("!outputRegion.HasValue &&");
        viewportRenderArea.ShouldContain("res = renderer.MapWindowPresentationRegionToBackbuffer(res);");
        imgui.ShouldContain("_swapchainPresentScalingActive &&");
        imgui.ShouldContain("XRWindow.IsInteractiveResizeInProgress");
        imgui.ShouldContain("uint fbWidth = swapChainExtent.Width;");
        imgui.ShouldContain("Vector2 snapshotToRasterScale");
        imgui.ShouldContain("Vector2 clipScale = drawData.FramebufferScale * snapshotToRasterScale;");
    }

    [Test]
    public void FailedRenderResourceGenerationFenceDoesNotPermanentlyBlockRetirementQueue()
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

        failurePath.ShouldContain("RetiredRenderResourceGenerationFenceFailed");
        failurePath.ShouldContain("PrepareForPhysicalResourceDestruction");
        failurePath.ShouldNotContain("return;");
    }

    [Test]
    public void VulkanBlitRegionsClampToLiveSourceAndDestinationExtents()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");

        int buildStart = source.IndexOf("private static bool TryBuildImageBlit", StringComparison.Ordinal);
        buildStart.ShouldBeGreaterThanOrEqualTo(0);
        int transitionStart = source.IndexOf("private void TransitionForBlit", buildStart, StringComparison.Ordinal);
        transitionStart.ShouldBeGreaterThan(buildStart);
        string buildBody = source[buildStart..transitionStart];

        buildBody.ShouldContain("int sourceWidth = (int)Math.Max(source.Extent.Width, 1u);");
        buildBody.ShouldContain("int destinationWidth = (int)Math.Max(destination.Extent.Width, 1u);");
        buildBody.ShouldContain("int srcX0 = ClampBlitOffset(inX, sourceWidth);");
        buildBody.ShouldContain("int srcX1 = ClampBlitOffset((long)inX + inW, sourceWidth);");
        buildBody.ShouldContain("int dstX0 = ClampBlitOffset(outX, destinationWidth);");
        buildBody.ShouldContain("int dstX1 = ClampBlitOffset((long)outX + outW, destinationWidth);");
        buildBody.ShouldContain("if (srcX1 <= srcX0 || srcY1 <= srcY0 || dstX1 <= dstX0 || dstY1 <= dstY0)");
        buildBody.ShouldContain("private static int ClampBlitOffset(long value, int extent)");
    }

    [Test]
    public void WindowPumpHost_StopFlushesMailboxBeforeCompletingQueue()
    {
        string source = ReadWorkspaceFile("XRENGINE/Engine/Engine.WindowPumpHost.cs");
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

        xrWindow.ShouldContain("public IWindow ThreadAffinedNativeWindow { get; }");
        xrWindow.ShouldContain("[EditorBrowsable(EditorBrowsableState.Never)]");
        xrWindow.ShouldContain("public IWindow Window => ThreadAffinedNativeWindow;");
        xrWindow.ShouldContain("public IInputContext? Input { get; private set; }");
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
    public void EditorPreviewTextureApiObjectCreationUsesRenderThreadInvocationService()
    {
        string services = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderSchedulingServices.cs");
        string hostServices = ReadWorkspaceFile("XRENGINE/Engine/Engine.RuntimeRenderingHostServices.cs");
        string editorHelper = ReadWorkspaceFile("XREngine.Editor/Rendering/EditorRenderThread.cs");
        string materialInspector = ReadWorkspaceFile("XREngine.Editor/AssetEditors/XRMaterialInspector.cs");
        string renderPipelineInspector = ReadWorkspaceFile("XREngine.Editor/AssetEditors/RenderPipelineInspector.cs");
        string viewportPanel = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.ViewportPanel.cs");

        services.ShouldContain("T InvokeRenderThreadTask<T>");
        hostServices.ShouldContain("Engine.EnqueueRenderThreadTask(");
        hostServices.ShouldContain("ManualResetEventSlim completed");
        editorHelper.ShouldContain("RuntimeRenderingHostServices.Scheduling.InvokeRenderThreadTask");
        materialInspector.ShouldContain("EditorRenderThread.Invoke(");
        renderPipelineInspector.ShouldContain("EditorRenderThread.Invoke(");
        viewportPanel.ShouldContain("EditorRenderThread.Invoke(");
    }

    [Test]
    public void EditorAndAppWindowAccessUseXRWindowSnapshotsAndMailboxWrappers()
    {
        string xrWindow = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        string fileDrop = ReadWorkspaceFile("XREngine.Editor/EditorFileDropHandler.cs");
        string fileBrowser = ReadWorkspaceFile("XREngine.Editor/UI/ImGuiFileBrowser.cs");
        string statePanel = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.StatePanel.cs");
        string vrState = ReadWorkspaceFile("XRENGINE/Engine/Engine.VRState.cs");

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
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, "XRENGINE.slnx");
            if (File.Exists(candidate))
            {
                string path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
                return File.ReadAllText(path);
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
