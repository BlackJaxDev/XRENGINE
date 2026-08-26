using XREngine.Components;
using XREngine.Input;
using XREngine.Scene;

namespace XREngine.Networking;

/// <summary>
/// Host-owned world operations used by the transport-neutral networking runtime.
/// Rendering and application composition provide the concrete implementation.
/// </summary>
public interface IRuntimeNetworkWorldContext
{
    XRWorld? TargetWorld { get; }
    GameMode? GameMode { get; set; }
    object WorldInstance { get; }
    PawnComponent? CreateRemotePawn(int serverPlayerIndex, string? displayName, bool serverOwned);
    void DestroyPawn(PawnComponent pawn);
}

/// <summary>
/// Application composition required by realtime networking. This boundary keeps
/// networking independent from renderer windows and input-integration types.
/// </summary>
public interface IRuntimeNetworkingHostServices
{
    string ProtocolVersion { get; }
    IReadOnlyList<IPawnController?> LocalPlayers { get; }
    IPawnController? CreateRemotePlayer(int serverPlayerIndex);
    void AddRemotePlayer(IPawnController player);
    void RemoveRemotePlayer(IPawnController player);
    IRuntimeNetworkWorldContext? ResolvePrimaryWorld();
    IRuntimeNetworkWorldContext? CreateWorldContext(object worldInstance);
    IRuntimeNetworkWorldContext? EnsureClientWorld(WorldSyncDescriptor descriptor);
    ServerJoinAdmissionResult? ResolveServerJoinAdmission(PlayerJoinRequest request);
    ServerSessionContext? ResolveServerSession(PlayerJoinRequest request);
    void NotifyServerPlayerConnected(ServerSessionPlayerEvent playerEvent);
    void NotifyServerPlayerDisconnected(ServerSessionPlayerEvent playerEvent);
    void NotifyServerPlayerHeartbeatObserved(ServerSessionPlayerEvent playerEvent);
}

/// <summary>
/// Process-wide installation point for application networking composition.
/// </summary>
public static class RuntimeNetworkingHostServices
{
    private static readonly object Sync = new();
    private sealed class DefaultRuntimeNetworkingHostServices : IRuntimeNetworkingHostServices
    {
        public string ProtocolVersion => typeof(RuntimeNetworkingHostServices).Assembly.GetName().Version?.ToString() ?? "dev";
        public IReadOnlyList<IPawnController?> LocalPlayers => Array.Empty<IPawnController?>();
        public IPawnController? CreateRemotePlayer(int serverPlayerIndex) => null;
        public void AddRemotePlayer(IPawnController player) { }
        public void RemoveRemotePlayer(IPawnController player) { }
        public IRuntimeNetworkWorldContext? ResolvePrimaryWorld() => null;
        public IRuntimeNetworkWorldContext? CreateWorldContext(object worldInstance) => null;
        public IRuntimeNetworkWorldContext? EnsureClientWorld(WorldSyncDescriptor descriptor) => null;
        public ServerJoinAdmissionResult? ResolveServerJoinAdmission(PlayerJoinRequest request) => null;
        public ServerSessionContext? ResolveServerSession(PlayerJoinRequest request) => null;
        public void NotifyServerPlayerConnected(ServerSessionPlayerEvent playerEvent) { }
        public void NotifyServerPlayerDisconnected(ServerSessionPlayerEvent playerEvent) { }
        public void NotifyServerPlayerHeartbeatObserved(ServerSessionPlayerEvent playerEvent) { }
    }

    private static readonly IRuntimeNetworkingHostServices Default = new DefaultRuntimeNetworkingHostServices();
    private static IRuntimeNetworkingHostServices _current = Default;
    private static long _generation;

    public static IRuntimeNetworkingHostServices Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static IDisposable Install(IRuntimeNetworkingHostServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (Sync)
        {
            long generation = ++_generation;
            _current = services;
            return new InstallationLease(generation);
        }
    }

    private sealed class InstallationLease(long generation) : IDisposable
    {
        private long _generation = generation;

        public void Dispose()
        {
            long installedGeneration = Interlocked.Exchange(ref _generation, 0L);
            if (installedGeneration == 0L)
                return;

            lock (Sync)
            {
                if (RuntimeNetworkingHostServices._generation != installedGeneration)
                    return;

                _current = Default;
                ++RuntimeNetworkingHostServices._generation;
            }
        }
    }
}
