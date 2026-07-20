using System.Numerics;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine;

/// <summary>
/// Binds Runtime.Core game-mode policy to the concrete engine world, scene, and camera model.
/// </summary>
internal sealed class EngineRuntimeGameModeHostServices : IRuntimeGameModeHostServices
{
    public bool AutoSpawnPlayer => Engine.PlayMode.Configuration.AutoSpawnPlayer;
    public ELocalPlayerIndex DefaultPlayerIndex => Engine.PlayMode.Configuration.DefaultPlayerIndex;
    public Type? DefaultPawnType => typeof(FlyingCameraPawnComponent);

    public string? GetWorldName(object? worldInstance)
        => (worldInstance as XRWorldInstance)?.TargetWorld?.Name;

    public XRComponent? CreatePawn(object worldInstance, string nodeName, Type pawnType)
    {
        if (worldInstance is not XRWorldInstance world)
            return null;

        SceneNode pawnNode = new(world, nodeName);
        if (pawnNode.AddComponent(pawnType) is not XRComponent pawn || pawn is not IRuntimeGameModePawn)
        {
            pawnNode.Destroy();
            return null;
        }

        world.RootNodes.Add(pawnNode);
        return pawn;
    }

    public (Vector3 Position, Quaternion Rotation) GetSpawnPoint(ELocalPlayerIndex playerIndex)
    {
        IPawnController? player = RuntimePlayerControllerServices.Current?.GetLocalPlayer(playerIndex);
        if (player?.Viewport is XRViewport viewport)
        {
            TransformBase? cameraTransform = viewport.CameraComponent?.Transform ?? viewport.ActiveCamera?.Transform;
            if (cameraTransform is not null)
                return (cameraTransform.WorldTranslation, cameraTransform.WorldRotation);
        }

        if (player?.ControlledPawnComponent?.SceneNode?.Transform is TransformBase pawnTransform)
            return (pawnTransform.WorldTranslation, pawnTransform.WorldRotation);

        return (Vector3.Zero, Quaternion.Identity);
    }

    public void ApplySpawnTransform(XRComponent pawn, Vector3 position, Quaternion rotation)
    {
        if (pawn.SceneNode?.Transform is not TransformBase transform)
            return;

        switch (transform)
        {
            case Transform standardTransform:
                standardTransform.SetWorldTranslationRotation(position, rotation);
                break;
            case RigidBodyTransform rigidBodyTransform:
                rigidBodyTransform.SetWorldTranslation(position);
                rigidBodyTransform.SetWorldRotation(rotation);
                break;
            default:
                return;
        }

        transform.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential).GetAwaiter().GetResult();
    }

    public void DestroyPawn(XRComponent pawn)
    {
        if (!pawn.IsDestroyed)
            pawn.SceneNode?.Destroy();
    }
}
