using System.Numerics;
using XREngine.Components;
using XREngine.Input.Devices;
using XREngine.Networking;
using XREngine.Scene.Transforms;

namespace XREngine.Input
{
    /// <summary>
    /// Represents a player that is controlled remotely by another client.
    /// All movement/state comes from network messages instead of local input devices.
    /// </summary>
    public class RemotePlayerController : PlayerController<ServerInputInterface>
    {
        public RemotePlayerController(int serverPlayerIndex) : base(new ServerInputInterface(serverPlayerIndex))
        {
            PlayerInfo.ServerIndex = serverPlayerIndex;
        }

        public int ServerPlayerIndex => PlayerInfo.ServerIndex;

        /// <summary>
        /// Applies the latest transform information received from the server.
        /// </summary>
        public void ApplyNetworkTransform(PlayerTransformUpdate update)
        {
            ApplyNetworkTransform(update.Translation, update.Rotation);
        }

        /// <summary>
        /// Applies a raw transform update to the controlled pawn, if present.
        /// </summary>
        public void ApplyNetworkTransform(Vector3 translation, Quaternion rotation)
        {
            if (ControlledPawn?.SceneNode?.Transform is Transform transform)
            {
                transform.TargetTranslation = translation;
                transform.TargetRotation = rotation;
            }
        }

        protected override void RegisterInput(InputInterface input)
        {
            // Remote controllers do not register local input; all input/state comes from the network.
        }
    }
}
