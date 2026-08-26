using XREngine.Components;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Adapts Bootstrap-owned world hosts and controller composition to the lower
/// Runtime.Core networking contract.
/// </summary>
internal sealed class EngineRuntimeNetworkingHostServices : IRuntimeNetworkingHostServices
{
    public string ProtocolVersion => typeof(Engine).Assembly.GetName().Version?.ToString() ?? "dev";
    public IReadOnlyList<IPawnController?> LocalPlayers => Engine.State.LocalPlayers;

    public IRuntimeNetworkWorldContext? ResolvePrimaryWorld()
    {
        foreach (var window in RuntimeEngine.Windows)
            if (window?.TargetWorldInstance?.WorldContext is RuntimeWorld world)
                return new EngineRuntimeNetworkWorldContext(world);

        return RuntimeWorldRegistryServices.Current?.Snapshot().Values.FirstOrDefault() is { } fallback
            ? new EngineRuntimeNetworkWorldContext(fallback)
            : null;
    }

    public IRuntimeNetworkWorldContext? CreateWorldContext(object worldInstance)
        => worldInstance is RuntimeWorld world ? new EngineRuntimeNetworkWorldContext(world) : null;

    public IRuntimeNetworkWorldContext? EnsureClientWorld(WorldSyncDescriptor descriptor)
    {
        XRWorld world = new() { Name = string.IsNullOrWhiteSpace(descriptor.WorldName) ? "RemoteWorld" : descriptor.WorldName! };
        RuntimeWorld instance = RuntimeWorldHostServices.Current?.GetOrCreate(world)
            ?? throw new InvalidOperationException("Bootstrap world-host services are not installed.");
        IRuntimeRenderWorld? renderWorld = RuntimeRenderWorldRegistry.Get(instance);
        foreach (var window in RuntimeEngine.Windows)
        {
            if (window is null)
                continue;

            window.TargetWorldInstance ??= renderWorld;
            break;
        }

        return new EngineRuntimeNetworkWorldContext(instance);
    }

    public IPawnController? CreateRemotePlayer(int serverPlayerIndex) => Engine.State.InstantiateRemoteController(serverPlayerIndex);
    public void AddRemotePlayer(IPawnController player)
    {
        if (!Engine.State.RemotePlayers.Contains(player))
            Engine.State.RemotePlayers.Add(player);
    }
    public void RemoveRemotePlayer(IPawnController player) => Engine.State.RemotePlayers.Remove(player);
    public ServerJoinAdmissionResult? ResolveServerJoinAdmission(PlayerJoinRequest request) => Engine.ServerJoinAdmissionResolver?.Invoke(request);
    public ServerSessionContext? ResolveServerSession(PlayerJoinRequest request) => Engine.ServerSessionResolver?.Invoke(request);
    public void NotifyServerPlayerConnected(ServerSessionPlayerEvent playerEvent) => Engine.ServerPlayerConnected?.Invoke(playerEvent);
    public void NotifyServerPlayerDisconnected(ServerSessionPlayerEvent playerEvent) => Engine.ServerPlayerDisconnected?.Invoke(playerEvent);
    public void NotifyServerPlayerHeartbeatObserved(ServerSessionPlayerEvent playerEvent) => Engine.ServerPlayerHeartbeatObserved?.Invoke(playerEvent);
}

internal sealed class EngineRuntimeNetworkWorldContext(RuntimeWorld world) : IRuntimeNetworkWorldContext
{
    public XRWorld? TargetWorld => world.TargetWorld;
    public GameMode? GameMode { get => world.GameMode; set => world.GameMode = value; }
    public object WorldInstance => world;

    public PawnComponent? CreateRemotePawn(int serverPlayerIndex, string? displayName, bool serverOwned)
    {
        Type pawnType = world.GameMode?.PlayerPawnClass ?? typeof(FlyingCameraPawnComponent);
        string fallbackName = serverOwned ? $"ServerPlayer_{serverPlayerIndex}" : $"RemotePlayer_{serverPlayerIndex}";
        SceneNode node = new(world, string.IsNullOrWhiteSpace(displayName) ? fallbackName : displayName!);
        if (node.AddComponent(pawnType) is not PawnComponent pawn)
        {
            node.Destroy();
            return null;
        }

        world.RootNodes.Add(node);
        return pawn;
    }

    public void DestroyPawn(PawnComponent pawn)
    {
        SceneNode? node = pawn.SceneNode;
        if (node is null)
            return;

        world.RootNodes.Remove(node);
        node.Destroy();
    }
}
