using XREngine.Components;
using XREngine.Components.Animation;
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
    public void EnqueueSimulation(Action action) => Engine.EnqueueSimulationBoundaryTask(action);
    public IReadOnlyList<IPawnController?> LocalPlayers
        => RuntimePlayerControllerServices.Current?.AllLocalPlayers ?? Array.Empty<IPawnController?>();

    public IRuntimeNetworkWorldContext? ResolvePrimaryWorld()
    {
        foreach (var window in RuntimeEngine.Windows)
            if (window?.TargetWorldInstance?.WorldContext is RuntimeWorld world)
                return EngineRuntimeNetworkWorldContext.Get(world);

        return RuntimeWorldRegistryServices.Current?.Snapshot().Values.FirstOrDefault() is { } fallback
            ? EngineRuntimeNetworkWorldContext.Get(fallback)
            : null;
    }

    public IRuntimeNetworkWorldContext? CreateWorldContext(object worldInstance)
        => worldInstance is RuntimeWorld world ? EngineRuntimeNetworkWorldContext.Get(world) : null;

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

        return EngineRuntimeNetworkWorldContext.Get(instance);
    }

    public IPawnController? CreateRemotePlayer(int serverPlayerIndex)
        => RuntimePlayerControllerServices.Current?.CreateRemotePlayer(serverPlayerIndex);
    public void AddRemotePlayer(IPawnController player)
        => RuntimePlayerControllerServices.Current?.AddRemotePlayer(player);
    public void RemoveRemotePlayer(IPawnController player)
        => RuntimePlayerControllerServices.Current?.RemoveRemotePlayer(player);
    public ServerJoinAdmissionResult? ResolveServerJoinAdmission(PlayerJoinRequest request) => Engine.ServerJoinAdmissionResolver?.Invoke(request);
    public bool TryGetManagedAdmissionVerifier(PlayerJoinRequest request, out ManagedAdmissionVerifier verifier)
    {
        verifier = Engine.ManagedAdmissionVerifierResolver?.Invoke(request)!;
        return verifier is not null;
    }
    public bool CommitManagedAdmission(PlayerJoinRequest request, ManagedAdmissionVerifier verifier, Func<ServerJoinAdmissionResult, bool> admit)
        => Engine.ManagedAdmissionCommit?.Invoke(request, verifier, admit) == true;
    public ServerSessionContext? ResolveServerSession(PlayerJoinRequest request) => Engine.ServerSessionResolver?.Invoke(request);
    public void NotifyServerPlayerConnected(ServerSessionPlayerEvent playerEvent) => Engine.ServerPlayerConnected?.Invoke(playerEvent);
    public void NotifyServerPlayerDisconnected(ServerSessionPlayerEvent playerEvent) => Engine.ServerPlayerDisconnected?.Invoke(playerEvent);
    public void NotifyServerPlayerHeartbeatObserved(ServerSessionPlayerEvent playerEvent) => Engine.ServerPlayerHeartbeatObserved?.Invoke(playerEvent);
}

internal sealed class EngineRuntimeNetworkWorldContext(RuntimeWorld world) : IRuntimeNetworkWorldContext
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RuntimeWorld, EngineRuntimeNetworkWorldContext> Contexts = new();
    internal static EngineRuntimeNetworkWorldContext Get(RuntimeWorld world) => Contexts.GetValue(world, static value => new(value));
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RuntimeWorld, SceneReplicatedEntityWorld> ReplicatedWorlds = new();
    public XRWorld? TargetWorld => world.TargetWorld;
    public GameMode? GameMode { get => world.GameMode; set => world.GameMode = value; }
    public object WorldInstance => world;
    public IReplicatedEntityWorld ReplicatedEntityWorld => ReplicatedWorlds.GetValue(world, static value => new SceneReplicatedEntityWorld(value));

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

    public void BindRemotePawn(PawnComponent pawn, Guid sessionId, string clientId, int serverPlayerIndex)
    {
        if (pawn.SceneNode is not { } node
            || sessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(clientId)
            || serverPlayerIndex is <= 0 or > ushort.MaxValue)
        {
            return;
        }

        // The default remote pawn is a camera pawn. Add the opt-in avatar pose
        // consumer here so games can replace it with a richer pawn factory without
        // weakening the managed pose identity binding.
        HumanoidComponent humanoid = node.GetComponent<HumanoidComponent>() ?? node.AddComponent<HumanoidComponent>()!;
        VRIKSolverComponent solver = node.GetComponent<VRIKSolverComponent>() ?? node.AddComponent<VRIKSolverComponent>()!;
        _ = humanoid;
        solver.BindNetworkPose(sessionId, clientId, (ushort)serverPlayerIndex);
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
