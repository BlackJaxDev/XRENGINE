using System.Numerics;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine
{
    /// <summary>
    /// Game mode intended for desktop character-controller testing.
    /// Derives from <see cref="FlyingCameraGameMode"/> so noclip can be enabled at any time.
    /// </summary>
    public class LocomotionGameMode : FlyingCameraGameMode
    {
        public LocomotionGameMode()
        {
            _defaultPlayerPawnClass = typeof(CharacterPawnComponent);
        }

        public override PawnComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex)
        {
            if (WorldInstance is null)
                return null;

            var pawnNodeName = $"Player{(int)playerIndex + 1}_CharacterPawn";
            var pawnNode = new SceneNode(WorldInstance, pawnNodeName);

            // Match UnitTestingWorld: prefer a rigid-body transform for smoother physics interpolation.
            var rbTfm = pawnNode.SetTransform<RigidBodyTransform>();
            rbTfm.InterpolationMode = RigidBodyTransform.EInterpolationMode.Interpolate;

            if (pawnNode.AddComponent(typeof(CharacterPawnComponent)) is not CharacterPawnComponent pawn)
            {
                pawnNode.Destroy();
                return null;
            }

            // Simple first-person-ish camera setup on a child node.
            var cameraOffsetNode = pawnNode.NewChild("CameraOffset");
            var cameraOffsetTfm = cameraOffsetNode.SetTransform<Transform>();
            cameraOffsetTfm.Translation = new Vector3(0.0f, 1.7f, 0.0f);

            var cameraNode = cameraOffsetNode.NewChild("Camera");
            var camTfm = cameraNode.SetTransform<Transform>();
            _ = camTfm;

            var camera = cameraNode.AddComponent<CameraComponent>("PlayerCamera");
            camera?.SetPerspective(60.0f, 0.1f, 100000.0f, null);

            pawn.CameraComponent = camera;
            pawn.InputOrientationTransform = cameraNode.Transform;
            pawn.ViewRotationTransform = cameraOffsetTfm;

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
