using XREngine.Networking;

namespace XREngine;

/// <summary>
/// Transitional application-facing realtime session hooks. Bootstrap adapts these
/// delegates to the Runtime.Core networking host boundary.
/// </summary>
public static partial class Engine
{
    public static Func<PlayerJoinRequest, ServerSessionContext?>? ServerSessionResolver { get; set; }
    public static Func<PlayerJoinRequest, ServerJoinAdmissionResult?>? ServerJoinAdmissionResolver { get; set; }
    public static Func<PlayerJoinRequest, ManagedAdmissionVerifier?>? ManagedAdmissionVerifierResolver { get; set; }
    public static Func<PlayerJoinRequest, ManagedAdmissionVerifier, Func<ServerJoinAdmissionResult, bool>, bool>? ManagedAdmissionCommit { get; set; }
    public static Action<ServerSessionPlayerEvent>? ServerPlayerConnected { get; set; }
    public static Action<ServerSessionPlayerEvent>? ServerPlayerDisconnected { get; set; }
    public static Action<ServerSessionPlayerEvent>? ServerPlayerHeartbeatObserved { get; set; }
    public static int? ServerMaximumPlayers { get; set; }
    public static System.Net.IPAddress? ServerBindAddress { get; set; }
    public static bool ServerRequiresManagedUdpTransport { get; set; }
}
