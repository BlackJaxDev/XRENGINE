using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class PublishedLauncherGenerationTests
{
    [Test]
    public void NativeAotLauncher_InstallsRuntimeServicesAndDirectlyRootsGameBootstrap()
    {
        string source = CodeManager.BuildLauncherProgramSourceForTests(
            new BuildSettings { PublishLauncherAsNativeAot = true },
            "startup.asset",
            "editor_preferences.asset",
            "user_settings.asset",
            "MonkeyBallVR.MonkeyBallGameBootstrap");

        source.ShouldContain("RuntimeRenderingBootstrap.InstallEngineHostServices();");
        source.ShouldContain("IGameLaunchBootstrap gameBootstrap = new global::MonkeyBallVR.MonkeyBallGameBootstrap();");
        source.ShouldContain("startup = gameBootstrap.ConfigureStartup(startup)");
        source.ShouldContain("Engine.Run(startup, gameBootstrap.CreateInitialGameState());");
        source.ShouldContain("if (!File.Exists(commonAssetsArchivePath))");
        source.ShouldContain("Environment.ExitCode = 1;");
        source.ShouldContain("AssetManager.ConfigurePublishedArchives(");
    }
}
