using RollingBall;
using XREngine;
using XREngine.Components.VR;
using XREngine.Data.Components.Scene;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace RollingBall.DesktopVR;

/// <summary>Desktop-only VR composition for the portable Rolling Ball world.</summary>
public sealed class RollingBallDesktopVrHost : IRollingBallPlatformHost
{
    /// <summary>Installs the VR host before the standalone launcher requests its profile.</summary>
    public static void Register()
        => RollingBallHostRegistration.Register(new RollingBallDesktopVrHost());

    public RuntimeApplicationProfile ApplicationProfile => RuntimeApplicationProfile.VrClient;

    public GameStartupSettings CreateStartupSettings(RollingBallWorldAsset world, bool runtimeSmoke)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (runtimeSmoke)
            return new GameStartupSettings();

        AttachVrRig(world);
        return new VRGameStartupSettings<RollingBallActionSet, RollingBallAction>
        {
            GameName = "Rolling Ball",
            VRRuntime = EVRRuntime.Auto,
            VRManifest = RollingBallVrManifest.CreateApplicationManifest(),
            ActionManifest = RollingBallVrManifest.CreateActionManifest(),
        };
    }

    private static void AttachVrRig(RollingBallWorldAsset world)
    {
        SceneNode? rig = null;
        foreach (XRScene scene in world.Scenes)
        {
            foreach (SceneNode root in scene.RootNodes)
            {
                rig = root.FindDescendantByName("Player Rig", StringComparison.Ordinal);
                if (rig is not null)
                    break;
            }
            if (rig is not null)
                break;
        }

        if (rig is null)
            throw new InvalidOperationException("Rolling Ball desktop VR host requires the authored 'Player Rig' node.");

        if (rig.FindDescendantByName("VR Headset", StringComparison.Ordinal) is null)
        {
            SceneNode headset = rig.NewChild("VR Headset");
            headset.SetTransform<VRHeadsetTransform>();
            _ = new SceneNode(headset, "Left Eye", new VREyeTransform(true));
            _ = new SceneNode(headset, "Right Eye", new VREyeTransform(false));
            VRHeadsetComponent component = headset.AddComponent<VRHeadsetComponent>("Tracked Headset")
                ?? throw new InvalidOperationException("Rolling Ball desktop VR host could not create the headset component.");
            component.Near = 0.1f;
            component.Far = 100000.0f;
        }

        AddControllerIfMissing(rig, "Left Controller", leftHand: true);
        AddControllerIfMissing(rig, "Right Controller", leftHand: false);
        if (rig.FindDescendantByName("VR Trackers", StringComparison.Ordinal) is null)
            _ = rig.NewChild("VR Trackers").AddComponent<VRTrackerCollectionComponent>("Tracked Devices");
    }

    private static void AddControllerIfMissing(SceneNode rig, string name, bool leftHand)
    {
        if (rig.FindDescendantByName(name, StringComparison.Ordinal) is not null)
            return;

        SceneNode controller = rig.NewChild(name);
        VRControllerTransform transform = controller.SetTransform<VRControllerTransform>();
        transform.LeftHand = leftHand;
        VRControllerModelComponent model = controller.AddComponent<VRControllerModelComponent>("Runtime Controller Model")
            ?? throw new InvalidOperationException($"Rolling Ball desktop VR host could not create '{name}'.");
        model.LeftHand = leftHand;
    }
}
