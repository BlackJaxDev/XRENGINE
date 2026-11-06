using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture, NonParallelizable]
public sealed class CutsceneVrCameraTests
{
    private const float FadeSeconds = 0.4f;
    private const float HalfFade = FadeSeconds * 0.5f;
    private static readonly Vector3 RigStart = new(5, 0, 5);
    private static readonly Quaternion RigStartRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f);
    private static readonly Vector3 CameraAPosition = new(1, 2, 3);
    private static readonly Vector3 CameraBPosition = new(-4, 1, 8);

    private sealed class FakeVrHost : ICutsceneVrHost
    {
        public readonly List<XRViewport> EyeViewports = [];
        public readonly List<TransformBase> EyeCameraTransforms = [];
        public readonly List<(float Seconds, float Opacity)> Fades = [];

        public bool IsInVR { get; set; } = true;

        public RuntimeVrState.VRRuntime ActiveRuntime { get; set; } = RuntimeVrState.VRRuntime.OpenVR;

        public EVrMirrorMode MirrorMode { get; set; } = EVrMirrorMode.FullIndependentRender;

        public bool CanFadeHeadset { get; set; } = true;

        public double TimeSeconds { get; set; } = 100.0;

        public ulong RenderFrameId { get; set; } = 1000;

        public TransformBase? HeadTransform { get; set; }

        /// <summary>True to make each fade return false.</summary>
        public bool FadeFails { get; set; }

        /// <summary>True to make each fade throw, which breaks the host contract.</summary>
        public bool FadeThrows { get; set; }

        public bool HasTeleportPublisher { get; set; } = true;

        public int TeleportCount { get; private set; }

        public int EyeHistoryInvalidateCount { get; private set; }

        public int EyeExposureResetCount { get; private set; }

        /// <summary>True to make the scheduled fade-in after a close fail.</summary>
        public bool ScheduleFails { get; set; }

        public readonly List<(float FadeSeconds, int RenderFrames, double MaximumSeconds)> ScheduledFadeIns = [];

        public bool IsEyeViewport(XRViewport viewport) => EyeViewports.Contains(viewport);

        public bool IsEyeCameraTransform(TransformBase transform) => EyeCameraTransforms.Contains(transform);

        public bool TryFadeHeadset(float seconds, float opacity)
        {
            if (FadeThrows)
                throw new InvalidOperationException("The compositor is gone.");
            if (FadeFails)
                return false;

            Fades.Add((seconds, opacity));
            return true;
        }

        public bool TryPublishTeleport()
        {
            if (!HasTeleportPublisher)
                return false;

            TeleportCount++;
            return true;
        }

        public void InvalidateEyeViewHistory() => EyeHistoryInvalidateCount++;

        public void ResetEyeExposure() => EyeExposureResetCount++;

        public bool TryScheduleFadeInAfterBlackHold(float fadeSeconds, int renderFrames, double maximumSeconds)
        {
            if (ScheduleFails)
                return false;

            ScheduledFadeIns.Add((fadeSeconds, renderFrames, maximumSeconds));
            return true;
        }

        /// <summary>Runs the scheduled step as the engine host does after the hold.</summary>
        public void CompleteScheduledFadeIn()
        {
            (float fadeSeconds, _, _) = ScheduledFadeIns[^1];
            ResetEyeExposure();
            TryFadeHeadset(fadeSeconds, 0.0f);
        }
    }

    private sealed class FakeResolver : ICutsceneBindingResolver
    {
        public readonly Dictionary<Guid, SceneNode> ById = [];

        public SceneNode? FindNodeById(Guid id) => ById.GetValueOrDefault(id);

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    private sealed class TestWorld : IRuntimeWorldContext
    {
        public bool IsPlaySessionActive => false;

        public void RegisterTick(ETickGroup group, int order, WorldTick tick)
        {
        }

        public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
        {
        }

        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
        {
        }

        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
        {
        }
    }

    /// <summary>
    /// A director with shots at 0-2 s (camera A) and 2-4 s (camera B), a desktop viewport, a headset eye viewport,
    /// and a rig origin with a tracked headset child.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        public required SceneNode Root { get; init; }
        public required CutsceneDirectorComponent Director { get; init; }
        public required CutsceneAsset Asset { get; init; }
        public required FakeVrHost Host { get; init; }
        public required CameraComponent CameraA { get; init; }
        public required CameraComponent CameraB { get; init; }
        public required CameraComponent DesktopCamera { get; init; }
        public required CameraComponent EyeCamera { get; init; }
        public required XRViewport DesktopViewport { get; init; }
        public required XRViewport EyeViewport { get; init; }
        public required Transform RigOrigin { get; init; }
        public required Transform Headset { get; init; }

        public CutscenePlayer Player => Director.Player!;

        public CutsceneVrCameraRouter Router => Director.VrRouter!;

        /// <summary>Runs one director frame after one render frame.</summary>
        public void Frame(double deltaSeconds = 0)
        {
            Host.RenderFrameId++;
            Director.AdvanceFrame(deltaSeconds);
        }

        /// <summary>Ends the fade to black, holds black for the render frames, and ends the fade back in.</summary>
        public void CompleteCutFade()
        {
            Host.TimeSeconds += HalfFade;
            for (int i = 0; i <= CutsceneVrCameraRouter.BlackHoldRenderFrames; i++)
                Frame();
            Host.TimeSeconds += HalfFade;
            Frame();
        }

        public void Dispose()
        {
            Director.Close();
            Root.Destroy(true);
        }
    }

    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static CameraComponent CreateCamera(SceneNode root, string name)
    {
        CameraComponent component = root.NewChild(name).AddComponent<CameraComponent>()!;
        XRCamera camera = new(component.Transform, new XRPerspectiveCameraParameters(60, 1, 0.1f, 100));
        typeof(CameraComponent).GetField("_camera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(component, new Lazy<XRCamera>(() => camera));
        return component;
    }

    private static XRViewport CreateViewport(CameraComponent camera)
        => new(null) { AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false, SetRenderPipelineFromCamera = false, CameraComponent = camera };

    private static Rig MakeRig(ECutsceneVrCameraMode mode, bool withRigOrigin = true)
    {
        SceneNode root = new("Root", new Transform());
        CameraComponent cameraA = CreateCamera(root, "Camera A");
        CameraComponent cameraB = CreateCamera(root, "Camera B");
        CameraComponent desktopCamera = CreateCamera(root, "Desktop");

        // Camera A looks down at 30 degrees, so the rig must use only its heading.
        Transform a = (Transform)cameraA.Transform;
        a.Translation = CameraAPosition;
        a.Rotation = Quaternion.CreateFromYawPitchRoll(MathF.PI * 0.5f, MathF.PI / 6.0f, 0.0f);
        Transform b = (Transform)cameraB.Transform;
        b.Translation = CameraBPosition;
        b.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);

        SceneNode rigNode = root.NewChild("Rig Origin");
        Transform rigOrigin = (Transform)rigNode.Transform;
        rigOrigin.Translation = RigStart;
        rigOrigin.Rotation = RigStartRotation;
        SceneNode headNode = rigNode.NewChild("Headset");
        Transform headset = (Transform)headNode.Transform;
        headset.Translation = new Vector3(0.1f, 1.7f, -0.2f);
        headset.Rotation = Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.05f);
        CameraComponent eyeCamera = CreateCamera(headNode, "Eye");

        FakeResolver resolver = new();
        CutsceneAsset asset = new() { Duration = T(4), VrCameraMode = mode, RigCutFadeSeconds = FadeSeconds };
        foreach (CameraComponent camera in new[] { cameraA, cameraB })
        {
            resolver.ById[camera.SceneNode!.ID] = camera.SceneNode!;
            asset.Bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.SceneObject, ObjectId = camera.SceneNode!.ID });
        }
        CameraCutTrack cut = new() { Name = "Cuts", BindingSlot = 0 };
        cut.Clips.Add(new CameraCutClip { Start = T(0), Duration = T(2), CameraBindingSlot = 0 });
        cut.Clips.Add(new CameraCutClip { Start = T(2), Duration = T(2), CameraBindingSlot = 1 });
        asset.Tracks.Add(cut);

        FakeVrHost host = new() { HeadTransform = headset };
        host.EyeCameraTransforms.Add(eyeCamera.Transform);
        XRViewport desktop = CreateViewport(desktopCamera);
        XRViewport eye = CreateViewport(eyeCamera);
        host.EyeViewports.Add(eye);

        SceneNode directorNode = root.NewChild("Director");
        directorNode.SetWorldContext(new TestWorld());
        CutsceneDirectorComponent director = directorNode.AddComponent<CutsceneDirectorComponent>()!;
        director.Asset = asset;
        director.BindingResolver = resolver;
        director.ResidencyEnabled = false;
        director.TargetViewport = desktop;
        director.VrHost = host;
        if (withRigOrigin)
            director.VrRigOrigin = rigOrigin;

        return new Rig
        {
            Root = root,
            Director = director,
            Asset = asset,
            Host = host,
            CameraA = cameraA,
            CameraB = cameraB,
            DesktopCamera = desktopCamera,
            EyeCamera = eyeCamera,
            DesktopViewport = desktop,
            EyeViewport = eye,
            RigOrigin = rigOrigin,
            Headset = headset,
        };
    }

    /// <summary>Opens a RigAnchor rig and completes the fade of the first shot, so the rig is at camera A.</summary>
    private static Rig MakeRigAtFirstShot()
    {
        Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        rig.Director.OpenAsync().Wait();
        rig.Player.Play();
        rig.Frame();
        rig.CompleteCutFade();
        rig.RigOrigin.Translation.ShouldBe(CameraAPosition);
        rig.Router.IsFading.ShouldBeFalse();
        return rig;
    }

    private static void ShouldBeSameRotation(Quaternion actual, Quaternion expected)
        => MathF.Abs(Quaternion.Dot(Quaternion.Normalize(actual), Quaternion.Normalize(expected))).ShouldBeGreaterThan(0.9999f);

    private static void ShouldBeAtOriginalPose(Rig rig)
    {
        rig.RigOrigin.Translation.ShouldBe(RigStart);
        ShouldBeSameRotation(rig.RigOrigin.Rotation, RigStartRotation);
    }

    /// <summary>The eye viewport, the eye camera, and the tracked headset must keep their values in every mode.</summary>
    private sealed record EyeAndHeadSnapshot(CameraComponent? EyeViewportCamera, Matrix4x4 EyeCameraLocal, Matrix4x4 HeadsetLocal)
    {
        public static EyeAndHeadSnapshot Take(Rig rig)
            => new(rig.EyeViewport.CameraComponent, Local(rig.EyeCamera.Transform), Local(rig.Headset));

        private static Matrix4x4 Local(TransformBase transform)
        {
            Transform t = (Transform)transform;
            return Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Translation);
        }
    }

    [Test]
    public void VrCameraMode_DefaultsToSpectatorOnly()
        => new CutsceneAsset().VrCameraMode.ShouldBe(ECutsceneVrCameraMode.SpectatorOnly);

    [TestCase(0.0f)]
    [TestCase(-1.0f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void RigCutFadeSeconds_RejectsAValueThatIsNotFiniteAndPositive(float value)
    {
        CutsceneAsset asset = new();
        Should.Throw<ArgumentOutOfRangeException>(() => asset.RigCutFadeSeconds = value);
        asset.RigCutFadeSeconds.ShouldBe(CutsceneAsset.DefaultRigCutFadeSeconds);
    }

    [Test]
    public void NoMode_WritesTheEyeCameraOrTheHeadPose(
        [Values(ECutsceneVrCameraMode.SpectatorOnly, ECutsceneVrCameraMode.RigAnchor, ECutsceneVrCameraMode.WorldScreen)] ECutsceneVrCameraMode mode)
    {
        using Rig rig = MakeRig(mode);
        EyeAndHeadSnapshot before = EyeAndHeadSnapshot.Take(rig);

        rig.Director.OpenAsync().Wait();
        if (rig.Director.IsOpen)
        {
            rig.Player.Play();
            for (int i = 0; i < 30; i++)
            {
                rig.Frame(0.25);
                rig.Host.TimeSeconds += HalfFade;
            }
            rig.Player.Seek(T(1), ESeekIntent.Jump);
            rig.Frame();
            rig.CompleteCutFade();
            rig.Director.Close();
        }

        EyeAndHeadSnapshot after = EyeAndHeadSnapshot.Take(rig);
        after.EyeViewportCamera.ShouldBeSameAs(before.EyeViewportCamera);
        after.EyeCameraLocal.ShouldBe(before.EyeCameraLocal);
        after.HeadsetLocal.ShouldBe(before.HeadsetLocal);
        rig.EyeViewport.HasCameraClaim.ShouldBeFalse();
        rig.EyeViewport.ActiveCamera.ShouldBeSameAs(rig.EyeCamera.Camera);
    }

    [Test]
    public void SpectatorOnly_InVr_ClaimsOnlyTheDesktopViewport()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.SpectatorOnly);
        rig.Director.OpenAsync().Wait();
        rig.Director.IsOpen.ShouldBeTrue();
        rig.Director.VrRouter.ShouldBeNull();

        rig.Player.Play();
        rig.Frame();
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.CameraA);
        rig.Frame(2.5);
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.CameraB);

        rig.EyeViewport.HasCameraClaim.ShouldBeFalse();
        rig.EyeViewport.CameraComponent.ShouldBeSameAs(rig.EyeCamera);
        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades.ShouldBeEmpty();
        rig.Host.TeleportCount.ShouldBe(0);
        rig.Host.EyeHistoryInvalidateCount.ShouldBe(0);
        rig.Host.EyeExposureResetCount.ShouldBe(0);
        rig.Director.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void TargetViewportIsAnEyeViewport_FailsTheOpenAndClaimsNothing(
        [Values(ECutsceneVrCameraMode.SpectatorOnly, ECutsceneVrCameraMode.RigAnchor)] ECutsceneVrCameraMode mode)
    {
        using Rig rig = MakeRig(mode);
        rig.Director.TargetViewport = rig.EyeViewport;

        rig.Director.OpenAsync().Wait();

        rig.Director.IsOpen.ShouldBeFalse();
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains("headset eye", StringComparison.Ordinal));
        rig.EyeViewport.HasCameraClaim.ShouldBeFalse();
        rig.EyeViewport.CameraComponent.ShouldBeSameAs(rig.EyeCamera);
        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades.ShouldBeEmpty();
    }

    [Test]
    public void RigAnchor_MovesTheRigOriginAndEachCutFadesThroughBlack()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        rig.Director.OpenAsync().Wait();
        rig.Director.IsOpen.ShouldBeTrue();
        CutsceneVrCameraRouter router = rig.Router;

        // The first shot fades the headset to black, and the rig holds until the headset is black.
        rig.Player.Play();
        rig.Frame();
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.CameraA);
        rig.Host.Fades.ShouldBe([(HalfFade, 1.0f)]);
        router.CutFadeCount.ShouldBe(1);
        ShouldBeAtOriginalPose(rig);

        rig.Host.TimeSeconds += HalfFade * 0.5;
        rig.Frame();
        ShouldBeAtOriginalPose(rig);

        // At black, the rig moves to camera A with only its heading. The headset stays black for the render frames in flight.
        rig.Host.TimeSeconds += HalfFade * 0.5;
        rig.Frame();
        rig.RigOrigin.Translation.ShouldBe(CameraAPosition);
        ShouldBeSameRotation(rig.RigOrigin.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f));
        router.IsHoldingBlack.ShouldBeTrue();
        router.RigMoveCount.ShouldBe(1);
        rig.Host.TeleportCount.ShouldBe(1);
        rig.Host.EyeHistoryInvalidateCount.ShouldBe(1);
        rig.Host.Fades.Count.ShouldBe(1);

        // A stereo eye frame from before the move has no epoch guard, so the exposure reset waits for the end of the hold.
        rig.Host.EyeExposureResetCount.ShouldBe(0);
        for (int i = 1; i < CutsceneVrCameraRouter.BlackHoldRenderFrames; i++)
        {
            rig.Frame();
            rig.Host.Fades.Count.ShouldBe(1);
            rig.Host.EyeExposureResetCount.ShouldBe(0);
        }
        rig.Frame();
        rig.Host.Fades.ShouldBe([(HalfFade, 1.0f), (HalfFade, 0.0f)]);
        rig.Host.EyeExposureResetCount.ShouldBe(1);
        rig.Host.EyeHistoryInvalidateCount.ShouldBe(1);
        router.IsRigMoved.ShouldBeTrue();

        // The camera of the shot moves, and the rig follows with no fade.
        rig.Host.TimeSeconds += HalfFade;
        ((Transform)rig.CameraA.Transform).Translation = CameraAPosition + new Vector3(0, 0, 1);
        rig.Frame(0.5);
        rig.RigOrigin.Translation.ShouldBe(CameraAPosition + new Vector3(0, 0, 1));
        router.IsFading.ShouldBeFalse();
        rig.Host.Fades.Count.ShouldBe(2);

        // A cut fades to black again. The rig stays at camera A until the headset is black.
        rig.Frame(2.0);
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.CameraB);
        router.CutFadeCount.ShouldBe(2);
        rig.Host.Fades[^1].ShouldBe((HalfFade, 1.0f));
        rig.RigOrigin.Translation.ShouldBe(CameraAPosition + new Vector3(0, 0, 1));

        rig.CompleteCutFade();
        rig.RigOrigin.Translation.ShouldBe(CameraBPosition);
        ShouldBeSameRotation(rig.RigOrigin.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI));
        rig.Host.Fades[^1].ShouldBe((HalfFade, 0.0f));
        rig.Host.TeleportCount.ShouldBe(2);

        // The tracked headset keeps its local pose, so its world pose follows the rig.
        rig.Headset.Translation.ShouldBe(new Vector3(0.1f, 1.7f, -0.2f));
        rig.EyeViewport.HasCameraClaim.ShouldBeFalse();
    }

    [Test]
    public void RigAnchor_WithNoRenderFrames_EndsTheBlackHoldAfterTheMaximumTime()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        rig.Director.OpenAsync().Wait();
        rig.Player.Play();
        rig.Director.AdvanceFrame(0);
        rig.Host.TimeSeconds += HalfFade;
        rig.Director.AdvanceFrame(0);
        rig.Router.IsHoldingBlack.ShouldBeTrue();

        rig.Host.TimeSeconds += CutsceneVrCameraRouter.MaximumBlackHoldSeconds * 0.5;
        rig.Director.AdvanceFrame(0);
        rig.Router.IsHoldingBlack.ShouldBeTrue();

        rig.Host.TimeSeconds += CutsceneVrCameraRouter.MaximumBlackHoldSeconds * 0.5;
        rig.Director.AdvanceFrame(0);
        rig.Router.IsHoldingBlack.ShouldBeFalse();
        rig.Host.Fades[^1].ShouldBe((HalfFade, 0.0f));
    }

    [Test]
    public void RigAnchor_AJumpInsideOneShot_FadesThroughBlack()
    {
        using Rig rig = MakeRigAtFirstShot();
        rig.Router.CutFadeCount.ShouldBe(1);

        rig.Player.Seek(T(1.5), ESeekIntent.Jump);
        rig.Frame();

        rig.Router.CutFadeCount.ShouldBe(2);
        rig.Host.Fades[^1].ShouldBe((HalfFade, 1.0f));
    }

    [Test]
    public void RigAnchor_Close_PutsTheRigBackBehindAFadeFromBlack()
    {
        using Rig rig = MakeRigAtFirstShot();
        int fadesBefore = rig.Host.Fades.Count;
        int teleportsBefore = rig.Host.TeleportCount;
        int invalidatesBefore = rig.Host.EyeHistoryInvalidateCount;
        int exposureBefore = rig.Host.EyeExposureResetCount;

        rig.Director.Close();

        // The cleanup ends in the close. The host holds black, then resets the exposure and fades in.
        ShouldBeAtOriginalPose(rig);
        rig.Director.VrRouter.ShouldBeNull();
        rig.DesktopViewport.HasCameraClaim.ShouldBeFalse();
        rig.Host.Fades.Skip(fadesBefore).ShouldBe([(0.0f, 1.0f)]);
        rig.Host.TeleportCount.ShouldBe(teleportsBefore + 1);
        rig.Host.EyeHistoryInvalidateCount.ShouldBe(invalidatesBefore + 1);
        rig.Host.EyeExposureResetCount.ShouldBe(exposureBefore);
        rig.Host.ScheduledFadeIns.ShouldBe([(HalfFade, CutsceneVrCameraRouter.BlackHoldRenderFrames, CutsceneVrCameraRouter.MaximumBlackHoldSeconds)]);

        rig.Host.CompleteScheduledFadeIn();
        rig.Host.Fades[^1].ShouldBe((HalfFade, 0.0f));
        rig.Host.EyeExposureResetCount.ShouldBe(exposureBefore + 1);
    }

    [Test]
    public void RigAnchor_CloseWhenTheHostCannotHoldBlack_FadesInAtOnceWithADiagnostic()
    {
        using Rig rig = MakeRigAtFirstShot();
        rig.Host.ScheduleFails = true;
        int fadesBefore = rig.Host.Fades.Count;

        rig.Director.Close();

        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades.Skip(fadesBefore).ShouldBe([(0.0f, 1.0f), (HalfFade, 0.0f)]);
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains("could not hold the headset black", StringComparison.Ordinal));
    }

    [Test]
    public void RigAnchor_CloseDuringTheFirstFade_EndsWithTheSceneVisible()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        rig.Director.OpenAsync().Wait();
        rig.Player.Play();
        rig.Frame();

        rig.Director.Close();

        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades[^1].Opacity.ShouldBe(0.0f);
    }

    [Test]
    public void RigAnchor_CloseDuringTheFadeIn_PutsTheRigBackAndEndsWithTheSceneVisible()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        rig.Director.OpenAsync().Wait();
        rig.Player.Play();
        rig.Frame();
        rig.Host.TimeSeconds += HalfFade;
        for (int i = 0; i <= CutsceneVrCameraRouter.BlackHoldRenderFrames; i++)
            rig.Frame();
        rig.Router.IsFading.ShouldBeTrue();
        rig.Router.IsHoldingBlack.ShouldBeFalse();
        rig.RigOrigin.Translation.ShouldBe(CameraAPosition);
        int fadesBefore = rig.Host.Fades.Count;

        rig.Director.Close();

        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades.Skip(fadesBefore).ShouldBe([(0.0f, 1.0f)]);
        rig.Host.ScheduledFadeIns.Count.ShouldBe(1);
    }

    [Test]
    public void RigAnchor_CloseDuringTheBlackHold_PutsTheRigBackAndSchedulesTheHoldAndFadeIn()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        rig.Director.OpenAsync().Wait();
        rig.Player.Play();
        rig.Frame();
        rig.Host.TimeSeconds += HalfFade;
        rig.Frame();
        rig.Router.IsHoldingBlack.ShouldBeTrue();
        int fadesBefore = rig.Host.Fades.Count;

        rig.Director.Close();

        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades.Skip(fadesBefore).ShouldBe([(0.0f, 1.0f)]);
        rig.Host.ScheduledFadeIns.Count.ShouldBe(1);
        rig.Host.TeleportCount.ShouldBe(2);
        rig.Host.EyeExposureResetCount.ShouldBe(0);
        rig.Host.CompleteScheduledFadeIn();
        rig.Host.EyeExposureResetCount.ShouldBe(1);
        rig.Host.Fades[^1].Opacity.ShouldBe(0.0f);
    }

    [Test]
    public void RigAnchor_ACutDuringTheBlackHold_MovesTheRigAgainAtBlackWithNoSecondFadeOut()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        rig.Director.OpenAsync().Wait();
        rig.Player.Play();
        rig.Frame(1.9);
        rig.Host.TimeSeconds += HalfFade;
        rig.Frame();
        rig.Router.IsHoldingBlack.ShouldBeTrue();
        rig.RigOrigin.Translation.ShouldBe(CameraAPosition);

        // The cut to camera B comes while the headset is black.
        rig.Frame(0.2);
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.CameraB);
        rig.Frame();

        rig.RigOrigin.Translation.ShouldBe(CameraBPosition);
        rig.Router.IsHoldingBlack.ShouldBeTrue();
        rig.Router.CutFadeCount.ShouldBe(1);
        rig.Router.RigMoveCount.ShouldBe(2);
        rig.Host.TeleportCount.ShouldBe(2);
        rig.Host.EyeHistoryInvalidateCount.ShouldBe(2);
        rig.Host.Fades.ShouldBe([(HalfFade, 1.0f)]);
        rig.Host.EyeExposureResetCount.ShouldBe(0);

        for (int i = 0; i < CutsceneVrCameraRouter.BlackHoldRenderFrames; i++)
            rig.Frame();
        rig.Host.Fades.ShouldBe([(HalfFade, 1.0f), (HalfFade, 0.0f)]);
        rig.Host.EyeExposureResetCount.ShouldBe(1);
    }

    [Test]
    public void RigAnchor_HeadMovesToAnotherParent_TheNextCutStopsTheRouter()
    {
        using Rig rig = MakeRigAtFirstShot();
        SceneNode otherRig = rig.Root.NewChild("Other Rig");
        rig.Headset.SceneNode!.Parent = otherRig;

        rig.Frame(2.5);

        rig.Router.HasFailed.ShouldBeTrue();
        ShouldBeAtOriginalPose(rig);
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains("rig origin check failed at a camera cut", StringComparison.Ordinal));
        rig.Frame(0.5);
        ShouldBeAtOriginalPose(rig);
    }

    [Test]
    public void RigAnchor_CloseWithAFadeThatThrowsOrFails_StillRestoresEverything([Values] bool throws)
    {
        using Rig rig = MakeRigAtFirstShot();
        rig.DesktopViewport.HasCameraClaim.ShouldBeTrue();
        if (throws)
            rig.Host.FadeThrows = true;
        else
            rig.Host.FadeFails = true;

        Should.NotThrow(rig.Director.Close);

        ShouldBeAtOriginalPose(rig);
        rig.Director.IsOpen.ShouldBeFalse();
        rig.DesktopViewport.HasCameraClaim.ShouldBeFalse();
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.DesktopCamera);
    }

    [Test]
    public void RigAnchor_AFadeThatThrowsAtACut_StopsTheRouterWithoutAnException()
    {
        using Rig rig = MakeRigAtFirstShot();
        rig.Host.FadeThrows = true;

        Should.NotThrow(() => rig.Frame(2.5));

        rig.Router.HasFailed.ShouldBeTrue();
        ShouldBeAtOriginalPose(rig);
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.CameraB);
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains("fade failed at a camera cut", StringComparison.Ordinal));
    }

    [Test]
    public void RigAnchor_FadeLostDuringUpdateInsideOneShot_PutsTheRigBackAtOnce([Values] bool sessionEnded)
    {
        using Rig rig = MakeRigAtFirstShot();
        int fadesBefore = rig.Host.Fades.Count;
        if (sessionEnded)
            rig.Host.IsInVR = false;
        rig.Host.CanFadeHeadset = false;

        // The step stays inside the first shot, so no cut runs.
        rig.Frame(0.1);

        rig.Router.HasFailed.ShouldBeTrue();
        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades.Count.ShouldBe(fadesBefore);
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains("became unavailable during playback", StringComparison.Ordinal));

        // The rig stays at the pose of the user for the rest of the cutscene.
        rig.Frame(2.5);
        ShouldBeAtOriginalPose(rig);
    }

    [Test]
    public void RigAnchor_WithoutATeleportPublisher_AddsOneDiagnostic()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        rig.Host.HasTeleportPublisher = false;
        rig.Director.OpenAsync().Wait();
        rig.Player.Play();
        rig.Frame();
        rig.CompleteCutFade();
        rig.Frame(2.5);
        rig.CompleteCutFade();

        rig.Router.RigMoveCount.ShouldBe(2);
        rig.Director.Diagnostics.Count(d => d.Message.Contains("teleport publisher", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    public void RigAnchor_OnARuntimeWithoutAHeadsetFade_FailsAndDoesNotSubstituteAMode()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        rig.Host.ActiveRuntime = RuntimeVrState.VRRuntime.OpenXR;
        rig.Host.CanFadeHeadset = false;

        rig.Director.OpenAsync().Wait();

        rig.Director.IsOpen.ShouldBeFalse();
        rig.Director.Diagnostics.ShouldContain(d =>
            d.Message.Contains(nameof(ECutsceneVrCameraMode.RigAnchor), StringComparison.Ordinal)
            && d.Message.Contains("OpenXR", StringComparison.Ordinal));
        rig.DesktopViewport.HasCameraClaim.ShouldBeFalse();
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.DesktopCamera);
        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades.ShouldBeEmpty();
    }

    [Test]
    public void RigAnchor_WithoutARigOrigin_FailsTheOpen()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor, withRigOrigin: false);

        rig.Director.OpenAsync().Wait();

        rig.Director.IsOpen.ShouldBeFalse();
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains(nameof(CutsceneDirectorComponent.VrRigOrigin), StringComparison.Ordinal));
        rig.DesktopViewport.HasCameraClaim.ShouldBeFalse();
    }

    [TestCase("head", "is the tracked head")]
    [TestCase("eye", "is a headset eye camera")]
    [TestCase("sibling", "not an ancestor")]
    [TestCase("child", "not an ancestor")]
    [TestCase("no head", "published none")]
    public void RigAnchor_ARigOriginThatIsNotAnAncestorOfTheHead_FailsTheOpen(string kind, string expected)
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.RigAnchor);
        EyeAndHeadSnapshot before = EyeAndHeadSnapshot.Take(rig);
        switch (kind)
        {
            case "head":
                rig.Director.VrRigOrigin = rig.Headset;
                break;
            case "eye":
                rig.Director.VrRigOrigin = rig.EyeCamera.Transform;
                break;
            case "sibling":
                rig.Director.VrRigOrigin = rig.Root.NewChild("Elsewhere").Transform;
                break;
            case "child":
                rig.Director.VrRigOrigin = rig.Headset.SceneNode!.NewChild("Under Head").Transform;
                break;
            case "no head":
                rig.Host.HeadTransform = null;
                break;
        }

        rig.Director.OpenAsync().Wait();

        rig.Director.IsOpen.ShouldBeFalse();
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains(expected, StringComparison.Ordinal));
        rig.DesktopViewport.HasCameraClaim.ShouldBeFalse();
        rig.Host.Fades.ShouldBeEmpty();
        EyeAndHeadSnapshot after = EyeAndHeadSnapshot.Take(rig);
        after.EyeCameraLocal.ShouldBe(before.EyeCameraLocal);
        after.HeadsetLocal.ShouldBe(before.HeadsetLocal);
    }

    [Test]
    public void WorldScreen_InVr_FailsAndDoesNotSubstituteAMode()
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.WorldScreen);

        rig.Director.OpenAsync().Wait();

        rig.Director.IsOpen.ShouldBeFalse();
        rig.Director.Diagnostics.ShouldContain(d =>
            d.Message.Contains(nameof(ECutsceneVrCameraMode.WorldScreen), StringComparison.Ordinal)
            && d.Message.Contains("not supported", StringComparison.Ordinal));
        rig.DesktopViewport.HasCameraClaim.ShouldBeFalse();
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.DesktopCamera);
        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades.ShouldBeEmpty();
    }

    [Test]
    public void UnknownMode_InVr_FailsTheOpen()
    {
        using Rig rig = MakeRig((ECutsceneVrCameraMode)99);

        rig.Director.OpenAsync().Wait();

        rig.Director.IsOpen.ShouldBeFalse();
        rig.Director.Diagnostics.ShouldContain(d => d.Message.Contains("99", StringComparison.Ordinal));
    }

    [Test]
    public void AnyMode_OutsideVr_UsesOnlyTheDesktopViewport(
        [Values(ECutsceneVrCameraMode.SpectatorOnly, ECutsceneVrCameraMode.RigAnchor, ECutsceneVrCameraMode.WorldScreen)] ECutsceneVrCameraMode mode)
    {
        using Rig rig = MakeRig(mode);
        rig.Host.IsInVR = false;
        rig.Host.CanFadeHeadset = false;

        rig.Director.OpenAsync().Wait();
        rig.Player.Play();
        rig.Frame();

        rig.Director.IsOpen.ShouldBeTrue();
        rig.Director.VrRouter.ShouldBeNull();
        rig.DesktopViewport.CameraComponent.ShouldBeSameAs(rig.CameraA);
        ShouldBeAtOriginalPose(rig);
        rig.Host.Fades.ShouldBeEmpty();
    }

    [TestCase(EVrMirrorMode.BlitSubmittedEye, true)]
    [TestCase(EVrMirrorMode.CyclopeanReconstruct, true)]
    [TestCase(EVrMirrorMode.Off, true)]
    [TestCase(EVrMirrorMode.FullIndependentRender, false)]
    public void MirrorModeWithoutTheDesktopScene_AddsANoteAndKeepsTheMirrorMode(EVrMirrorMode mirror, bool expectNote)
    {
        using Rig rig = MakeRig(ECutsceneVrCameraMode.SpectatorOnly);
        rig.Host.MirrorMode = mirror;

        rig.Director.OpenAsync().Wait();

        rig.Director.IsOpen.ShouldBeTrue();
        rig.Director.Diagnostics.Any(d => d.Message.Contains("mirror mode", StringComparison.Ordinal)).ShouldBe(expectNote);
        rig.Host.MirrorMode.ShouldBe(mirror);
    }

    [Test]
    public void RigAnchor_FollowInsideOneShot_AllocatesNothing()
    {
        using Rig rig = MakeRigAtFirstShot();
        rig.Frame(0.001);
        Transform cameraA = (Transform)rig.CameraA.Transform;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
        {
            cameraA.Translation = CameraAPosition + new Vector3(i * 0.001f, 0, 0);
            rig.Frame(0.001);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
        rig.RigOrigin.Translation.X.ShouldBe(CameraAPosition.X + 499 * 0.001f, 1e-4f);
    }

    [Test]
    public void RigAnchor_FramesThatWaitDuringTheFadeOut_AllocateNothing()
    {
        using Rig rig = MakeRigAtFirstShot();
        rig.Frame(2.0);
        rig.Router.IsFading.ShouldBeTrue();
        rig.Router.IsHoldingBlack.ShouldBeFalse();
        rig.Frame(0.001);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
            rig.Frame(0.001);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
        rig.Router.IsFading.ShouldBeTrue();
        rig.RigOrigin.Translation.ShouldBe(CameraAPosition);
    }
}
