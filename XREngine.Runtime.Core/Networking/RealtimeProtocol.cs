namespace XREngine.Networking;

/// <summary>
/// The realtime wire-protocol version. It identifies the binary layout of realtime frames and is
/// independent of the build version string, so two builds interoperate exactly when their wire
/// versions match.
/// </summary>
public static class RealtimeProtocol
{
    /// <summary>
    /// Current wire version. Version 2 introduced binary state-change frames and the binary
    /// humanoid pose, clock-sync, and replication-delta packets. Increment it for every change to
    /// a frame or packet layout.
    /// </summary>
    public const int WireVersion = 2;

    /// <summary>True when a peer's advertised wire version can exchange frames with this build.</summary>
    public static bool IsCompatible(int remoteWireVersion)
        => remoteWireVersion == WireVersion;

    /// <summary>Builds the diagnostic used by join and admission rejection. It names both versions.</summary>
    public static string DescribeMismatch(int remoteWireVersion)
        => $"Realtime wire protocol mismatch: this build speaks version {WireVersion}, the peer speaks version {remoteWireVersion}. Both sides must run builds with the same realtime wire protocol.";
}
