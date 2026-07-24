using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
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

        var scene = new XRScene("Physics Testing Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        Pawns.CreatePlayerPawn(setUI, isServer, rootNode);

        if (Toggles.DirLight)
            Lighting.AddDirLight(rootNode);

        BootstrapPhysicsTestWorldBuilder.AddPlayground(
            rootNode,
            RuntimeBootstrapState.Settings.PhysicsBallCount,
            RuntimeBootstrapState.Settings.RenderPhysicsDebug);

        // Keep the flying editor pawn for scene inspection, but spawn and possess a
        // collision-aware character pawn at the start of its authored test course.
        var gameMode = new LocomotionGameMode
        {
            SpawnPositionOverride = new Vector3(0.0f, 2.0f, 1.5f),
            SpawnRotationOverride = Quaternion.CreateFromYawPitchRoll(
                MathF.PI,
                XRMath.DegToRad(-10.0f),
                0.0f),
        };
        return CreateTrackedWorld("Physics Testing World", scene, gameMode);
    }

    public static XRWorld CreatePhysxTestingWorld(bool setUI, bool isServer)
        => CreatePhysicsTestingWorld(setUI, isServer);
}
