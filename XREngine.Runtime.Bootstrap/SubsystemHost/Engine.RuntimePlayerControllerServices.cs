using System;
using XREngine.Input;
using XREngine.Runtime.InputIntegration;

namespace XREngine;

/// <summary>
/// Bootstrap-installed controller registry. Concrete local and remote controller
/// ownership remains in Runtime.InputIntegration rather than the facade state.
/// </summary>
internal sealed class EngineRuntimePlayerControllerServices : IRuntimePlayerControllerServices, IDisposable
{
    private readonly InputIntegrationPlayerControllerServices _registry = new();

    internal EngineRuntimePlayerControllerServices()
    {
        _registry.LocalPlayerAdded += SynchronizeAddedPlayer;
        _registry.LocalPlayerRemoved += SynchronizeRemovedPlayer;
    }

    public event Action<IPawnController>? LocalPlayerAdded
    {
        add => _registry.LocalPlayerAdded += value;
        remove => _registry.LocalPlayerAdded -= value;
    }

    public event Action<IPawnController>? LocalPlayerRemoved
    {
        add => _registry.LocalPlayerRemoved += value;
        remove => _registry.LocalPlayerRemoved -= value;
    }

    public IPawnController? GetLocalPlayer(ELocalPlayerIndex index) => _registry.GetLocalPlayer(index);
    public IPawnController GetOrCreateLocalPlayer(ELocalPlayerIndex index, Type? controllerTypeOverride = null) => _registry.GetOrCreateLocalPlayer(index, controllerTypeOverride);
    public bool RemoveLocalPlayer(ELocalPlayerIndex index) => _registry.RemoveLocalPlayer(index);
    public IPawnController MainPlayer => _registry.MainPlayer;
    public int LocalPlayerCount => _registry.LocalPlayerCount;
    public IReadOnlyList<IPawnController?> AllLocalPlayers => _registry.AllLocalPlayers;
    public IPawnController CreateRemotePlayer(int serverPlayerIndex) => _registry.CreateRemotePlayer(serverPlayerIndex);
    public IReadOnlyList<IPawnController> RemotePlayers => _registry.RemotePlayers;
    public void AddRemotePlayer(IPawnController player) => _registry.AddRemotePlayer(player);
    public bool RemoveRemotePlayer(IPawnController player) => _registry.RemoveRemotePlayer(player);
    public void Dispose()
    {
        _registry.Dispose();
        _registry.LocalPlayerAdded -= SynchronizeAddedPlayer;
        _registry.LocalPlayerRemoved -= SynchronizeRemovedPlayer;
    }

    private static void SynchronizeAddedPlayer(IPawnController player)
    {
        if (player.LocalPlayerIndex is ELocalPlayerIndex index)
            Engine.State.SynchronizeCompatibilityLocalPlayer(index, player);
    }

    private static void SynchronizeRemovedPlayer(IPawnController player)
    {
        if (player.LocalPlayerIndex is ELocalPlayerIndex index)
            Engine.State.SynchronizeCompatibilityLocalPlayer(index, null);
    }
}
