using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    private enum ENetworkingPoseRole
    {
        Server,
        SendingClient,
        ReceivingClient,
    }

    public static XRWorld CreateNetworkingPoseWorld(bool setUI, bool isServer)
    {
        // Dedicated local multiplayer world focused on avatar/VRIK pose replication.
        // Keep enough systems active for humanoid rig + IK + networking while reducing unrelated noise,
        // and specialize the setup by network role.
        ENetworkingPoseRole role = ResolveNetworkingPoseRole();

        Toggles.WorldKind = UnitTestWorldKind.NetworkingPose;
        Toggles.EditorType = UnitTestEditorType.Native;
        Toggles.VRPawn = true;
        Toggles.EmulatedVRPawn = true;
        Toggles.Locomotion = true;
        Toggles.AddCharacterIK = true;
        Toggles.AllowEditingInVR = true;
        Toggles.AddCameraVRPickup = false;
        Toggles.AddPhysics = false;
        Toggles.PhysicsChain = false;
        Toggles.DirLight = true;
        Toggles.DirLight2 = false;
        Toggles.PointLight = false;
        Toggles.SpotLight = false;
        Toggles.LightProbe = false;
        Toggles.Mirror = false;
        Toggles.Spline = false;
        Toggles.DeferredDecal = false;
        Toggles.VideoStreaming = false;
        Toggles.UltralightWebView = false;

        switch (role)
        {
            case ENetworkingPoseRole.Server:
                // Server: focus on authoritative networking simulation with a stable spectator rig.
                Toggles.Locomotion = false;
                Toggles.ThirdPersonPawn = true;
                break;

            case ENetworkingPoseRole.SendingClient:
                // Sender: actively drives pose updates.
                Toggles.Locomotion = true;
                Toggles.ThirdPersonPawn = false;
                break;

            case ENetworkingPoseRole.ReceivingClient:
                // Receiver: observe replicated pose from a detached/spectator perspective.
                Toggles.Locomotion = false;
                Toggles.ThirdPersonPawn = true;
                break;
        }

        Debug.Out($"[World] Using dedicated NetworkingPose unit-test world as role={role}. allowEditingInVR={Toggles.AllowEditingInVR}.");
        return CreateUnitTestWorld(setUI, isServer);
    }

    private static ENetworkingPoseRole ResolveNetworkingPoseRole()
    {
        string? explicitRole = Environment.GetEnvironmentVariable("XRE_NETWORKING_POSE_ROLE");
        if (!string.IsNullOrWhiteSpace(explicitRole))
        {
            if (string.Equals(explicitRole, "server", StringComparison.OrdinalIgnoreCase))
                return ENetworkingPoseRole.Server;
            if (string.Equals(explicitRole, "sender", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(explicitRole, "sending", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(explicitRole, "sendingclient", StringComparison.OrdinalIgnoreCase))
                return ENetworkingPoseRole.SendingClient;
            if (string.Equals(explicitRole, "receiver", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(explicitRole, "receiving", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(explicitRole, "receivingclient", StringComparison.OrdinalIgnoreCase))
                return ENetworkingPoseRole.ReceivingClient;
        }

        string? netMode = Environment.GetEnvironmentVariable("XRE_NET_MODE");
        if (string.Equals(netMode, "Server", StringComparison.OrdinalIgnoreCase))
            return ENetworkingPoseRole.Server;

        if (TryGetBoolEnv("XRE_POSE_BROADCAST_ENABLED", out bool broadcastEnabled) && broadcastEnabled)
            return ENetworkingPoseRole.SendingClient;

        return ENetworkingPoseRole.ReceivingClient;
    }

    private static bool TryGetBoolEnv(string name, out bool value)
    {
        value = default;
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (raw == "1")
        {
            value = true;
            return true;
        }
        if (raw == "0")
        {
            value = false;
            return true;
        }

        return bool.TryParse(raw, out value);
    }
}