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

        source.ShouldContain("keyboard.KeyDown += InputSnapshot_KeyDown;");
        source.ShouldContain("keyboard.KeyUp += InputSnapshot_KeyUp;");
        source.ShouldContain("keyboard.KeyChar += InputSnapshot_KeyChar;");
        source.ShouldContain("mouse.MouseDown += InputSnapshot_MouseDown;");
        source.ShouldContain("mouse.MouseUp += InputSnapshot_MouseUp;");
        source.ShouldContain("mouse.MouseMove += InputSnapshot_MouseMove;");
        source.ShouldContain("mouse.Scroll += InputSnapshot_Scroll;");
        source.ShouldContain("PointerDeltaX");
        source.ShouldContain("ScrollDeltaY");
        source.ShouldContain("TextInputCount");
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
    public void RuntimeLocalPlayerViewport_ExposesInputSnapshotAndQuarantinesThreadAffinedDeviceBinding()
    {
        string contract = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimePlayerViewportContracts.cs");
        string viewport = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRViewport.cs");

        contract.ShouldContain("WindowInputSnapshot InputSnapshot { get; }");
        contract.ShouldContain("GetThreadAffinedDeviceSourceForBinding");
        contract.ShouldNotContain("InputContext { get; }");
        viewport.ShouldContain("IRuntimeLocalPlayerViewport.InputSnapshot");
        viewport.ShouldContain("Window?.LatestWindowInputSnapshot ?? default");
        viewport.ShouldContain("IRuntimeLocalPlayerViewport.GetThreadAffinedDeviceSourceForBinding");
        viewport.ShouldNotContain("IRuntimeLocalPlayerViewport.InputContext");
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
