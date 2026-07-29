using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class MonkeyBallWorldAssetContractTests
{
    [Test]
    public void SavedWorld_ContainsAuthoredPhysicsLitGeometryAndStandaloneDirectionalLight()
    {
        string asset = ReadRepoFile(
            "Samples",
            "MonkeyBallVR",
            "Assets",
            "Worlds",
            "MonkeyBallWorld.asset");

        asset.ShouldContain("__assetType: MonkeyBallVR.MonkeyBallWorldAsset");
        asset.ShouldContain("__type: MonkeyBallVR.MonkeyBallGameComponent");
        asset.ShouldContain("Name: Tilting Course");
        asset.ShouldContain("Name: Player Rig");
        asset.ShouldContain("Name: VR Headset");
        asset.ShouldContain("Name: Left Controller");
        asset.ShouldContain("Name: Right Controller");
        asset.ShouldContain("Name: VR Trackers");
        asset.ShouldContain("Name: Procedural Scoreboard");
        CountOccurrences(
            asset,
            "__type: XREngine.Components.Physics.DynamicRigidBodyComponent").ShouldBe(2);
        asset.ShouldContain(
            "$type: XREngine.Scene.Transforms.RigidBodyTransform, XREngine.Runtime.Core");
        asset.ShouldContain("__type: XREngine.Components.Mesh.Shapes.BoxMeshComponent");
        asset.ShouldContain("__type: XREngine.Components.Mesh.Shapes.SphereMeshComponent");
        asset.ShouldContain("Source: Shaders/Common/ColoredDeferred.fs");
        asset.ShouldContain("Gravity: 0 -9.81 0");
        asset.ShouldContain("PhysicsSubsteps: 2");
        asset.ShouldContain("RenderSkybox: false");
        asset.ShouldContain("DirectionalShadowRenderingMode: NonCascaded");
        asset.ShouldContain("DesktopCameraPitchDegrees: -14");
        asset.ShouldContain("UseShadowAtlas: false");
        asset.ShouldContain("EnableCascadedShadows: false");
        CountOccurrences(
            asset,
            "ExcludeFromGpuIndirect: true").ShouldBe(4);
        asset.ShouldNotContain("\n        ShadowMap:");
        asset.ShouldNotContain("\n          Shape:");

        string[] lines = asset.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int ballLine = Array.IndexOf(lines, "      Name: Player Ball");
        ballLine.ShouldBeGreaterThanOrEqualTo(0);
        lines[ballLine + 1].ShouldBe("      ChildNodes:");

        int cameraLine = Array.FindIndex(
            lines,
            ballLine + 2,
            static line => line == "        Name: Desktop Camera");
        int settingsLine = Array.FindIndex(
            lines,
            ballLine + 2,
            static line => line == "Settings:");
        cameraLine.ShouldBeGreaterThan(ballLine);
        settingsLine.ShouldBeGreaterThan(cameraLine);
    }

    [Test]
    public void Bootstrap_LoadsCookedWorldAssetInsteadOfConstructingRuntimeScene()
    {
        string bootstrap = ReadMonkeyBallScript("MonkeyBallGameBootstrap.cs");
        string game = ReadMonkeyBallScript("MonkeyBallGameComponent.cs");
        string registration = ReadMonkeyBallScript("MonkeyBallRuntimeRegistration.cs");
        string serializer = ReadMonkeyBallScript("MonkeyBallWorldCookedSerializer.cs");

        bootstrap.ShouldContain("LoadGameAsset<MonkeyBallWorldAsset>");
        bootstrap.ShouldContain("\"Worlds\"");
        bootstrap.ShouldContain("\"MonkeyBallWorld.asset\"");
        registration.ShouldContain("PublishedCookedAssetRegistry.Register(");
        registration.ShouldContain("typeof(MonkeyBallWorldAsset)");

        game.ShouldContain("RegisterTick(ETickGroup.PrePhysics");
        game.ShouldContain("_courseBody.KinematicTarget = target;");
        game.ShouldContain("_ballBody?.RigidBody?.LinearVelocity");
        game.ShouldContain("rigidBody.SetTransform(position, Quaternion.Identity);");
        game.ShouldNotContain("CreateGameRoot");
        game.ShouldNotContain("BuildCourse(");
        game.ShouldContain("grading.AutoExposure = false;");
        game.ShouldContain("grading.Exposure = 1.0f;");
        game.ShouldContain("DirectionalShadowRenderingMode = EDirectionalShadowRenderingMode.NonCascaded;");
        game.ShouldNotContain("BuildPlayerRig(");
        game.ShouldNotContain("new SceneNode(");
        game.ShouldNotContain("SimulatePlaying(");
        game.ShouldNotContain("_ballPosition +=");
        serializer.ShouldContain("writer.Write(material.RenderOptions.ExcludeFromGpuIndirect);");
        serializer.ShouldContain(
            "material.RenderOptions.ExcludeFromGpuIndirect = excludeFromGpuIndirect;");
    }

    [Test]
    public void DesktopControls_AreCameraRelativeAndRotateStageAboutBall()
    {
        string game = ReadMonkeyBallScript("MonkeyBallGameComponent.cs");
        string pawn = ReadMonkeyBallScript("MonkeyBallPawnComponent.cs");

        game.ShouldContain("CameraRelativeInputToWorld(input, _cameraYaw)");
        game.ShouldContain("Vector3 pivot = new(ballPosition.X, 0.0f, ballPosition.Z);");
        game.ShouldContain("Vector3 translation = ResolveStagePivotTranslation(pivot, rotation);");
        game.ShouldContain("_courseBody.KinematicTarget = target;");
        game.ShouldContain("_desktopCameraTransform.SetWorldTranslationRotation(");
        game.ShouldContain("MathF.Atan2(-horizontalVelocity.X, -horizontalVelocity.Y)");
        game.ShouldContain("_cameraYaw = InterpolateAngle(");
        game.ShouldContain("DesktopCameraYawResponse,");

        pawn.ShouldContain("RegisterKeyStateChange(EKey.W, SetKeyboardForward)");
        pawn.ShouldContain("RegisterKeyStateChange(EKey.A, SetKeyboardLeft)");
        pawn.ShouldContain("RegisterKeyStateChange(EKey.S, SetKeyboardBackward)");
        pawn.ShouldContain("RegisterKeyStateChange(EKey.D, SetKeyboardRight)");
        pawn.ShouldContain("RegisterKeyStateChange(EKey.Up, SetArrowForward)");
        pawn.ShouldContain("RegisterKeyStateChange(EKey.Left, SetArrowLeft)");
        pawn.ShouldContain("RegisterKeyStateChange(EKey.Down, SetArrowBackward)");
        pawn.ShouldContain("RegisterKeyStateChange(EKey.Right, SetArrowRight)");
    }

    private static string ReadMonkeyBallScript(string fileName)
        => ReadRepoFile(
            "Samples",
            "MonkeyBallVR",
            "Assets",
            "Scripts",
            fileName);

    private static int CountOccurrences(string value, string substring)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }

        return count;
    }

    private static string ReadRepoFile(params string[] relativePath)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(relativePath)));

    private static string FindRepositoryRoot()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);
        while (true)
        {
            if (File.Exists(Path.Combine(current, "XRENGINE.slnx")))
                return current;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
                throw new DirectoryNotFoundException("Unable to locate the XRENGINE repository root.");

            current = parent;
        }
    }
}
