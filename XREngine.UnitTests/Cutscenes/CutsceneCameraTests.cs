using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture, NonParallelizable]
public sealed class CutsceneCameraTests
{
    private sealed class FakeResolver : ICutsceneBindingResolver
    {
        public readonly Dictionary<Guid, SceneNode> ById = [];

        public SceneNode? FindNodeById(Guid id) => ById.GetValueOrDefault(id);

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    /// <summary>A player, three camera nodes in slots 0 to 2, and a node without a camera in slot 3.</summary>
    private sealed class Rig : IDisposable
    {
        public required SceneNode Root { get; init; }
        public required CutscenePlayer Player { get; init; }
        public required CutsceneBindingTable Table { get; init; }
        public required CameraComponent[] Cameras { get; init; }

        public void Dispose()
        {
            Table.Dispose();
            Root.Destroy(true);
        }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static CameraCutClip Shot(double start, double duration, int slot)
        => new() { Start = T(start), Duration = T(duration), CameraBindingSlot = slot };

    private static CameraComponent CreateCamera(SceneNode root, string name, float fieldOfView = 60f, bool perspective = true)
    {
        CameraComponent component = root.NewChild(name).AddComponent<CameraComponent>()!;
        XRCameraParameters parameters = perspective
            ? new XRPerspectiveCameraParameters(fieldOfView, 1, 0.1f, 100)
            : new XROrthographicCameraParameters(10, 10, 0.1f, 100);
        XRCamera camera = new(component.Transform, parameters);
        typeof(CameraComponent).GetField("_camera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(component, new Lazy<XRCamera>(() => camera));
        return component;
    }

    private static XRViewport CreateViewport(CameraComponent? camera = null)
        => new(null) { AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false, SetRenderPipelineFromCamera = false, CameraComponent = camera };

    /// <summary>Builds a cutscene with shots at 0-2 s (slot 0), 2-4 s (slot 1), and 4-6 s (slot 2).</summary>
    private static Rig MakeRig(
        IEnumerable<CutsceneClip>? shots = null,
        IEnumerable<CutsceneClip>? propertyClips = null,
        int propertySlot = 0,
        bool perspective = true,
        bool lastIsPlainNode = false)
    {
        SceneNode root = new("Cameras", new Transform());
        CameraComponent[] cameras =
        [
            CreateCamera(root, "Camera A", 60f, perspective),
            CreateCamera(root, "Camera B", 70f),
            CreateCamera(root, "Camera C", 80f),
        ];

        FakeResolver resolver = new();
        CutsceneAsset asset = new() { Duration = T(6) };
        for (int i = 0; i < cameras.Length; i++)
        {
            resolver.ById[cameras[i].SceneNode!.ID] = cameras[i].SceneNode!;
            asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = cameras[i].SceneNode!.ID });
        }
        SceneNode plain = root.NewChild("No camera");
        resolver.ById[plain.ID] = plain;
        asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = plain.ID });

        CameraCutTrack cut = new() { Name = "Cuts", BindingSlot = 0 };
        cut.Clips.AddRange(shots ?? [Shot(0, 2, 0), Shot(2, 2, 1), Shot(4, 2, lastIsPlainNode ? 3 : 2)]);
        asset.Tracks.Add(cut);

        if (propertyClips is not null)
        {
            CameraPropertyTrack property = new() { Name = "Lens", BindingSlot = propertySlot };
            property.Clips.AddRange(propertyClips);
            asset.Tracks.Add(property);
        }

        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, []);
        CutsceneBindingTable table = CutsceneBindingTable.Resolve(compiled, resolver);
        CutscenePlayer player = new(compiled, table, new CutsceneStateSnapshot(table));
        return new Rig { Root = root, Player = player, Table = table, Cameras = cameras };
    }

    private static void SeekAndTick(CutscenePlayer player, double seconds)
    {
        player.Seek(T(seconds), ESeekIntent.Jump);
        player.Tick(0);
    }

    [Test]
    public void Cut_ChangesViewportActiveCamera()
    {
        using Rig rig = MakeRig();
        XRViewport viewport = CreateViewport();
        CutsceneCameraRouter router = new(rig.Player);
        router.Attach(viewport);

        rig.Player.Play();
        rig.Player.Tick(0);
        viewport.ActiveCamera.ShouldBeSameAs(rig.Cameras[0].Camera);

        rig.Player.Tick(2.5);
        viewport.ActiveCamera.ShouldBeSameAs(rig.Cameras[1].Camera);
        viewport.CameraComponent.ShouldBeSameAs(rig.Cameras[1]);

        rig.Player.Tick(2.0);
        viewport.ActiveCamera.ShouldBeSameAs(rig.Cameras[2].Camera);
    }

    [Test]
    public void Cut_RaisesOneDiscontinuityAndInvalidatesHistoryOnce()
    {
        using Rig rig = MakeRig();
        XRViewport viewport = CreateViewport();
        CutsceneCameraRouter router = new(rig.Player);
        router.Attach(viewport);
        rig.Player.Play();
        rig.Player.Tick(0);

        int discontinuities = 0;
        rig.Player.Discontinuity += _ => discontinuities++;
        ulong epochBefore = rig.Cameras[1].Camera.TemporalHistoryEpoch;

        rig.Player.Tick(2.5);

        discontinuities.ShouldBe(1);
        rig.Cameras[1].Camera.TemporalHistoryEpoch.ShouldBe(epochBefore + 1UL);
    }

    [Test]
    public void PlaybackInsideOneShot_RaisesNoDiscontinuity()
    {
        using Rig rig = MakeRig();
        XRViewport viewport = CreateViewport();
        new CutsceneCameraRouter(rig.Player).Attach(viewport);
        rig.Player.Play();
        rig.Player.Tick(0);

        int discontinuities = 0;
        rig.Player.Discontinuity += _ => discontinuities++;
        rig.Player.Tick(0.5);
        rig.Player.Tick(0.5);

        discontinuities.ShouldBe(0);
    }

    [Test]
    public void SeekAcrossCuts_SelectsTheShotAndRaisesOneDiscontinuity()
    {
        using Rig rig = MakeRig();
        XRViewport viewport = CreateViewport();
        CutsceneCameraRouter router = new(rig.Player);
        router.Attach(viewport);

        int discontinuities = 0;
        rig.Player.Discontinuity += _ => discontinuities++;
        ulong epochBefore = rig.Cameras[2].Camera.TemporalHistoryEpoch;

        SeekAndTick(rig.Player, 5.0);

        viewport.ActiveCamera.ShouldBeSameAs(rig.Cameras[2].Camera);
        discontinuities.ShouldBe(1);
        rig.Cameras[2].Camera.TemporalHistoryEpoch.ShouldBe(epochBefore + 1UL);

        SeekAndTick(rig.Player, 1.0);
        viewport.ActiveCamera.ShouldBeSameAs(rig.Cameras[0].Camera);
        SeekAndTick(rig.Player, 3.0);
        viewport.ActiveCamera.ShouldBeSameAs(rig.Cameras[1].Camera);
        discontinuities.ShouldBe(3);
    }

    [Test]
    public void OverlappingShots_HighestWeightWinsAndLatestStartWinsATie()
    {
        // The shots overlap from 1.5 s to 2.5 s with a crossfade, so the weights meet at 2.0 s.
        CameraCutClip first = Shot(0, 2.5, 0);
        first.BlendOut = T(1);
        CameraCutClip second = Shot(1.5, 2.5, 1);
        second.BlendIn = T(1);
        using Rig rig = MakeRig([first, second]);

        SeekAndTick(rig.Player, 1.75);
        rig.Player.ActiveCamera.ShouldBeSameAs(rig.Cameras[0]);
        SeekAndTick(rig.Player, 2.0);
        rig.Player.ActiveCamera.ShouldBeSameAs(rig.Cameras[1]);
        SeekAndTick(rig.Player, 2.25);
        rig.Player.ActiveCamera.ShouldBeSameAs(rig.Cameras[1]);
    }

    [Test]
    public void Detach_RestoresThePreviousCameraAndStopsRouting()
    {
        using Rig rig = MakeRig();
        CameraComponent previous = CreateCamera(rig.Root, "Previous");
        XRViewport viewport = CreateViewport(previous);
        CutsceneCameraRouter router = new(rig.Player);
        router.Attach(viewport);
        SeekAndTick(rig.Player, 3.0);
        viewport.CameraComponent.ShouldBeSameAs(rig.Cameras[1]);

        router.Detach();

        viewport.CameraComponent.ShouldBeSameAs(previous);
        viewport.HasCameraClaim.ShouldBeFalse();
        SeekAndTick(rig.Player, 5.0);
        viewport.CameraComponent.ShouldBeSameAs(previous);
    }

    [Test]
    public void Detach_WithNoPreviousCameraLeavesTheViewportEmpty()
    {
        using Rig rig = MakeRig();
        XRViewport viewport = CreateViewport();
        CutsceneCameraRouter router = new(rig.Player);
        router.Attach(viewport);
        SeekAndTick(rig.Player, 3.0);

        router.Detach();

        viewport.CameraComponent.ShouldBeNull();
    }

    [Test]
    public void Stop_ReturnsThePreviousCamera()
    {
        using Rig rig = MakeRig();
        CameraComponent previous = CreateCamera(rig.Root, "Previous");
        XRViewport viewport = CreateViewport(previous);
        new CutsceneCameraRouter(rig.Player).Attach(viewport);
        SeekAndTick(rig.Player, 3.0);

        rig.Player.Stop();

        viewport.CameraComponent.ShouldBeSameAs(previous);
        viewport.HasCameraClaim.ShouldBeFalse();
    }

    [Test]
    public void ClaimedViewport_IgnoresFallbackWritesUntilTheClaimEnds()
    {
        using Rig rig = MakeRig();
        CameraComponent fallback = CreateCamera(rig.Root, "Fallback");
        CameraComponent original = CreateCamera(rig.Root, "Original");
        XRViewport viewport = CreateViewport(original);
        object owner = new();

        viewport.ClaimCamera(owner, rig.Cameras[0]);
        viewport.HasCameraClaim.ShouldBeTrue();
        viewport.CameraComponent = fallback;

        viewport.CameraComponent.ShouldBeSameAs(rig.Cameras[0]);
        viewport.ActiveCamera.ShouldBeSameAs(rig.Cameras[0].Camera);

        viewport.ReleaseCameraClaim(owner);
        viewport.HasCameraClaim.ShouldBeFalse();
        viewport.CameraComponent.ShouldBeSameAs(fallback);
    }

    [Test]
    public void Claims_LatestWinsAndReleaseRemovesOnlyThatOwner()
    {
        using Rig rig = MakeRig();
        CameraComponent original = CreateCamera(rig.Root, "Original");
        XRViewport viewport = CreateViewport(original);
        object first = new();
        object second = new();

        viewport.ClaimCamera(first, rig.Cameras[0]);
        viewport.ClaimCamera(second, rig.Cameras[1]);
        viewport.CameraComponent.ShouldBeSameAs(rig.Cameras[1]);

        viewport.ReleaseCameraClaim(first);
        viewport.CameraComponent.ShouldBeSameAs(rig.Cameras[1]);
        viewport.HasCameraClaim.ShouldBeTrue();

        viewport.ReleaseCameraClaim(second);
        viewport.CameraComponent.ShouldBeSameAs(original);
        viewport.HasCameraClaim.ShouldBeFalse();

        // A release for an owner without a claim does nothing.
        viewport.ReleaseCameraClaim(second);
        viewport.CameraComponent.ShouldBeSameAs(original);
    }

    [Test]
    public void ReleasingTheTopClaim_ShowsTheEarlierClaim()
    {
        using Rig rig = MakeRig();
        XRViewport viewport = CreateViewport();
        object first = new();
        object second = new();

        viewport.ClaimCamera(first, rig.Cameras[0]);
        viewport.ClaimCamera(second, rig.Cameras[1]);
        viewport.ReleaseCameraClaim(second);

        viewport.CameraComponent.ShouldBeSameAs(rig.Cameras[0]);
    }

    [Test]
    public void ShotNodeWithoutCameraComponent_AddsOneDiagnostic()
    {
        using Rig rig = MakeRig(lastIsPlainNode: true);
        XRViewport viewport = CreateViewport();
        new CutsceneCameraRouter(rig.Player).Attach(viewport);

        SeekAndTick(rig.Player, 5.0);
        SeekAndTick(rig.Player, 5.5);
        rig.Player.Play();
        rig.Player.Tick(0.1);

        rig.Player.Diagnostics.Count.ShouldBe(1);
        rig.Player.Diagnostics[0].Message.ShouldContain("no camera component");
        viewport.HasCameraClaim.ShouldBeFalse();
    }

    [Test]
    public void FieldOfViewTrack_WritesTheCurveAndRestoresOnStop()
    {
        CameraPropertyClip lens = new()
        {
            Start = T(0),
            Duration = T(2),
            FieldOfView = new CutsceneFloatCurve { Times = [T(0), T(2)], Values = [30f, 50f] },
        };
        using Rig rig = MakeRig(propertyClips: [lens], propertySlot: 0);
        XRPerspectiveCameraParameters parameters = (XRPerspectiveCameraParameters)rig.Cameras[0].Camera.Parameters;

        SeekAndTick(rig.Player, 1.0);
        parameters.VerticalFieldOfView.ShouldBe(40f, 0.001f);

        SeekAndTick(rig.Player, 3.0);
        parameters.VerticalFieldOfView.ShouldBe(60f, 0.001f);

        SeekAndTick(rig.Player, 0.5);
        parameters.VerticalFieldOfView.ShouldBe(35f, 0.001f);

        rig.Player.Stop();
        parameters.VerticalFieldOfView.ShouldBe(60f, 0.001f);
    }

    [Test]
    public void FieldOfViewTrack_OnOrthographicCamera_AddsOneDiagnostic()
    {
        CameraPropertyClip lens = new()
        {
            Start = T(0),
            Duration = T(2),
            FieldOfView = new CutsceneFloatCurve { Times = [T(0), T(2)], Values = [30f, 50f] },
        };
        using Rig rig = MakeRig(propertyClips: [lens], propertySlot: 0, perspective: false);

        SeekAndTick(rig.Player, 0.5);
        SeekAndTick(rig.Player, 1.0);

        rig.Player.Diagnostics.Count.ShouldBe(1);
        rig.Player.Diagnostics[0].Message.ShouldContain("perspective");
    }
}
