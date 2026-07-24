using System.Numerics;
using XREngine.Components;
using XREngine.Input;

namespace XREngine;

/// <summary>
/// Host operations used by <see cref="GameMode"/> that require scene, camera, or engine composition.
/// </summary>
public interface IRuntimeGameModeHostServices
{
    bool AutoSpawnPlayer { get; }
    ELocalPlayerIndex DefaultPlayerIndex { get; }
    Type? DefaultPawnType { get; }
    string? GetWorldName(object? worldInstance);
    XRComponent? CreatePawn(object worldInstance, string nodeName, Type pawnType);
    XRComponent? CreatePlayerUserInterface(
        object worldInstance,
        string nodeName,
        Type userInterfaceType,
        XRComponent pawn);
    (Vector3 Position, Quaternion Rotation) GetSpawnPoint(ELocalPlayerIndex playerIndex);
    void ApplySpawnTransform(XRComponent pawn, Vector3 position, Quaternion rotation);
    void DestroyPlayerUserInterface(XRComponent pawn, XRComponent userInterface);
    void DestroyPawn(XRComponent pawn);
}

/// <summary>
/// Runtime accessor for the engine-side game-mode host.
/// </summary>
public static class RuntimeGameModeHostServices
{
    public static IRuntimeGameModeHostServices? Current { get; set; }
}
