using XREngine.Components;
using XREngine.Data.Components.Scene;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine
{
    /// <summary>
    /// VR-focused game mode that spawns a character pawn and a minimal VR rig (HMD + controllers).
    /// This is intentionally lightweight and meant as a reasonable default.
    /// </summary>
    public class VRGameMode : GameMode
    {
        public VRGameMode()
        {
            _defaultPlayerPawnClass = typeof(CharacterPawnComponent);
        }

        public override PawnComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex)
        {
            if (WorldInstance is null)
                return null;

            var pawnNodeName = $"Player{(int)playerIndex + 1}_VRPawn";
            var pawnNode = new SceneNode(WorldInstance, pawnNodeName);

            var rbTfm = pawnNode.SetTransform<RigidBodyTransform>();
            rbTfm.InterpolationMode = RigidBodyTransform.EInterpolationMode.Interpolate;

            if (pawnNode.AddComponent<CharacterPawnComponent>() is not CharacterPawnComponent pawn)
            {
                pawnNode.Destroy();
                return null;
            }

            // Add VR input set (will auto-require CharacterPawnComponent).
            _ = pawnNode.AddComponent(typeof(VRPlayerInputSet));

            // Create HMD node.
            var hmdNode = pawnNode.NewChild("VRHeadset");
            _ = hmdNode.SetTransform<VRHeadsetTransform>();
            _ = hmdNode.AddComponent<VRHeadsetComponent>("VR Headset");

            // Create controller nodes.
            var leftNode = pawnNode.NewChild("VRLeftController");
            var leftTfm = leftNode.SetTransform<VRControllerTransform>();
            leftTfm.LeftHand = true;
            var leftModel = leftNode.AddComponent<Components.VR.VRControllerModelComponent>("Left Controller Model");
            leftModel?.LeftHand = true;

            var rightNode = pawnNode.NewChild("VRRightController");
            var rightTfm = rightNode.SetTransform<VRControllerTransform>();
            rightTfm.LeftHand = false;
            var rightModel = rightNode.AddComponent<Components.VR.VRControllerModelComponent>("Right Controller Model");
            rightModel?.LeftHand = false;

            // Tracker collection node (optional devices).
            _ = pawnNode.NewChild("VRTrackers").AddComponent<VRTrackerCollectionComponent>();

            WorldInstance.RootNodes.Add(pawnNode);

            if (WorldInstance.PlayState == XRWorldInstance.EPlayState.Playing)
            {
                pawnNode.OnBeginPlay();
                if (pawnNode.IsActiveSelf)
                    pawnNode.OnActivated();
            }

            return pawn;
        }
    }
}
