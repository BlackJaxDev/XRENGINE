using MemoryPack;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Input;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>
/// Defines host-independent rules and lifecycle behavior for a game session.
/// Scene construction and camera-specific spawning are delegated to <see cref="RuntimeGameModeHostServices"/>.
/// </summary>
public abstract class GameMode : XRAsset
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    protected Type? _defaultPlayerControllerClass;
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    protected Type? _defaultPlayerPawnClass;
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    protected Type? _defaultPlayerUserInterfaceClass;

    public Type? PlayerControllerClass => _defaultPlayerControllerClass;
    public Type? PlayerPawnClass => _defaultPlayerPawnClass ?? RuntimeGameModeHostServices.Current?.DefaultPawnType;
    public Type? PlayerUserInterfaceClass => _defaultPlayerUserInterfaceClass;

    /// <summary>
    /// True when <see cref="CreateDefaultPawn"/> constructs the pawn at its final spawn pose.
    /// </summary>
    protected virtual bool DefaultPawnAppliesSpawnTransform => false;

    [YamlIgnore, MemoryPackIgnore]
    [Description("The world instance this GameMode is managing.")]
    public object? WorldInstance { get; internal set; }

    [YamlIgnore, MemoryPackIgnore]
    [Description("Queue of pending possessions per pawn.")]
    public Dictionary<XRComponent, Queue<ELocalPlayerIndex>> PossessionQueue { get; } = [];

    private HashSet<XRComponent> AutoSpawnedPawns { get; } = [];
    private Dictionary<XRComponent, XRComponent> AutoSpawnedPlayerUserInterfaces { get; } = [];

    public bool IsActive { get; private set; }

    protected void TrackAutoSpawnedPawn(XRComponent pawn) => AutoSpawnedPawns.Add(pawn);

    public virtual void OnBeginPlay()
    {
        IsActive = true;
        IRuntimeGameModeHostServices? host = RuntimeGameModeHostServices.Current;
        if (host?.AutoSpawnPlayer == true)
            SpawnDefaultPlayerPawn(host.DefaultPlayerIndex);

        Debug.Out($"GameMode.OnBeginPlay - World: {host?.GetWorldName(WorldInstance) ?? "null"}");
    }

    public virtual void OnEndPlay()
    {
        foreach (XRComponent pawn in PossessionQueue.Keys)
            if (pawn is IRuntimeGameModePawn runtimePawn)
                runtimePawn.RuntimePreUnpossessed -= OnPawnUnpossessing;

        PossessionQueue.Clear();

        IRuntimeGameModeHostServices? host = RuntimeGameModeHostServices.Current;
        if (host is not null)
            foreach ((XRComponent pawn, XRComponent userInterface) in AutoSpawnedPlayerUserInterfaces)
                host.DestroyPlayerUserInterface(pawn, userInterface);

        AutoSpawnedPlayerUserInterfaces.Clear();

        IRuntimePlayerControllerServices? players = RuntimePlayerControllerServices.Current;
        if (players is not null)
            foreach (IPawnController? player in players.AllLocalPlayers)
                if (player?.ControlledPawnComponent is not null)
                    player.ControlledPawnComponent = null;

        if (host is not null)
            foreach (XRComponent pawn in AutoSpawnedPawns)
                host.DestroyPawn(pawn);

        AutoSpawnedPawns.Clear();
        IsActive = false;
        Debug.Out($"GameMode.OnEndPlay - World: {host?.GetWorldName(WorldInstance) ?? "null"}");
    }

    public virtual void Tick(float deltaTime) { }
    public virtual void FixedTick(float fixedDeltaTime) { }

    public virtual XRComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex)
    {
        Type? pawnType = PlayerPawnClass;
        if (pawnType is null)
            return null;

        if (WorldInstance is null)
        {
            Debug.LogWarning("Cannot spawn default pawn without an active world instance.");
            return null;
        }

        IRuntimeGameModeHostServices? host = RuntimeGameModeHostServices.Current;
        if (host is null)
        {
            Debug.LogWarning("Cannot spawn default pawn without a runtime game-mode host.");
            return null;
        }

        XRComponent? pawn = host.CreatePawn(WorldInstance, $"Player{(int)playerIndex + 1}_Pawn", pawnType);
        if (pawn is null)
            Debug.LogWarning($"Failed to create pawn of type {pawnType.FullName ?? pawnType.Name} for player {playerIndex}.");
        return pawn;
    }

    public virtual (Vector3 Position, Quaternion Rotation) GetSpawnPoint(ELocalPlayerIndex playerIndex)
        => RuntimeGameModeHostServices.Current?.GetSpawnPoint(playerIndex)
            ?? (Vector3.Zero, Quaternion.Identity);

    protected virtual XRComponent? SpawnDefaultPlayerPawn(ELocalPlayerIndex playerIndex)
    {
        EnsureLocalPlayerController(playerIndex);
        XRComponent? pawn = CreateDefaultPawn(playerIndex);
        if (pawn is null)
            return null;

        TrackAutoSpawnedPawn(pawn);
        if (!DefaultPawnAppliesSpawnTransform)
        {
            (Vector3 position, Quaternion rotation) = GetSpawnPoint(playerIndex);
            RuntimeGameModeHostServices.Current?.ApplySpawnTransform(pawn, position, rotation);
        }

        XRComponent? userInterface = CreateDefaultPlayerUserInterface(playerIndex, pawn);
        if (userInterface is not null)
            AutoSpawnedPlayerUserInterfaces[pawn] = userInterface;

        ForcePossession(pawn, playerIndex);
        Debug.Out($"Spawned default pawn for player {playerIndex}");
        return pawn;
    }

    /// <summary>
    /// Creates the runtime user interface declared by this game mode for a newly spawned player pawn.
    /// The host binds the returned component to the pawn's camera and input path.
    /// </summary>
    public virtual XRComponent? CreateDefaultPlayerUserInterface(
        ELocalPlayerIndex playerIndex,
        XRComponent pawn)
    {
        Type? userInterfaceType = PlayerUserInterfaceClass;
        if (userInterfaceType is null)
            return null;

        if (WorldInstance is null)
        {
            Debug.LogWarning("Cannot spawn a player user interface without an active world instance.");
            return null;
        }

        IRuntimeGameModeHostServices? host = RuntimeGameModeHostServices.Current;
        if (host is null)
        {
            Debug.LogWarning("Cannot spawn a player user interface without a runtime game-mode host.");
            return null;
        }

        XRComponent? userInterface = host.CreatePlayerUserInterface(
            WorldInstance,
            $"Player{(int)playerIndex + 1}_UserInterface",
            userInterfaceType,
            pawn);
        if (userInterface is null)
            Debug.LogWarning(
                $"Failed to create player user interface of type {userInterfaceType.FullName ?? userInterfaceType.Name} " +
                $"for player {playerIndex}.");

        return userInterface;
    }

    public void ForcePossession(XRComponent pawn, ELocalPlayerIndex possessor)
    {
        IPawnController? player = RuntimePlayerControllerServices.Current?.GetOrCreateLocalPlayer(possessor);
        if (player is null)
        {
            Debug.LogWarning($"Failed to possess pawn: could not resolve local player for index {possessor}");
            return;
        }

        player.ControlledPawnComponent = pawn;
    }

    public void EnqueuePossession(XRComponent pawn, ELocalPlayerIndex possessor)
    {
        if (pawn is not IRuntimeGameModePawn runtimePawn || runtimePawn.Controller is null)
        {
            ForcePossession(pawn, possessor);
            return;
        }

        if (!PossessionQueue.TryGetValue(pawn, out Queue<ELocalPlayerIndex>? queue))
            PossessionQueue[pawn] = queue = new Queue<ELocalPlayerIndex>();

        queue.Enqueue(possessor);
        runtimePawn.RuntimePreUnpossessed -= OnPawnUnpossessing;
        runtimePawn.RuntimePreUnpossessed += OnPawnUnpossessing;
    }

    private void OnPawnUnpossessing(XRComponent pawn)
    {
        if (!PossessionQueue.TryGetValue(pawn, out Queue<ELocalPlayerIndex>? queue) || queue.Count == 0)
            return;

        ELocalPlayerIndex possessor = queue.Dequeue();
        if (queue.Count == 0)
        {
            PossessionQueue.Remove(pawn);
            if (pawn is IRuntimeGameModePawn runtimePawn)
                runtimePawn.RuntimePreUnpossessed -= OnPawnUnpossessing;
        }

        ForcePossession(pawn, possessor);
    }

    protected virtual IPawnController? EnsureLocalPlayerController(ELocalPlayerIndex playerIndex)
        => RuntimePlayerControllerServices.Current?.GetOrCreateLocalPlayer(playerIndex, _defaultPlayerControllerClass);
}
