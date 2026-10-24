using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIMediaTeardownTests
{
    [Test]
    public void WebOwnerFailureIsPublicAndInvalidatesVisualOncePerReason()
    {
        using UITestShaderScope shaders = new();
        (IRuntimeRenderingHostServices host, UIVideoDeferredRenderHost state) =
            UIVideoDeferredRenderHost.CreateHost();
        state.CaptureAppTasks = true;
        using IDisposable installation = RuntimeRenderingHostServices.Install(host);
        UIBoundableTransform transform = new();
        SceneNode root = new("Web owner diagnostic", transform) { IsActiveSelf = false };
        try
        {
            root.SetWorldContext(new TestWorldContext());
            UIWebViewComponent web = root.AddComponent<UIWebViewComponent>()!;
            web.Backend = new UIWebFakeScopedBackend();
            root.IsActiveSelf = true;
            state.DrainAppTasks();
            long beforeVisual = transform.ChangeRevisions.Visual;

            web.RenderWebFrameForTest();
            web.LastBackendFailure.ShouldNotBeNull().ShouldContain("UI.Web.OutputOwnerUnavailable");
            state.QueuedAppTasks.ShouldBe(1);
            web.RenderWebFrameForTest();
            state.QueuedAppTasks.ShouldBe(1);

            state.DrainAppTasks();
            transform.ChangeRevisions.Visual.ShouldBe(beforeVisual + 1);
        }
        finally
        {
            root.Destroy(true);
            state.DrainAppTasks();
            state.DrainRenderTasks();
        }
    }

    [Test]
    public void WebReactivationCancelsQueuedSameBackendRetirement()
    {
        using UITestShaderScope shaders = new();
        (IRuntimeRenderingHostServices host, UIVideoDeferredRenderHost state) =
            UIVideoDeferredRenderHost.CreateHost();
        using IDisposable installation = RuntimeRenderingHostServices.Install(host);
        int previousRenderThread = RuntimeEngine.RenderThreadId;
        SceneNode root = new("Web reactivation", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            RuntimeEngine.AssignRenderThread(-1);
            root.SetWorldContext(new TestWorldContext());
            UIWebViewComponent web = root.AddComponent<UIWebViewComponent>()!;
            UIWebFakeBackend backend = new();
            web.Backend = backend;
            root.IsActiveSelf = true;
            state.DrainRenderTasks();
            web.RenderWebFrameForTest();
            backend.InitializeCount.ShouldBe(1);

            root.IsActiveSelf = false;
            root.IsActiveSelf = true;
            state.DrainRenderTasks();
            backend.DisposeCount.ShouldBe(0);
            web.RenderWebFrameForTest();
            backend.InitializeCount.ShouldBe(2);
        }
        finally
        {
            root.Destroy(true);
            state.DrainRenderTasks();
            RuntimeEngine.AssignRenderThread(previousRenderThread);
        }
    }

    [Test]
    public void WebInitializeReplacementDoesNotPublishRetiredFailure()
    {
        using UITestShaderScope shaders = new();
        (IRuntimeRenderingHostServices host, UIVideoDeferredRenderHost state) =
            UIVideoDeferredRenderHost.CreateHost();
        using IDisposable installation = RuntimeRenderingHostServices.Install(host);
        int previousRenderThread = RuntimeEngine.RenderThreadId;
        SceneNode root = new("Web initialization replacement", new UIBoundableTransform())
        {
            IsActiveSelf = false
        };
        try
        {
            RuntimeEngine.AssignRenderThread(-1);
            root.SetWorldContext(new TestWorldContext());
            UIWebViewComponent web = root.AddComponent<UIWebViewComponent>()!;
            UIWebFakeBackend first = new();
            UIWebFakeBackend replacement = new();
            first.OnInitialize = () =>
            {
                web.Backend = replacement;
                throw new InvalidOperationException("Retired initialization failed.");
            };
            web.Backend = first;
            root.IsActiveSelf = true;
            state.DrainRenderTasks();

            web.RenderWebFrameForTest();
            web.Backend.ShouldBeSameAs(replacement);
            web.LastBackendFailure.ShouldBeNull();
            state.DrainRenderTasks();

            web.RenderWebFrameForTest();
            replacement.InitializeCount.ShouldBe(1);
            replacement.RenderCount.ShouldBe(1);
            web.LastBackendFailure.ShouldBeNull();
        }
        finally
        {
            root.Destroy(true);
            state.DrainRenderTasks();
            RuntimeEngine.AssignRenderThread(previousRenderThread);
        }
    }

    [Test]
    public void WebBackendFailureClearsWhenBackendChanges()
    {
        using UITestShaderScope shaders = new();
        (IRuntimeRenderingHostServices host, UIVideoDeferredRenderHost state) =
            UIVideoDeferredRenderHost.CreateHost();
        using IDisposable installation = RuntimeRenderingHostServices.Install(host);
        int previousRenderThread = RuntimeEngine.RenderThreadId;
        SceneNode root = new("Web backend failure", new UIBoundableTransform())
        {
            IsActiveSelf = false
        };
        try
        {
            RuntimeEngine.AssignRenderThread(-1);
            root.SetWorldContext(new TestWorldContext());
            UIWebViewComponent web = root.AddComponent<UIWebViewComponent>()!;
            UIWebFakeBackend failed = new()
            {
                OnInitialize = () => throw new InvalidOperationException("Expected initialization failure.")
            };
            UIWebFakeBackend replacement = new();
            web.Backend = failed;
            root.IsActiveSelf = true;
            state.DrainRenderTasks();

            web.RenderWebFrameForTest();
            web.LastBackendFailure.ShouldNotBeNull().ShouldContain("Expected initialization failure.");

            web.Backend = replacement;
            web.LastBackendFailure.ShouldBeNull();
            state.DrainRenderTasks();
            web.RenderWebFrameForTest();
            replacement.RenderCount.ShouldBe(1);
            web.LastBackendFailure.ShouldBeNull();
        }
        finally
        {
            root.Destroy(true);
            state.DrainRenderTasks();
            RuntimeEngine.AssignRenderThread(previousRenderThread);
        }
    }

    [Test]
    public void WebReplacementDuringUpdateCannotPublishOrRenderOldBackend()
    {
        using UITestShaderScope shaders = new();
        (IRuntimeRenderingHostServices host, UIVideoDeferredRenderHost state) =
            UIVideoDeferredRenderHost.CreateHost();
        using IDisposable installation = RuntimeRenderingHostServices.Install(host);
        int previousRenderThread = RuntimeEngine.RenderThreadId;
        SceneNode root = new("Web replacement", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            RuntimeEngine.AssignRenderThread(-1);
            root.SetWorldContext(new TestWorldContext());
            UIWebViewComponent web = root.AddComponent<UIWebViewComponent>()!;
            UIWebFakeBackend first = new();
            UIWebFakeBackend second = new();
            web.Backend = first;
            root.IsActiveSelf = true;
            state.DrainRenderTasks();
            first.OnUpdate = () => web.Backend = second;
            web.RenderWebFrameForTest();

            first.RenderCount.ShouldBe(0);
            second.RenderCount.ShouldBe(0);
            first.DisposeCount.ShouldBe(0);
            state.DrainRenderTasks();
            first.DisposeCount.ShouldBe(1);
            web.RenderWebFrameForTest();
            second.InitializeCount.ShouldBe(1);
            second.RenderCount.ShouldBe(1);
        }
        finally
        {
            root.Destroy(true);
            state.DrainRenderTasks();
            RuntimeEngine.AssignRenderThread(previousRenderThread);
        }
    }

    [Test]
    public void WebQueuedReplacementPreservesReattachedBackendAndRetiresIntermediate()
    {
        using UITestShaderScope shaders = new();
        (IRuntimeRenderingHostServices host, UIVideoDeferredRenderHost state) =
            UIVideoDeferredRenderHost.CreateHost();
        using IDisposable installation = RuntimeRenderingHostServices.Install(host);
        int previousRenderThread = RuntimeEngine.RenderThreadId;
        SceneNode root = new("Web backend reuse", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            RuntimeEngine.AssignRenderThread(-1);
            root.SetWorldContext(new TestWorldContext());
            UIWebViewComponent web = root.AddComponent<UIWebViewComponent>()!;
            UIWebFakeBackend first = new();
            UIWebFakeBackend intermediate = new();
            web.Backend = first;
            root.IsActiveSelf = true;
            state.DrainRenderTasks();
            web.RenderWebFrameForTest();

            web.Backend = intermediate;
            web.Backend = first;
            state.DrainRenderTasks();
            first.DisposeCount.ShouldBe(0);
            intermediate.DisposeCount.ShouldBe(1);
            web.RenderWebFrameForTest();
            first.RenderCount.ShouldBe(2);
        }
        finally
        {
            root.Destroy(true);
            state.DrainRenderTasks();
            RuntimeEngine.AssignRenderThread(previousRenderThread);
        }
    }

    [Test]
    public void WebDisposeCallbackCannotRenderTheClaimedBackend()
    {
        using UITestShaderScope shaders = new();
        (IRuntimeRenderingHostServices host, UIVideoDeferredRenderHost state) =
            UIVideoDeferredRenderHost.CreateHost();
        using IDisposable installation = RuntimeRenderingHostServices.Install(host);
        int previousRenderThread = RuntimeEngine.RenderThreadId;
        SceneNode root = new("Web disposal reentry", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            RuntimeEngine.AssignRenderThread(-1);
            root.SetWorldContext(new TestWorldContext());
            UIWebViewComponent web = root.AddComponent<UIWebViewComponent>()!;
            UIWebFakeBackend first = new();
            UIWebFakeBackend second = new();
            web.Backend = first;
            root.IsActiveSelf = true;
            state.DrainRenderTasks();
            web.RenderWebFrameForTest();
            int before = first.UpdateCount;

            first.OnDispose = () =>
            {
                first.OnDispose = null;
                web.Backend = first;
                web.RenderWebFrameForTest();
                first.UpdateCount.ShouldBe(before);
            };
            web.Backend = second;
            state.DrainRenderTasks();
            first.DisposeCount.ShouldBe(1);
            web.RenderWebFrameForTest();
            first.UpdateCount.ShouldBe(before + 1);
        }
        finally
        {
            root.Destroy(true);
            state.DrainRenderTasks();
            RuntimeEngine.AssignRenderThread(previousRenderThread);
        }
    }

    [Test]
    public void WebFailedBackendRetirementDoesNotStopReplacement()
    {
        using UITestShaderScope shaders = new();
        (IRuntimeRenderingHostServices host, UIVideoDeferredRenderHost state) =
            UIVideoDeferredRenderHost.CreateHost();
        using IDisposable installation = RuntimeRenderingHostServices.Install(host);
        int previousRenderThread = RuntimeEngine.RenderThreadId;
        SceneNode root = new("Web failed disposal", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            RuntimeEngine.AssignRenderThread(-1);
            root.SetWorldContext(new TestWorldContext());
            UIWebViewComponent web = root.AddComponent<UIWebViewComponent>()!;
            UIWebFakeBackend failed = new() { ThrowOnDispose = true };
            UIWebFakeBackend replacement = new();
            web.Backend = failed;
            root.IsActiveSelf = true;
            state.DrainRenderTasks();
            web.RenderWebFrameForTest();

            web.Backend = replacement;
            state.DrainRenderTasks();
            failed.DisposeCount.ShouldBe(1);
            web.RenderWebFrameForTest();
            replacement.RenderCount.ShouldBe(1);
            failed.DisposeCount.ShouldBe(1);
        }
        finally
        {
            root.Destroy(true);
            state.DrainRenderTasks();
            RuntimeEngine.AssignRenderThread(previousRenderThread);
        }
    }

    [Test]
    public void VideoSubtreeDeactivationAndDirectDestroyRejectOldQueuedStarts()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Video teardown", new UIBoundableTransform())
        {
            IsActiveSelf = false
        };
        try
        {
            root.SetWorldContext(new TestWorldContext());
            UIVideoComponent video = root.AddComponent<UIVideoComponent>()!;
            video.StreamUrl = null;

            root.IsActiveSelf = true;
            long firstStart = video.StreamingLifecycleEpoch;
            video.IsCurrentStreamingLifecycle(firstStart).ShouldBeTrue();

            root.IsActiveSelf = false;
            video.IsCurrentStreamingLifecycle(firstStart).ShouldBeFalse();

            root.IsActiveSelf = true;
            long secondStart = video.StreamingLifecycleEpoch;
            secondStart.ShouldBeGreaterThan(firstStart);
            video.IsCurrentStreamingLifecycle(firstStart).ShouldBeFalse();
            video.IsCurrentStreamingLifecycle(secondStart).ShouldBeTrue();

            video.Destroy(true);
            video.IsCurrentStreamingLifecycle(secondStart).ShouldBeFalse();
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void PropertyTextWatcherStopsPollingOnSubtreeDeactivationAndDirectDestroy()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Property text teardown", new UIBoundableTransform())
        {
            IsActiveSelf = false
        };
        try
        {
            root.SetWorldContext(new TestWorldContext());
            root.AddComponent<UITextComponent>();
            UIPropertyTextDriverComponent watcher =
                root.AddComponent<UIPropertyTextDriverComponent>()!;

            root.IsActiveSelf = true;
            watcher.IsPolling.ShouldBeTrue();
            root.IsActiveSelf = false;
            watcher.IsPolling.ShouldBeFalse();
            root.IsActiveSelf = true;
            watcher.IsPolling.ShouldBeTrue();

            watcher.Destroy(true);
            watcher.IsPolling.ShouldBeFalse();
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public async Task BlankUrlClosesPendingSessionAndRejectsDelayedOpenFailure()
    {
        using UITestShaderScope shaders = new();
        RuntimeRenderingHostServices.FrameTiming.IsAppThread.ShouldBeTrue();
        SceneNode root = new("Pending video open", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            root.SetWorldContext(new TestWorldContext());
            UIVideoComponent video = root.AddComponent<UIVideoComponent>()!;
            video.StreamUrl = null;
            root.IsActiveSelf = true;

            UIVideoFakeMediaSession session = new();
            video.AttachStreamingSession(session, "https://example.invalid/media.m3u8",
                new StreamOpenOptions(), video.StreamingLifecycleEpoch);
            session.OpenCount.ShouldBe(1);
            Task completionDispatch = video.StreamingOpenCompletionTask!;

            video.StreamUrl = "  ";
            session.OpenToken.IsCancellationRequested.ShouldBeTrue();
            session.CloseCount.ShouldBe(1);
            session.DisposeCount.ShouldBe(1);

            session.FailOpen();
            await completionDispatch.WaitAsync(TimeSpan.FromSeconds(5));
            session.OpenCount.ShouldBe(1);
            session.CloseCount.ShouldBe(1);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void VideoGpuActionsRetireOnRenderOwnerWithoutTouchingReplacement()
    {
        using UITestShaderScope shaders = new();
        (IRuntimeRenderingHostServices host, UIVideoDeferredRenderHost state) =
            UIVideoDeferredRenderHost.CreateHost();
        using IDisposable installation = RuntimeRenderingHostServices.Install(host);
        int previousRenderThread = RuntimeEngine.RenderThreadId;
        SceneNode root = new("Video GPU retirement", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            RuntimeEngine.AssignRenderThread(-1);
            root.SetWorldContext(new TestWorldContext());
            UIVideoComponent video = root.AddComponent<UIVideoComponent>()!;
            video.StreamUrl = null;
            root.IsActiveSelf = true;

            UIVideoFakeGpuActions first = new();
            UIVideoFakeGpuActions replacement = new();
            video.AttachVideoGpuActions(first);
            video.StreamUrl = " ";
            first.DisposeCount.ShouldBe(0);
            state.QueuedRenderTasks.ShouldBeGreaterThan(0);

            video.AttachVideoGpuActions(replacement);
            state.DrainRenderTasks();
            first.DisposeCount.ShouldBe(1);
            replacement.DisposeCount.ShouldBe(0);

            video.Destroy(true);
            state.DrainRenderTasks();
            replacement.DisposeCount.ShouldBe(1);
        }
        finally
        {
            root.Destroy(true);
            state.DrainRenderTasks();
            RuntimeEngine.AssignRenderThread(previousRenderThread);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReentrantCancellationOrCloseCannotResetReplacementSession(bool replaceOnClose)
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Video reentrant replacement", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            root.SetWorldContext(new TestWorldContext());
            UIVideoComponent video = root.AddComponent<UIVideoComponent>()!;
            video.StreamUrl = null;
            root.IsActiveSelf = true;

            UIVideoFakeMediaSession old = new();
            UIVideoFakeMediaSession replacement = new();
            video.AttachStreamingSession(old, "https://example.invalid/old.m3u8",
                new StreamOpenOptions(), video.StreamingLifecycleEpoch);
            Action attachReplacement = () => video.AttachStreamingSession(replacement,
                "https://example.invalid/new.m3u8", new StreamOpenOptions(), video.StreamingLifecycleEpoch);
            if (replaceOnClose)
                old.OnClose = attachReplacement;
            else
                old.OpenToken.Register(attachReplacement);

            video.StreamUrl = " ";

            old.CloseCount.ShouldBe(1);
            old.DisposeCount.ShouldBe(1);
            replacement.OpenCount.ShouldBe(1);
            replacement.CloseCount.ShouldBe(0);
            replacement.OpenToken.IsCancellationRequested.ShouldBeFalse();
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void ThrowingSessionCloseStillDisposesSessionAndGpuActions()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Video throwing close", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            root.SetWorldContext(new TestWorldContext());
            UIVideoComponent video = root.AddComponent<UIVideoComponent>()!;
            video.StreamUrl = null;
            root.IsActiveSelf = true;

            UIVideoFakeMediaSession session = new() { ThrowOnClose = true };
            UIVideoFakeGpuActions actions = new();
            video.AttachStreamingSession(session, "https://example.invalid/media.m3u8",
                new StreamOpenOptions(), video.StreamingLifecycleEpoch);
            video.AttachVideoGpuActions(actions);

            Should.Throw<InvalidOperationException>(() => video.StreamUrl = " ");
            session.CloseCount.ShouldBe(1);
            session.DisposeCount.ShouldBe(1);
            actions.DisposeCount.ShouldBe(1);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void DirectDestroyReleasesVideoSurfacesEvenWhenSessionCloseThrows()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Video destroy failure", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            root.SetWorldContext(new TestWorldContext());
            UIVideoComponent video = root.AddComponent<UIVideoComponent>()!;
            video.StreamUrl = null;
            root.IsActiveSelf = true;
            UIVideoFakeMediaSession session = new() { ThrowOnClose = true };
            UIVideoFakeGpuActions actions = new();
            video.AttachStreamingSession(session, "https://example.invalid/media.m3u8",
                new StreamOpenOptions(), video.StreamingLifecycleEpoch);
            video.AttachVideoGpuActions(actions);

            Should.Throw<InvalidOperationException>(() => video.Destroy(true));
            video.VideoSurfacesReleased.ShouldBeTrue();
            session.DisposeCount.ShouldBe(1);
            actions.DisposeCount.ShouldBe(1);

            // The native failure prevented the base IsDestroyed transition;
            // a retry completes it without reclaiming resources twice.
            video.Destroy(true);
            video.IsDestroyed.ShouldBeTrue();
        }
        finally
        {
            root.Destroy(true);
        }
    }
}
