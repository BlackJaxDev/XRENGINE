using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.VR;
using XREngine.Data.Components.Scene;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Runtime.Bootstrap.Builders;

/// <summary>Shared avatar/calibration composition for imported avatars and configured runtime prefabs.</summary>
public static class BootstrapVrAvatarFactory
{
    public static VRPlayerCharacterComponent ConfigureImportedAvatar(SceneNode pawnRoot, HumanoidComponent humanoid,
        VRHeightScaleComponent? heightScale = null, VRIKSolverComponent? solver = null, string? avatarIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(pawnRoot);
        ArgumentNullException.ThrowIfNull(humanoid);
        SceneNode avatarRoot = humanoid.SceneNode;
        heightScale ??= avatarRoot.GetComponent<VRHeightScaleComponent>() ?? avatarRoot.AddComponent<VRHeightScaleComponent>()!;
        solver ??= avatarRoot.GetComponent<VRIKSolverComponent>() ?? avatarRoot.AddComponent<VRIKSolverComponent>()!;
        solver.AssignedHumanoid = humanoid;
        heightScale.HumanoidComponent = humanoid;
        heightScale.CharacterMovementComponent = pawnRoot.GetComponent<CharacterMovement3DComponent>();
        VRPlayerCharacterComponent player = pawnRoot.GetComponent<VRPlayerCharacterComponent>() ?? pawnRoot.AddComponent<VRPlayerCharacterComponent>()!;
        player.HumanoidComponent = humanoid;
        player.IKSolver = solver;
        player.HeightScaleComponent = heightScale;
        player.CharacterMovementComponent = heightScale.CharacterMovementComponent;
        player.Headset = pawnRoot.FindDescendantByName("VRHeadsetNode")?.Transform as VRHeadsetTransform;
        player.LeftController = pawnRoot.FindDescendantByName("VRLeftControllerNode")?.Transform as VRControllerTransform;
        player.RightController = pawnRoot.FindDescendantByName("VRRightControllerNode")?.Transform as VRControllerTransform;
        player.TrackerCollection = pawnRoot.FindDescendantByName("VRTrackerCollectionNode")?.GetComponent<VRTrackerCollectionComponent>();
        player.PlayspaceRoot = player.Headset?.Parent as Transform;
        player.EyeLBoneName = "Eye_L";
        player.EyeRBoneName = "Eye_R";
        player.EyesModelResolveName = "Face";
        player.AvatarIdentity = avatarIdentity ?? humanoid.AvatarDefinition.DefinitionContentSha256;
        player.InitializeRig();
        if (pawnRoot.GetComponent<VrCalibrationFeedbackComponent>() is null
            && pawnRoot.FindDescendantByName("VR Calibration Feedback") is null)
            BootstrapVrCalibrationFeedbackFactory.Create(pawnRoot, player);
        if (player.Spectator is null)
        {
            player.Spectator = BootstrapVrSpectatorFactory.Create(pawnRoot, pawnRoot.Transform, player);
            player.SpectatorEnabled = Engine.UserSettings.VrSpectatorEnabled;
        }
        player.Spectator.Player = null;
        player.Spectator.Player = player;
        player.Spectator.ClearFirstPersonVisibility();
        if (Engine.UserSettings.VrFirstPersonAvatarLayer is >= 0 and < 31
                && player.Headset?.SceneNode?.GetComponent<VRHeadsetComponent>() is { } headset)
            BootstrapVrSpectatorFactory.ConfigureFirstPersonVisibility(player.Spectator, avatarRoot, headset, Engine.UserSettings.VrFirstPersonAvatarLayer.Value);
        return player;
    }

    /// <summary>Instantiates only an explicitly configured prefab; reports a missing avatar instead of inventing one.</summary>
    public static VRPlayerCharacterComponent? TryCreateConfiguredAvatar(SceneNode pawnRoot, SceneNode avatarParent)
    {
        string? path = Engine.UserSettings.VrAvatarPrefabPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Debug.Animation("VR pawn has no configured avatar prefab. Headset/controller input is available; choose a humanoid prefab for body calibration.");
            return null;
        }
        SceneNode? instance = Engine.Assets.InstantiatePrefab(path, pawnRoot.World, avatarParent, false);
        if (instance is null)
        {
            Debug.Animation("The configured VR avatar prefab could not be instantiated.");
            return null;
        }
        HumanoidComponent? humanoid = instance.GetComponent<HumanoidComponent>();
        if (humanoid is null)
        {
            Debug.Animation("The configured VR avatar prefab needs a root HumanoidComponent.");
            instance.Destroy(true);
            return null;
        }
        return ConfigureImportedAvatar(pawnRoot, humanoid, avatarIdentity: path + ":" + humanoid.AvatarDefinition.DefinitionContentSha256);
    }
}
