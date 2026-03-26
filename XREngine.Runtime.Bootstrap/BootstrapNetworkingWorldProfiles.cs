namespace XREngine.Runtime.Bootstrap;

public static class BootstrapNetworkingWorldProfiles
{
    private enum ENetworkingPoseRole
    {
        Server,
        SendingClient,
        ReceivingClient,
    }

    public static void ApplyNetworkingPoseProfile()
    {
        ENetworkingPoseRole role = ResolveNetworkingPoseRole();
        var settings = RuntimeBootstrapState.Settings;

        settings.WorldKind = UnitTestWorldKind.NetworkingPose;
        settings.EditorType = UnitTestEditorType.Native;
        settings.VRPawn = true;
        settings.EmulatedVRPawn = true;
        settings.Locomotion = true;
        settings.AddCharacterIK = true;
        settings.AllowEditingInVR = true;
        settings.AddCameraVRPickup = false;
        settings.AddPhysics = false;
        settings.PhysicsChain = false;
        settings.DirLight = true;
        settings.DirLight2 = false;
        settings.PointLight = false;
        settings.SpotLight = false;
        settings.LightProbe = LightProbeMode.Off;
        settings.Mirror = false;
        settings.Spline = false;
        settings.DeferredDecal = false;
        settings.VideoStreaming = false;
        settings.UltralightWebView = false;

        switch (role)
        {
            case ENetworkingPoseRole.Server:
                settings.Locomotion = false;
                settings.ThirdPersonPawn = true;
                break;
            case ENetworkingPoseRole.SendingClient:
                settings.Locomotion = true;
                settings.ThirdPersonPawn = false;
                break;
            case ENetworkingPoseRole.ReceivingClient:
                settings.Locomotion = false;
                settings.ThirdPersonPawn = true;
                break;
        }

        Debug.Out($"[World] Using dedicated NetworkingPose unit-test world as role={role}. allowEditingInVR={settings.AllowEditingInVR}.");
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