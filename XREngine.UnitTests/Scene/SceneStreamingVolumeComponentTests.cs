using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Volumes;

namespace XREngine.UnitTests.Scene;

[NonParallelizable]
public sealed class SceneStreamingVolumeComponentTests
{
    private IRuntimeSceneStreamingHostServices _previousHost = null!;
    private IRuntimeThreadServices _previousThreadServices = null!;

    [SetUp]
    public void SetUp()
    {
        _previousHost = RuntimeSceneStreamingHostServices.Current;
        _previousThreadServices = RuntimeThreadServices.Current;
        RuntimeThreadServices.Current = new ImmediateThreadServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeSceneStreamingHostServices.Current = _previousHost;
        RuntimeThreadServices.Current = _previousThreadServices;
    }

    [Test]
    public async Task FirstEnterAttachesAndLastLeaveDetachesInOriginatingWorld()
    {
        FakeStreamingHost host = new();
        RuntimeSceneStreamingHostServices.Current = host;
        FakeWorld world = new();
        TestStreamingVolume volume = CreateVolume(world);
        TestComponent other = new();

        volume.Enter(other);
        await WaitUntilAsync(() => host.AttachCount == 1);

        host.LoadedPath.ShouldBe("Scenes/Test.scene");
        host.AttachedWorld.ShouldBeSameAs(world);

        volume.Leave(other);

        host.DetachCount.ShouldBe(1);
        host.DetachedWorld.ShouldBeSameAs(world);
    }

    [Test]
    public async Task LeavingBeforeLoadCompletesCancelsPendingAttach()
    {
        FakeStreamingHost host = new(deferLoad: true);
        RuntimeSceneStreamingHostServices.Current = host;
        TestStreamingVolume volume = CreateVolume(new FakeWorld());
        TestComponent other = new();

        volume.Enter(other);
        volume.Leave(other);
        host.CompleteDeferredLoad();
        await Task.Delay(20);

        host.AttachCount.ShouldBe(0);
        host.DetachCount.ShouldBe(0);
    }

    [Test]
    public async Task ChangingWorldBeforeLoadCompletesRejectsAttach()
    {
        FakeStreamingHost host = new(deferLoad: true);
        RuntimeSceneStreamingHostServices.Current = host;
        FakeWorld originalWorld = new();
        TestStreamingVolume volume = CreateVolume(originalWorld);

        volume.Enter(new TestComponent());
        volume.SetWorldContext(new FakeWorld());
        host.CompleteDeferredLoad();
        await Task.Delay(20);

        host.AttachCount.ShouldBe(0);
    }

    [Test]
    public async Task DirectDestroyRejectsDeferredLoadAndDetachesOriginatingHost()
    {
        FakeStreamingHost deferredHost = new(deferLoad: true);
        RuntimeSceneStreamingHostServices.Current = deferredHost;
        QueuedThreadServices queuedThread = new();
        RuntimeThreadServices.Current = queuedThread;
        TestStreamingVolume pendingVolume = CreateVolume(new FakeWorld());

        pendingVolume.Enter(new TestComponent());
        pendingVolume.Destroy(true);
        deferredHost.CompleteDeferredLoad();
        await queuedThread.WaitForActionAsync();
        queuedThread.Drain();
        deferredHost.AttachCount.ShouldBe(0);

        FakeStreamingHost originalHost = new();
        RuntimeSceneStreamingHostServices.Current = originalHost;
        RuntimeThreadServices.Current = new ImmediateThreadServices();
        FakeWorld originalWorld = new();
        TestStreamingVolume loadedVolume = CreateVolume(originalWorld);
        loadedVolume.Enter(new TestComponent());
        originalHost.AttachCount.ShouldBe(1);

        RuntimeSceneStreamingHostServices.Current = new FakeStreamingHost();
        loadedVolume.Destroy(true);
        originalHost.DetachCount.ShouldBe(1);
        originalHost.DetachedWorld.ShouldBeSameAs(originalWorld);
    }

    [Test]
    public async Task QueuedDestroyRejectsCompletedLoadBeforeDestructionRuns()
    {
        FakeStreamingHost host = new(deferLoad: true);
        RuntimeSceneStreamingHostServices.Current = host;
        QueuedThreadServices queuedThread = new();
        RuntimeThreadServices.Current = queuedThread;
        TestStreamingVolume volume = CreateVolume(new FakeWorld());

        volume.Enter(new TestComponent());
        host.CompleteDeferredLoad();
        await queuedThread.WaitForActionAsync();
        volume.Destroy();
        volume.IsDestroyQueued.ShouldBeTrue();
        queuedThread.Drain();
        host.AttachCount.ShouldBe(0);
        volume.Destroy(true);
    }

    [Test]
    public void DestroyDuringHostAttachRetiresTheJustAttachedScene()
    {
        FakeStreamingHost host = new();
        RuntimeSceneStreamingHostServices.Current = host;
        FakeWorld world = new();
        TestStreamingVolume volume = CreateVolume(world);
        host.OnAttach = () => volume.Destroy(true);

        volume.Enter(new TestComponent());

        volume.IsDestroyed.ShouldBeTrue();
        host.AttachCount.ShouldBe(1);
        host.DetachCount.ShouldBe(1);
        host.DetachedWorld.ShouldBeSameAs(world);
    }

    private static TestStreamingVolume CreateVolume(FakeWorld world)
    {
        TestStreamingVolume volume = new()
        {
            SceneAssetPath = "Scenes/Test.scene"
        };
        volume.SetWorldContext(world);
        return volume;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 50 && !condition(); i++)
            await Task.Delay(5);

        condition().ShouldBeTrue();
    }

    private sealed class TestStreamingVolume : SceneStreamingVolumeComponent
    {
        public void Enter(XRComponent component)
        {
            OverlappingComponents.Add(component);
            OnEntered(component);
        }

        public void Leave(XRComponent component)
        {
            OverlappingComponents.Remove(component);
            OnLeft(component);
        }
    }

    private sealed class TestComponent : XRComponent;

    private sealed class FakeSceneHandle : IRuntimeSceneStreamingHandle;

    private sealed class FakeStreamingHost : IRuntimeSceneStreamingHostServices
    {
        private readonly TaskCompletionSource<IRuntimeSceneStreamingHandle?>? _deferredLoad;
        private readonly IRuntimeSceneStreamingHandle _handle = new FakeSceneHandle();

        public FakeStreamingHost(bool deferLoad = false)
        {
            if (deferLoad)
                _deferredLoad = new TaskCompletionSource<IRuntimeSceneStreamingHandle?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string? LoadedPath { get; private set; }
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }
        public IRuntimeWorldContext? AttachedWorld { get; private set; }
        public IRuntimeWorldContext? DetachedWorld { get; private set; }
        public Action? OnAttach { get; set; }

        public Task<IRuntimeSceneStreamingHandle?> LoadSceneAsync(string sceneAssetPath)
        {
            LoadedPath = sceneAssetPath;
            return _deferredLoad?.Task ?? Task.FromResult<IRuntimeSceneStreamingHandle?>(_handle);
        }

        public bool AttachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene)
        {
            AttachCount++;
            AttachedWorld = world;
            OnAttach?.Invoke();
            return true;
        }

        public bool DetachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene)
        {
            DetachCount++;
            DetachedWorld = world;
            return true;
        }

        public void CompleteDeferredLoad()
            => _deferredLoad!.SetResult(_handle);
    }

    private sealed class ImmediateThreadServices : IRuntimeThreadServices
    {
        public bool InvokeOnAppThread(
            Action action,
            string? reason = null,
            bool executeNowIfAlreadyAppThread = false)
        {
            action();
            return true;
        }
    }

    private sealed class QueuedThreadServices : IRuntimeThreadServices
    {
        private readonly Queue<Action> _actions = new();
        private readonly TaskCompletionSource<bool> _queued = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool InvokeOnAppThread(
            Action action,
            string? reason = null,
            bool executeNowIfAlreadyAppThread = false)
        {
            lock (_actions)
                _actions.Enqueue(action);
            _queued.TrySetResult(true);
            return true;
        }

        public Task WaitForActionAsync()
            => _queued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Drain()
        {
            while (true)
            {
                Action action;
                lock (_actions)
                {
                    if (_actions.Count == 0)
                        return;
                    action = _actions.Dequeue();
                }
                action();
            }
        }
    }

    private sealed class FakeWorld : IRuntimeWorldContext
    {
        public bool IsPlaySessionActive => true;

        public void RegisterTick(ETickGroup group, int order, WorldTick tick) { }
        public void UnregisterTick(ETickGroup group, int order, WorldTick tick) { }
        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject) { }
        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix) { }
    }
}
