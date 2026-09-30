using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.VR;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.VR;

[TestFixture, NonParallelizable]
public sealed class VrDesktopRoutingTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void DisableOrReplaceSpectator_RestoresNullablePriorDesktopCamera(bool replace)
    {
        var root = new SceneNode("Routing test", new Transform());
        try
        {
            var player = root.AddComponent<VRPlayerCharacterComponent>()!;
            var camera = CreateCamera(root, "Spectator camera");
            var follow = root.NewChild("Follow").AddComponent<VrSpectatorFollowComponent>()!;
            follow.Camera = camera;
            player.Spectator = follow;
            var viewport = new XRViewport(null) { AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false, SetRenderPipelineFromCamera = false };
            viewport.CameraComponent.ShouldBeNull();
            player.RouteSpectatorDesktop(viewport).ShouldBeTrue();
            viewport.CameraComponent.ShouldBeSameAs(camera);
            if (replace)
                player.Spectator = null;
            else
            {
                player.SpectatorEnabled = false;
                typeof(VRPlayerCharacterComponent).GetMethod("ApplySpectatorDesktopRouting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(player, null);
            }
            viewport.CameraComponent.ShouldBeNull();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ReplacingViewport_RestoresOldOutputAndDoesNotOverwriteNewUserCameraSelection()
    {
        var root = new SceneNode("Routing test", new Transform());
        try
        {
            var player = root.AddComponent<VRPlayerCharacterComponent>()!;
            var camera = CreateCamera(root, "Spectator camera");
            var previous = CreateCamera(root, "Editor camera");
            var userSelected = CreateCamera(root, "New user camera");
            var follow = root.NewChild("Follow").AddComponent<VrSpectatorFollowComponent>()!;
            follow.Camera = camera;
            player.Spectator = follow;
            var first = new XRViewport(null) { AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false, SetRenderPipelineFromCamera = false, CameraComponent = previous };
            var second = new XRViewport(null) { AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false, SetRenderPipelineFromCamera = false };
            player.RouteSpectatorDesktop(first).ShouldBeTrue();
            player.RouteSpectatorDesktop(second).ShouldBeTrue();
            first.CameraComponent.ShouldBeSameAs(previous);
            second.CameraComponent = userSelected;
            player.Spectator = null;
            second.CameraComponent.ShouldBeSameAs(userSelected);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SnapTurn_ChangesYawAndPublishesOneDiscontinuity()
    {
        var root = new SceneNode("Snap-turn test", new Transform());
        try
        {
            var pawn = root.AddComponent<CharacterPawnComponent>()!;
            var rotation = root.NewChild("Body yaw").GetTransformAs<Transform>(true)!;
            pawn.ViewRotationTransform = rotation;
            long version = RuntimeVrDiscontinuityServices.Version;
            pawn.SnapTurn(45).ShouldBeTrue();
            RuntimeVrDiscontinuityServices.Version.ShouldBe(version + 1);
            rotation.Rotator.Yaw.ShouldBe(45, 0.0001f);
            pawn.SnapTurn(float.NaN).ShouldBeFalse();
            RuntimeVrDiscontinuityServices.Version.ShouldBe(version + 1);
        }
        finally { root.Destroy(true); }
    }
    private static CameraComponent CreateCamera(SceneNode root, string name)
    {
        var component = root.NewChild(name).AddComponent<CameraComponent>()!;
        // Routing ownership does not require a renderer/window host. Supply a real native-free camera
        // instead of invoking the application's default camera-depth factory.
        var camera = new XRCamera(component.Transform, new XRPerspectiveCameraParameters(60, 1, 0.1f, 100));
        typeof(CameraComponent).GetField("_camera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(component, new Lazy<XRCamera>(() => camera));
        return component;
    }

}
