using XREngine.Components;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Bootstrap.Builders;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static XRWorld CreatePhysicsTestingWorld(bool setUI, bool isServer)
    {
        ApplyRenderSettingsFromToggles();

        // Make it easy to see what's going on.
        Engine.Rendering.Settings.PhysicsVisualizeSettings.SetAllTrue();

        var scene = new XRScene("Physics Testing Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        Pawns.CreatePlayerPawn(setUI, isServer, rootNode);

        if (Toggles.DirLight)
            Lighting.AddDirLight(rootNode);

        BootstrapPhysicsTestWorldBuilder.AddPlayground(
            rootNode,
            RuntimeBootstrapState.Settings.PhysicsBallCount);

        return CreateTrackedWorld("Physics Testing World", scene);
    }

    public static XRWorld CreatePhysxTestingWorld(bool setUI, bool isServer)
        => CreatePhysicsTestingWorld(setUI, isServer);
}
