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

        public Task<IRuntimeSceneStreamingHandle?> LoadSceneAsync(string sceneAssetPath)
        {
            LoadedPath = sceneAssetPath;
            return _deferredLoad?.Task ?? Task.FromResult<IRuntimeSceneStreamingHandle?>(_handle);
        }

        public bool AttachScene(IRuntimeWorldContext world, IRuntimeSceneStreamingHandle scene)
        {
            AttachCount++;
            AttachedWorld = world;
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

    private sealed class FakeWorld : IRuntimeWorldContext
    {
        public bool IsPlaySessionActive => true;

        public void RegisterTick(ETickGroup group, int order, WorldTick tick) { }
        public void UnregisterTick(ETickGroup group, int order, WorldTick tick) { }
        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject) { }
        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix) { }
    }
}
