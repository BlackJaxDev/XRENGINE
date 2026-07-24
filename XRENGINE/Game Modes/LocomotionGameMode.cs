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
        private Vector3? _spawnPositionOverride;
        private Quaternion? _spawnRotationOverride;

        public LocomotionGameMode()
        {
            _defaultPlayerPawnClass = typeof(CharacterPawnComponent);
            _defaultPlayerUserInterfaceClass = typeof(UICanvasComponent);
        }

        protected override bool DefaultPawnAppliesSpawnTransform => true;

        /// <summary>
        /// Optional authored world position for the locomotion pawn. When unset, the current
        /// editor camera/player position remains the spawn source.
        /// </summary>
        public Vector3? SpawnPositionOverride
        {
            get => _spawnPositionOverride;
            set => SetField(ref _spawnPositionOverride, value);
        }

        /// <summary>
        /// Optional authored world rotation for the locomotion pawn.
        /// </summary>
        public Quaternion? SpawnRotationOverride
        {
            get => _spawnRotationOverride;
            set => SetField(ref _spawnRotationOverride, value);
        }

        public override (Vector3 Position, Quaternion Rotation) GetSpawnPoint(ELocalPlayerIndex playerIndex)
        {
            (Vector3 position, Quaternion rotation) = base.GetSpawnPoint(playerIndex);
            return (SpawnPositionOverride ?? position, SpawnRotationOverride ?? rotation);
        }

        public override PawnComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex)
        {
            if (WorldInstance is not XRWorldInstance worldInstance)
                return null;

            var pawnNodeName = $"Player{(int)playerIndex + 1}_CharacterPawn";
            var pawnNode = new SceneNode(worldInstance, pawnNodeName);

            // CharacterMovement captures its initial native-controller position when the component
            // activates. Seed the transform before adding the component/root so the controller is
            // never created at the origin and then overwritten by the delayed spawn transform.
            (Vector3 spawnPosition, Quaternion spawnRotation) = GetSpawnPoint(playerIndex);
            var rbTfm = pawnNode.SetTransform<RigidBodyTransform>();
            rbTfm.InterpolationMode = RigidBodyTransform.EInterpolationMode.Interpolate;
            rbTfm.SetPositionAndRotation(spawnPosition, Quaternion.Identity);

            if (pawnNode.AddComponent(typeof(CharacterPawnComponent)) is not CharacterPawnComponent pawn)
            {
                pawnNode.Destroy();
                return null;
            }

            // Simple first-person-ish camera setup on a child node.
            var cameraOffsetNode = pawnNode.NewChild("CameraOffset");
            var cameraOffsetTfm = cameraOffsetNode.SetTransform<Transform>();
            cameraOffsetTfm.Translation = new Vector3(0.0f, 1.7f, 0.0f);
            cameraOffsetTfm.Rotation = spawnRotation;

            var cameraNode = cameraOffsetNode.NewChild("Camera");
            var camTfm = cameraNode.SetTransform<Transform>();
            _ = camTfm;

            var camera = cameraNode.AddComponent<CameraComponent>("PlayerCamera");
            camera?.SetPerspective(60.0f, 0.1f, 100000.0f, null);

            pawn.CameraComponent = camera;
            pawn.InputOrientationTransform = cameraNode.Transform;
            pawn.ViewRotationTransform = cameraOffsetTfm;

            worldInstance.RootNodes.Add(pawnNode);

            return pawn;
        }
    }
}
