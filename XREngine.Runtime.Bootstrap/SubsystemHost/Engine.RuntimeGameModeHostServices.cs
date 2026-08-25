using System.Numerics;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.UI;
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

    public XRComponent? CreatePlayerUserInterface(
        object worldInstance,
        string nodeName,
        Type userInterfaceType,
        XRComponent pawn)
    {
        if (worldInstance is not XRWorldInstance world ||
            pawn is not PawnComponent concretePawn ||
            !typeof(XRComponent).IsAssignableFrom(userInterfaceType) ||
            !typeof(IRuntimeGameModeUserInterface).IsAssignableFrom(userInterfaceType))
        {
            return null;
        }

        CameraComponent? camera = concretePawn.CameraComponent as CameraComponent
            ?? concretePawn.GetSiblingComponent<CameraComponent>();
        if (camera is null)
        {
            Debug.LogWarning(
                $"Cannot create player user interface '{userInterfaceType.Name}' because pawn " +
                $"'{concretePawn.Name ?? concretePawn.GetType().Name}' has no camera component.");
            return null;
        }

        SceneNode userInterfaceNode = new(world, nodeName);
        if (userInterfaceNode.AddComponent(userInterfaceType) is not XRComponent userInterface ||
            userInterface is not IRuntimeScreenSpaceUserInterface screenSpaceUserInterface)
        {
            userInterfaceNode.Destroy();
            return null;
        }

        if (userInterface is UICanvasComponent canvas)
        {
            UICanvasTransform canvasTransform = canvas.CanvasTransform;
            canvasTransform.DrawSpace = ECanvasDrawSpace.Screen;
            canvasTransform.SetSize(new Vector2(1920.0f, 1080.0f));
            canvasTransform.Padding = Vector4.Zero;

            UICanvasInputComponent? input = userInterfaceNode.AddComponent<UICanvasInputComponent>();
            if (input is not null)
            {
                input.OwningPawn = concretePawn;
                concretePawn.UserInterfaceInput = input;
            }
        }

        if (!screenSpaceUserInterface.IsScreenSpace)
        {
            userInterfaceNode.Destroy();
            return null;
        }

        world.RootNodes.Add(userInterfaceNode);
        camera.UserInterface = screenSpaceUserInterface;
        return userInterface;
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

    public void DestroyPlayerUserInterface(XRComponent pawn, XRComponent userInterface)
    {
        if (pawn is PawnComponent concretePawn)
        {
            CameraComponent? camera = concretePawn.CameraComponent as CameraComponent
                ?? concretePawn.GetSiblingComponent<CameraComponent>();
            if (userInterface is IRuntimeScreenSpaceUserInterface screenSpaceUserInterface &&
                ReferenceEquals(camera?.UserInterface, screenSpaceUserInterface))
            {
                camera.UserInterface = null;
            }

            if (concretePawn.UserInterfaceInput is UICanvasInputComponent input &&
                ReferenceEquals(input.SceneNode, userInterface.SceneNode))
            {
                input.OwningPawn = null;
                concretePawn.UserInterfaceInput = null;
            }
        }

        if (!userInterface.IsDestroyed)
            userInterface.SceneNode?.Destroy();
    }

    public void DestroyPawn(XRComponent pawn)
    {
        if (!pawn.IsDestroyed)
            pawn.SceneNode?.Destroy();
    }
}
