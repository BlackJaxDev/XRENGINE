using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneDirectorComponentTests
{
    private static readonly Vector3 OriginalPosition = new(7, 8, 9);

    private sealed class FakeResolver(SceneNode node) : ICutsceneBindingResolver
    {
        public SceneNode? FindNodeById(Guid id) => id == node.ID ? node : null;

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    private sealed class TestWorld : IRuntimeWorldContext
    {
        public readonly List<(ETickGroup Group, int Order, WorldTick Tick)> Ticks = [];

        public bool IsPlaySessionActive => false;

        public void RegisterTick(ETickGroup group, int order, WorldTick tick)
            => Ticks.Add((group, order, tick));

        public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
            => Ticks.Remove((group, order, tick));

        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
        {
        }

        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
        {
        }

        public void InvokeAll()
        {
            foreach ((ETickGroup _, int _, WorldTick tick) in Ticks.ToArray())
                tick();
        }
    }

    private sealed class Rig
    {
        public required SceneNode Target { get; init; }
        public required SceneNode Host { get; init; }
        public required CutsceneDirectorComponent Director { get; init; }
        public required TestWorld World { get; init; }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static Rig MakeRig(bool withAsset = true)
    {
        SceneNode target = new("Hero", new Transform());
        ((Transform)target.Transform).Translation = OriginalPosition;

        CutsceneAsset asset = new() { Duration = T(10) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = target.ID });
        TransformTrack track = new() { Name = "Move", BindingSlot = 0, WhenFinished = ECutsceneFinishBehavior.RestoreState };
        track.Clips.Add(new TransformTrackClip
        {
            Start = T(0),
            Duration = T(10),
            Position = new CutsceneVector3Curve { Times = [T(0), T(10)], Values = [Vector3.Zero, new Vector3(10, 0, 0)] },
            Rotation = new CutsceneQuaternionCurve { Times = [T(0), T(10)], Values = [Quaternion.Identity, Quaternion.Identity] },
            Scale = new CutsceneVector3Curve { Times = [T(0), T(10)], Values = [Vector3.One, Vector3.One] },
        });
        asset.Tracks.Add(track);

        SceneNode host = new("Director", new Transform());
        TestWorld world = new();
        host.SetWorldContext(world);
        CutsceneDirectorComponent director = host.AddComponent<CutsceneDirectorComponent>()!;
        if (withAsset)
            director.Asset = asset;
        director.BindingResolver = new FakeResolver(target);
        return new Rig { Target = target, Host = host, Director = director, World = world };
    }

    [Test]
    public async Task SetRuntimeSlot_BeforeOpen_LetsAnimationTrackPlay()
    {
        const string SlotName = "Actor";
        Rig rig = MakeRig();
        SceneNode actor = new("Actor", new Transform());

        PropAnimVector3 position = new(2f, false, true);
        position.Keyframes.Add(new Vector3Keyframe(0f, Vector3.One, Vector3.Zero, EVectorInterpType.Linear));
        AnimationMember transform = new("Transform", EAnimationMemberType.Property);
        transform.Children.Add(new AnimationMember("Translation", EAnimationMemberType.Property, position));
        AnimationMember sceneNode = new("SceneNode", EAnimationMemberType.Property);
        sceneNode.Children.Add(transform);

        CutsceneAsset asset = new() { Duration = T(10) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = SlotName });
        AnimationClipTrack track = new() { Name = "Anim", BindingSlot = 0 };
        track.Clips.Add(new AnimationClipTrackClip { Start = T(0), Duration = T(2), Clip = new AnimationClip(sceneNode) { LengthInSeconds = 2f } });
        asset.Tracks.Add(track);

        // Without a node at the open, the track says which call it needs.
        rig.Director.Asset = asset;
        await rig.Director.OpenAsync();
        rig.Director.Player!.Diagnostics.ShouldContain(d => d.Message.Contains(nameof(CutsceneDirectorComponent.SetRuntimeSlot), StringComparison.Ordinal));
        rig.Director.Close();

        rig.Director.SetRuntimeSlot(SlotName, actor);
        await rig.Director.OpenAsync();
        rig.Director.Diagnostics.ShouldNotContain(d => d.Message.Contains("is empty", StringComparison.Ordinal));
        rig.Director.Player!.Seek(T(1), ESeekIntent.Jump);
        rig.Director.Player.Tick(0);

        Vector3.Distance(((Transform)actor.Transform).Translation, Vector3.One).ShouldBeLessThan(1e-4f);
    }

    [Test]
    public void DirectorTickOrder_IsBetweenInputAndAnimation()
    {
        CutsceneDirectorComponent.DirectorTickOrder.ShouldBe(300000);
        CutsceneDirectorComponent.DirectorTickOrder.ShouldBeGreaterThan((int)ETickOrder.Input);
        CutsceneDirectorComponent.DirectorTickOrder.ShouldBeLessThan((int)ETickOrder.Animation);
    }

    [Test]
    public async Task OpenAsync_RegistersNormalTickAtDirectorOrder_AndCreatesPlayer()
    {
        Rig rig = MakeRig();
        rig.World.Ticks.ShouldBeEmpty();

        await rig.Director.OpenAsync(CancellationToken.None);

        rig.Director.Player.ShouldNotBeNull();
        rig.World.Ticks.Count.ShouldBe(1);
        rig.World.Ticks[0].Group.ShouldBe(ETickGroup.Normal);
        rig.World.Ticks[0].Order.ShouldBe(CutsceneDirectorComponent.DirectorTickOrder);
    }

    [Test]
    public async Task Tick_EvaluatesPlayer_AndCloseRestoresStateAndUnregisters()
    {
        Rig rig = MakeRig();
        await rig.Director.OpenAsync(CancellationToken.None);

        rig.Director.Player!.Seek(T(5), ESeekIntent.Jump);
        rig.World.InvokeAll();
        ((Transform)rig.Target.Transform).Translation.X.ShouldBe(5f, 1e-3f);

        rig.Director.Close();

        ((Transform)rig.Target.Transform).Translation.ShouldBe(OriginalPosition);
        rig.World.Ticks.ShouldBeEmpty();
        rig.Director.Player.ShouldBeNull();
    }

    [Test]
    public async Task Deactivation_ClosesTheDirector()
    {
        Rig rig = MakeRig();
        await rig.Director.OpenAsync(CancellationToken.None);
        rig.Director.Player!.Seek(T(5), ESeekIntent.Jump);
        rig.World.InvokeAll();

        rig.Director.IsActive = false;

        rig.Director.Player.ShouldBeNull();
        rig.World.Ticks.ShouldBeEmpty();
        ((Transform)rig.Target.Transform).Translation.ShouldBe(OriginalPosition);
    }

    [Test]
    public async Task Close_CalledTwice_IsSafe()
    {
        Rig rig = MakeRig();
        await rig.Director.OpenAsync(CancellationToken.None);

        rig.Director.Close();
        Should.NotThrow(rig.Director.Close);
        rig.World.Ticks.ShouldBeEmpty();
    }

    [Test]
    public void Close_BeforeOpen_IsSafe()
    {
        Rig rig = MakeRig();
        Should.NotThrow(rig.Director.Close);
    }

    [Test]
    public async Task OpenAsync_WhileOpen_ClosesTheFirstOpen()
    {
        Rig rig = MakeRig();
        await rig.Director.OpenAsync(CancellationToken.None);
        CutscenePlayer first = rig.Director.Player!;

        await rig.Director.OpenAsync(CancellationToken.None);

        rig.Director.Player.ShouldNotBeNull();
        rig.Director.Player.ShouldNotBeSameAs(first);
        rig.World.Ticks.Count.ShouldBe(1);
    }

    [Test]
    public async Task OpenAsync_WithoutAsset_ReportsDiagnosticAndDoesNotThrow()
    {
        Rig rig = MakeRig(withAsset: false);

        await Should.NotThrowAsync(() => rig.Director.OpenAsync(CancellationToken.None));

        rig.Director.Player.ShouldBeNull();
        rig.Director.Diagnostics.Count.ShouldBeGreaterThan(0);
        rig.World.Ticks.ShouldBeEmpty();
    }

    [Test]
    public async Task OpenAsync_WithCompileErrors_ExposesDiagnostics()
    {
        Rig rig = MakeRig();
        rig.Director.Asset!.Tracks[0].Clips.Add(new TransformTrackClip { Start = T(1), Duration = T(0) });

        await rig.Director.OpenAsync(CancellationToken.None);

        rig.Director.Diagnostics.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task OpenAsync_WithCanceledToken_ThrowsAndLeavesDirectorClosed()
    {
        Rig rig = MakeRig();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Should.ThrowAsync<TaskCanceledException>(() => rig.Director.OpenAsync(cts.Token));

        rig.Director.Player.ShouldBeNull();
        rig.World.Ticks.ShouldBeEmpty();
    }

    [Test]
    public async Task TargetViewport_ReceivesTheCutsceneCameraAndGetsThePreviousOneBackOnClose()
    {
        SceneNode root = new("Cameras", new Transform());
        CameraComponent shot = CreateCamera(root, "Shot");
        CameraComponent previous = CreateCamera(root, "Previous");
        CutsceneAsset asset = new() { Duration = T(4) };
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = shot.SceneNode!.ID });
        CameraCutTrack cut = new() { Name = "Cuts", BindingSlot = 0 };
        cut.Clips.Add(new CameraCutClip { Start = T(0), Duration = T(4), CameraBindingSlot = 0 });
        asset.Tracks.Add(cut);

        SceneNode host = new("Director", new Transform());
        TestWorld world = new();
        host.SetWorldContext(world);
        CutsceneDirectorComponent director = host.AddComponent<CutsceneDirectorComponent>()!;
        director.Asset = asset;
        director.BindingResolver = new FakeResolver(shot.SceneNode!);
        XRViewport viewport = new(null) { AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false, SetRenderPipelineFromCamera = false, CameraComponent = previous };
        director.TargetViewport = viewport;

        await director.OpenAsync(CancellationToken.None);
        director.Player!.Seek(T(1), ESeekIntent.Jump);
        world.InvokeAll();
        viewport.CameraComponent.ShouldBeSameAs(shot);

        director.Close();

        viewport.CameraComponent.ShouldBeSameAs(previous);
        viewport.HasCameraClaim.ShouldBeFalse();
    }

    private static CameraComponent CreateCamera(SceneNode root, string name)
    {
        CameraComponent component = root.NewChild(name).AddComponent<CameraComponent>()!;
        XRCamera camera = new(component.Transform, new XRPerspectiveCameraParameters(60, 1, 0.1f, 100));
        typeof(CameraComponent).GetField("_camera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(component, new Lazy<XRCamera>(() => camera));
        return component;
    }
}
