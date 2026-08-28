using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class PublishedLauncherGenerationTests
{
    [TestCase(ERendererBackendPackageMode.All, true, true)]
    [TestCase(ERendererBackendPackageMode.OpenGL, true, false)]
    [TestCase(ERendererBackendPackageMode.Vulkan, false, true)]
    public void NativeAotLauncher_DefineConstantsRootSelectedStaticRendererModules(
        ERendererBackendPackageMode rendererBackendPackage,
        bool expectOpenGl,
        bool expectVulkan)
    {
        BuildSettings settings = new()
        {
            PublishLauncherAsNativeAot = true,
            LauncherDefineConstants = "CUSTOM_LAUNCHER",
            RendererBackendPackage = rendererBackendPackage,
        };

        HashSet<string> constants = CodeManager
            .ComposeLauncherDefineConstantsForTests(settings)
            .Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        constants.ShouldContain("CUSTOM_LAUNCHER");
        constants.ShouldContain(XRRuntimeEnvironment.PublishedDefineConstant);
        constants.ShouldContain(XRRuntimeEnvironment.AotRuntimeDefineConstant);
        constants.Contains("XRENGINE_STATIC_OPENGL").ShouldBe(expectOpenGl);
        constants.Contains("XRENGINE_STATIC_VULKAN").ShouldBe(expectVulkan);
    }

    [Test]
    public void NativeAotLauncher_InstallsRuntimeServicesAndDirectlyRootsGameBootstrap()
    {
        string source = CodeManager.BuildLauncherProgramSourceForTests(
            new BuildSettings { PublishLauncherAsNativeAot = true },
            "startup.asset",
            "editor_preferences.asset",
            "user_settings.asset",
            "MonkeyBallVR.MonkeyBallGameBootstrap");

        source.ShouldNotContain("using IDisposable startupAssetServices = RuntimeAssetBootstrap.InstallEngineAssetServices();");
        source.ShouldContain("using IDisposable applicationServices = RuntimeApplicationBootstrap.Install(");
        source.ShouldNotContain("startupAssetServices.Dispose();");
        source.ShouldNotContain("EnginePublishedCookedAssetRegistryRegistration");
        source.ShouldContain("startup = LoadRequiredPublishedAsset<GameStartupSettings>(archivePath, \"startup.asset\");");
        source.ShouldContain("IGameLaunchBootstrap gameBootstrap = new global::MonkeyBallVR.MonkeyBallGameBootstrap();");
        source.ShouldContain("gameBootstrap.ApplicationProfile");
        source.ShouldContain("IGameLaunchRuntimeSmokeBootstrap? runtimeSmokeBootstrap =");
        source.ShouldContain("runtimeSmokeBootstrap?.ConfigureRuntimeSmoke();");
        source.ShouldContain("startup = gameBootstrap.ConfigureStartup(startup)");
        source.ShouldContain("Engine.Run(startup, gameBootstrap.CreateInitialGameState());");
        source.ShouldContain("runtimeSmokeBootstrap?.CompleteRuntimeSmoke();");
        source.ShouldContain("AOT runtime smoke passed.");
        source.IndexOf("runtimeSmokeBootstrap?.ConfigureRuntimeSmoke();", StringComparison.Ordinal)
            .ShouldBeLessThan(
                source.IndexOf("startup = gameBootstrap.ConfigureStartup(startup)", StringComparison.Ordinal));
        source.IndexOf("RuntimeApplicationBootstrap.Install(", StringComparison.Ordinal)
            .ShouldBeLessThan(
                source.IndexOf("startup = gameBootstrap.ConfigureStartup(startup)", StringComparison.Ordinal));
        source.ShouldContain("if (!File.Exists(commonAssetsArchivePath))");
        source.ShouldContain("Environment.ExitCode = 1;");
        source.ShouldContain("AssetManager.ConfigurePublishedArchives(");
    }
}
