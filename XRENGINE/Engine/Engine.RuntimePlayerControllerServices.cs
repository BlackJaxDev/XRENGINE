using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using XREngine.Input;

namespace XREngine;

/// <summary>
/// Engine-side implementation of <see cref="IRuntimePlayerControllerServices"/>
/// that delegates to <see cref="Engine.State"/>.
/// </summary>
internal sealed class EngineRuntimePlayerControllerServices : IRuntimePlayerControllerServices
{
    public event Action<IPawnController>? LocalPlayerAdded;
    public event Action<IPawnController>? LocalPlayerRemoved;

    internal EngineRuntimePlayerControllerServices()
    {
        Engine.State.LocalPlayerAdded += player => LocalPlayerAdded?.Invoke(player);
        Engine.State.LocalPlayerRemoved += player => LocalPlayerRemoved?.Invoke(player);
    }

    public IPawnController? GetLocalPlayer(ELocalPlayerIndex index)
        => Engine.State.GetLocalPlayer(index);

    public IPawnController GetOrCreateLocalPlayer(
        ELocalPlayerIndex index,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        Type? controllerTypeOverride = null)
        => Engine.State.GetOrCreateLocalPlayer(index, controllerTypeOverride);

    public bool RemoveLocalPlayer(ELocalPlayerIndex index)
        => Engine.State.RemoveLocalPlayer(index);

    public IPawnController MainPlayer
        => Engine.State.MainPlayer;

    public int LocalPlayerCount
        => Engine.State.LocalPlayers.Count(static p => p is not null);

    public IReadOnlyList<IPawnController?> AllLocalPlayers
        => Engine.State.LocalPlayers;

    public IPawnController CreateRemotePlayer(int serverPlayerIndex)
        => Engine.State.InstantiateRemoteController(serverPlayerIndex);

    public IReadOnlyList<IPawnController> RemotePlayers
        => Engine.State.RemotePlayers;

    public void AddRemotePlayer(IPawnController player)
    {
        if (!Engine.State.RemotePlayers.Contains(player))
            Engine.State.RemotePlayers.Add(player);
    }

    public bool RemoveRemotePlayer(IPawnController player)
        => Engine.State.RemotePlayers.Remove(player);
}
