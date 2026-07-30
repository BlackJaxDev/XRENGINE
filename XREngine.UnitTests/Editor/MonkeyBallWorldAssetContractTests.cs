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
        asset.ShouldContain("InterpolationMode: Interpolate");
        asset.ShouldContain("__type: XREngine.Components.Mesh.Shapes.BoxMeshComponent");
        asset.ShouldContain("__type: XREngine.Components.Mesh.Shapes.SphereMeshComponent");
        asset.ShouldContain("Source: Shaders/Common/ColoredDeferred.fs");
        asset.ShouldContain("Gravity: 0 -9.81 0");
        asset.ShouldContain("PhysicsTimestep: 0.008333333");
        asset.ShouldContain("PhysicsSubsteps: 2");
        asset.ShouldContain("RenderSkybox: false");
        asset.ShouldContain("DirectionalShadowRenderingMode: NonCascaded");
        asset.ShouldContain("DesktopCameraPitchDegrees: -14");
        asset.ShouldContain("Type: Dynamic");
        asset.ShouldContain("CastsShadows: true");
        asset.ShouldContain("UseShadowAtlas: false");
        asset.ShouldContain("ShadowMapStorageFormat: Depth24");
        asset.ShouldContain("ShadowMapEncoding: Depth");
        asset.ShouldContain("EnableCascadedShadows: false");
        asset.ShouldContain("Scale: 80 80 120");
        asset.ShouldContain("ShadowMapResolutionWidth: 2048");
        asset.ShouldContain("ShadowMapResolutionHeight: 2048");
        asset.ShouldContain("ShadowMinBias: 0.00001");
        asset.ShouldContain("ShadowMaxBias: 0.004");
        asset.ShouldContain("ShadowDepthBiasTexels: 1");
        asset.ShouldContain("ShadowSlopeBiasTexels: 2");
        asset.ShouldContain("ShadowNormalBiasTexels: 1");
        CountOccurrences(
            asset,
            "ExcludeFromGpuIndirect: true").ShouldBe(4);
        asset.ShouldNotContain("\n        ShadowMap:");
        asset.ShouldNotContain("\n          Shape:");

        string[] lines = asset.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int ballLine = Array.IndexOf(lines, "      Name: Player Ball");
        ballLine.ShouldBeGreaterThanOrEqualTo(0);
        lines[ballLine + 1].ShouldNotBe("      ChildNodes:");

        int cameraLine = Array.IndexOf(lines, "      Name: Desktop Camera");
        int nestedCameraLine = Array.IndexOf(lines, "        Name: Desktop Camera");
        int settingsLine = Array.IndexOf(lines, "Settings:");
        cameraLine.ShouldBeGreaterThan(ballLine);
        nestedCameraLine.ShouldBe(-1);
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
        game.ShouldContain("private IAbstractDynamicRigidBody RequireBallActor()");
        game.ShouldContain("=> RequireBallTransform().LastPhysicsLinearVelocity;");
        game.ShouldContain("transform-only physics is unsupported.");
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
        serializer.ShouldContain("writer.Write((int)light.Type);");
        serializer.ShouldContain("writer.Write((int)light.ShadowMapStorageFormat);");
        serializer.ShouldContain("writer.Write((int)light.ShadowMapEncoding);");
        serializer.ShouldContain("writer.Write(light.ShadowMinBias);");
        serializer.ShouldContain("writer.Write(light.ShadowMaxBias);");
        serializer.ShouldContain("writer.Write(light.ShadowDepthBiasTexels);");
        serializer.ShouldContain("writer.Write(light.ShadowSlopeBiasTexels);");
        serializer.ShouldContain("writer.Write(light.ShadowNormalBiasTexels);");
        serializer.ShouldContain("light.Type = (ELightType)reader.ReadInt32();");
        serializer.ShouldContain(
            "light.ShadowMapStorageFormat = (EShadowMapStorageFormat)reader.ReadInt32();");
        serializer.ShouldContain(
            "light.ShadowMapEncoding = (EShadowMapEncoding)reader.ReadInt32();");
    }

    [Test]
    public void DesktopControls_AreCameraRelativeAndRotateStageAboutBall()
    {
        string game = ReadMonkeyBallScript("MonkeyBallGameComponent.cs");
        string pawn = ReadMonkeyBallScript("MonkeyBallPawnComponent.cs");

        game.ShouldContain("CameraRelativeInputToWorld(input, _cameraYaw)");
        game.ShouldContain("Vector3 ballPosition = GetBallPhysicsPosition();");
        game.ShouldContain("Vector3 translation = ResolveStagePivotTranslation(ballPosition, _stageRotation);");
        game.ShouldNotContain("Vector3 pivot = new(ballPosition.X, 0.0f, ballPosition.Z);");
        game.ShouldContain("_courseBody.KinematicTarget = target;");
        game.ShouldContain("_desktopCameraTransform.SetWorldTranslationRotation(");
        game.ShouldContain("RegisterTick(ETickGroup.Late, ETickOrder.Scene, CameraTick)");
        game.ShouldContain("RecordDesktopCameraPresentation()");
        game.ShouldContain("_ballTransform.RenderTranslation");
        game.ShouldContain("Quaternion targetHeading = CreateYawFacing(horizontalVelocity);");
        game.ShouldContain("_cameraHeadingRotation = InterpolateRotation(");
        game.ShouldContain("_cameraYaw = ExtractYaw(_cameraHeadingRotation);");
        game.ShouldContain("CalculateDesktopCameraPose(");
        game.ShouldContain("ballWorldPosition + Vector3.Transform(cameraOffset, heading)");
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

    [Test]
    public void RuntimeSettings_UseInterpolatedBallAnd120HzPhysics()
    {
        string bootstrap = ReadMonkeyBallScript("MonkeyBallGameBootstrap.cs");
        string game = ReadMonkeyBallScript("MonkeyBallGameComponent.cs");
        string validation = ReadMonkeyBallScript("MonkeyBallRuntimeValidation.cs");
        string startup = ReadRepoFile("Samples", "MonkeyBallVR", "Assets", "startup.asset");
        string gameSettings = ReadRepoFile("Samples", "MonkeyBallVR", "Config", "game_settings.asset");

        bootstrap.ShouldContain("startup.FixedFramesPerSecond = 120.0f;");
        startup.ShouldContain("FixedFramesPerSecond: 120");
        gameSettings.ShouldContain("FixedFramesPerSecond: 120");
        game.ShouldContain("RequiredPhysicsFixedRateHz = 120.0f");
        game.ShouldContain(
            "_ballTransform.InterpolationMode == RigidBodyTransform.EInterpolationMode.Interpolate");
        validation.ShouldContain("physicsSteps * 4L >= normalTicks * 5L");
        validation.ShouldContain("fixedHz=120");
    }

    [Test]
    public void ReleasePackager_RequiresLiveMonkeyBallRuntimeCompletion()
    {
        string publisher = ReadRepoFile("Tools", "Publish-MonkeyBallVR.ps1");

        publisher.ShouldContain(
            "MonkeyBall runtime validation event=runtime-validation-passed");
        publisher.ShouldContain("AOT runtime smoke passed\\.");
    }

    [Test]
    public void ReleasePackager_RebuildsToolingAndVerifiesRendererInputHashes()
    {
        string genericPublisher = ReadRepoFile("Tools", "Publish-AotFinalGame.ps1");
        string gamePublisher = ReadRepoFile("Tools", "Publish-MonkeyBallVR.ps1");

        genericPublisher.ShouldContain(
            "Rebuilding canonical $EditorConfiguration editor/tooling output...");
        genericPublisher.ShouldContain("\"--no-incremental\"");
        genericPublisher.ShouldContain("$buildArgs = @(\"run\", \"--no-build\")");
        gamePublisher.ShouldContain("Assert-MonkeyBallRendererInputHashes");
        gamePublisher.ShouldContain(
            "Get-FileHash -LiteralPath $sourceAssembly.FullName -Algorithm SHA256");
        gamePublisher.ShouldContain(
            "Get-FileHash -LiteralPath $launcherAssembly.FullName -Algorithm SHA256");
        gamePublisher.ShouldContain("RenderingAssemblyHashes.json");
    }

    [Test]
    public void RuntimePhysicsReads_UsePostFetchCachesAcrossThreads()
    {
        string game = ReadMonkeyBallScript("MonkeyBallGameComponent.cs");
        string physx = ReadRepoFile(
            "XREngine.Runtime.Core",
            "Scene",
            "Physics",
            "Physx",
            "PhysxDynamicRigidBody.cs");

        game.ShouldContain("Vector3 velocity = GetBallCachedVelocity();");
        game.ShouldContain("GetBallCachedAngularVelocity(),");
        game.ShouldContain("ClampBallSpeedOnPhysicsThread();");
        game.ShouldNotContain("ballActor?.AngularVelocity");
        physx.ShouldContain("public override Vector3 LinearVelocity => _cachedLinearVelocity;");
        physx.ShouldContain("public override Vector3 AngularVelocity => _cachedAngularVelocity;");
        physx.ShouldContain("RuntimeThreadServices.Current.EnqueuePhysicsThread(");
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
