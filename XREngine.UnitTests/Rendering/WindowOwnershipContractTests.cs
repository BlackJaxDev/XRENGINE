using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

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
    public void XRWindow_CollapsedWindowEventPumpPublishesInputSnapshotAfterDoEvents()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs").Replace("\r\n", "\n");

        int renderStart = source.IndexOf("private void RenderFrame()", StringComparison.Ordinal);
        renderStart.ShouldBeGreaterThanOrEqualTo(0);
        int consumeStart = source.IndexOf("ConsumeLatestWindowSurfaceSnapshotForRenderFrame", renderStart, StringComparison.Ordinal);
        consumeStart.ShouldBeGreaterThan(renderStart);
        string renderPumpBody = source[renderStart..consumeStart];

        renderPumpBody.ShouldContain("Window.DoEvents();\n                PublishWindowInputSnapshot();");

        int endTickStart = source.IndexOf("private void EndTick()", StringComparison.Ordinal);
        endTickStart.ShouldBeGreaterThanOrEqualTo(0);
        int swapStart = source.IndexOf("private void SwapBuffers()", endTickStart, StringComparison.Ordinal);
        swapStart.ShouldBeGreaterThan(endTickStart);
        string endTickBody = source[endTickStart..swapStart];

        endTickBody.ShouldContain("Window.DoEvents();\n                }\n\n                PublishWindowInputSnapshot();");
    }

    [Test]
    public void XRWindow_ConsumedNativeSnapshotsUseCheapOutputResizeBeforeFullInternalGeneration()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        int consumeStart = source.IndexOf("private void ConsumeLatestWindowSurfaceSnapshotForRenderFrame()", StringComparison.Ordinal);
        consumeStart.ShouldBeGreaterThanOrEqualTo(0);
        int nextMethod = source.IndexOf("private void RecordAllRenderExtents", consumeStart, StringComparison.Ordinal);
        nextMethod.ShouldBeGreaterThan(consumeStart);

        string consumeBody = source[consumeStart..nextMethod];

        consumeBody.ShouldContain("ApplyInteractivePresentationResize");
        consumeBody.ShouldContain("QueueFullInternalResize");
        consumeBody.ShouldContain("force: !snapshot.IsInteractiveResize");
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

        retirement.ShouldContain("private readonly List<Framebuffer>[] _retiredFramebuffers");
        retirement.ShouldContain("private readonly List<RetiredImageResources>[] _retiredImages");
        retirement.ShouldContain("internal void RetireFramebuffer(Framebuffer framebuffer)");
        retirement.ShouldContain("internal void RetireImageResources(in RetiredImageResources resources)");
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
    public void VulkanMismatchedSwapchainPresentIsGatedBehindPresentScalingValidation()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");

        source.ShouldContain("private bool CanPresentMismatchedSwapchainExtent(");
        source.ShouldContain("VkSwapchainPresentScalingCreateInfoKHR support is queried");
        source.ShouldContain("return false;");
        source.ShouldContain("bool canPresentMismatchedSwapchainExtent = liveSurfaceValid &&");
        source.ShouldContain("CanPresentMismatchedSwapchainExtent(");
        source.ShouldContain("if (_frameBufferInvalidated || (!surfaceMatchesSwapchain && !canPresentMismatchedSwapchainExtent))");
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
        string services = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderingHostServices.cs");
        string hostServices = ReadWorkspaceFile("XRENGINE/Engine/Engine.RuntimeRenderingHostServices.cs");
        string editorHelper = ReadWorkspaceFile("XREngine.Editor/Rendering/EditorRenderThread.cs");
        string materialInspector = ReadWorkspaceFile("XREngine.Editor/AssetEditors/XRMaterialInspector.cs");
        string renderPipelineInspector = ReadWorkspaceFile("XREngine.Editor/AssetEditors/RenderPipelineInspector.cs");
        string viewportPanel = ReadWorkspaceFile("XREngine.Editor/IMGUI/EditorImGuiUI.ViewportPanel.cs");

        services.ShouldContain("T InvokeRenderThreadTask<T>");
        hostServices.ShouldContain("Engine.EnqueueRenderThreadTask(");
        hostServices.ShouldContain("ManualResetEventSlim completed");
        editorHelper.ShouldContain("RuntimeRenderingHostServices.Current.InvokeRenderThreadTask");
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
        xrWindow.ShouldContain("RuntimeRenderingHostServices.Current.EnqueueWindowThreadTask(");

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
