using System.Linq;
using XREngine;
using XREngine.Components;
using XREngine.Components.VR;
using XREngine.Rendering;
using XREngine.Scene.Prefabs;

namespace XREngine.Editor;

internal static class EditorOpenXrPawnSwitcher
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        Engine.VRState.OpenXRSessionRunningChanged += OnOpenXRSessionRunningChanged;
    }

    private static void OnOpenXRSessionRunningChanged(bool running)
    {
        if (!EditorUnitTests.Toggles.AllowEditingInVR)
            return;

        Engine.EnqueueUpdateThreadTask(() => SwitchPawnControl(running));
    }

    private static void SwitchPawnControl(bool preferVr)
    {
        var world = Engine.State.MainPlayer.Viewport?.World ?? Engine.WorldInstances.FirstOrDefault();
        if (world is null)
            return;

        var vrPawn = FindVrPawn(world);
        var desktopPawn = FindDesktopPawn(world);

        if (preferVr && vrPawn is not null)
            vrPawn.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);
        else if (!preferVr && desktopPawn is not null)
            desktopPawn.EnqueuePossessionByLocalPlayer(ELocalPlayerIndex.One);
    }

    private static PawnComponent? FindVrPawn(XRWorldInstance world)
    {
        foreach (var root in world.RootNodes)
        {
            foreach (var node in SceneNodePrefabUtility.EnumerateHierarchy(root))
            {
                if (node.GetComponent<VRPlayerInputSet>() is not null &&
                    node.GetComponent<PawnComponent>() is PawnComponent pawn)
                {
                    return pawn;
                }
            }
        }

        return null;
    }

    private static EditorFlyingCameraPawnComponent? FindDesktopPawn(XRWorldInstance world)
    {
        foreach (var root in world.RootNodes)
        {
            foreach (var node in SceneNodePrefabUtility.EnumerateHierarchy(root))
            {
                if (node.GetComponent<EditorFlyingCameraPawnComponent>() is EditorFlyingCameraPawnComponent pawn)
                    return pawn;
            }
        }

        return null;
    }
}
