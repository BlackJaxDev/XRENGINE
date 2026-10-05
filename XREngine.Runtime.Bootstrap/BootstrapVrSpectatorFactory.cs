using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Transforms;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.VR;
using XREngine.Data.Components.Scene;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace XREngine.Runtime.Bootstrap.Builders;

/// <summary>Composes an opt-in local VR spectator without a pawn or audio listener.</summary>
public static class BootstrapVrSpectatorFactory
{
    /// <summary>Applies explicitly reserved local-avatar visibility only to the supplied headset's eyes.</summary>
    public static void ConfigureFirstPersonVisibility(VrSpectatorFollowComponent spectator,
        SceneNode avatarRoot, VRHeadsetComponent headset, int reservedLayer)
    {
        ArgumentNullException.ThrowIfNull(spectator);
        ArgumentNullException.ThrowIfNull(avatarRoot);
        ArgumentNullException.ThrowIfNull(headset);
        spectator.ClearFirstPersonVisibility();
        spectator.OwnFirstPersonVisibility(new VrFirstPersonVisibilityScope(
            avatarRoot.FindAllDescendantComponentsAssignableTo<RenderableComponent>(),
            headset.LeftEyeCamera, headset.RightEyeCamera, reservedLayer));
    }

    public static VrSpectatorFollowComponent Create(SceneNode sceneParent, TransformBase playerRoot,
        VRPlayerCharacterComponent? player = null, XRComponent? playerCollisionBody = null)
    {
        ArgumentNullException.ThrowIfNull(sceneParent);
        ArgumentNullException.ThrowIfNull(playerRoot);
        SceneNode anchor = sceneParent.NewChild("VR Spectator Anchor");
        anchor.SetTransform<Transform>();
        SceneNode boomNode = anchor.NewChild("VR Spectator Collision Boom");
        BoomTransform boom = boomNode.SetTransform<BoomTransform>();
        boom.AutomaticUpdate = false;
        // Without an explicit owner body, static-only tracing cannot hit the dynamic player.
        boom.QueryFilter = new PhysicsQueryFilter(playerCollisionBody is null ? PhysicsQueryActorTypes.Static : PhysicsQueryActorTypes.All);
        boom.LayerMask = LayerMask.Everything;
        boom.TraceRadius = .2f;
        if (playerCollisionBody is not null)
            boom.IgnoredComponents.Add(playerCollisionBody);
        SceneNode cameraNode = boomNode.NewChild("VR Spectator Camera");
        cameraNode.SetTransform<Transform>();
        CameraComponent camera = cameraNode.AddComponent<CameraComponent>()!;
        camera.CameraParameters = new XRPerspectiveCameraParameters(60f, 1280f / 720f, .1f, 10000f);
        camera.Camera.RenderPipeline = BootstrapRenderSettings.CreateSceneRenderPipeline(stereo: false);
        camera.CullWithFrustum = true;
        VrSpectatorOutputComponent output = cameraNode.AddComponent<VrSpectatorOutputComponent>()!;
        output.SourceCamera = camera.Camera;
        VrSpectatorFollowComponent follow = anchor.AddComponent<VrSpectatorFollowComponent>()!;
        follow.PlayerRoot = playerRoot;
        follow.Player = player;
        follow.Camera = camera;
        follow.Boom = boom;
        follow.Output = output;
        return follow;
    }
}
