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
            "RollingBall.RollingBallGameBootstrap");

        source.ShouldNotContain("using IDisposable startupAssetServices = RuntimeAssetBootstrap.InstallEngineAssetServices();");
        source.ShouldContain("using IDisposable applicationServices = RuntimeApplicationBootstrap.Install(");
        source.ShouldNotContain("startupAssetServices.Dispose();");
        source.ShouldNotContain("EnginePublishedCookedAssetRegistryRegistration");
        source.ShouldContain("startup = LoadRequiredPublishedAsset<GameStartupSettings>(archivePath, \"startup.asset\");");
        source.ShouldContain("IGameLaunchBootstrap gameBootstrap = new global::RollingBall.RollingBallGameBootstrap();");
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

    [Test]
    public void DevelopmentArchiveLauncher_DefineConstantsSelectDevelopmentArchivesOnly()
    {
        HashSet<string> constants = ComposeConstants(new BuildSettings { DevelopmentLauncherReadsArchives = true });

        constants.ShouldContain(XRRuntimeEnvironment.DevelopmentArchivesDefineConstant);
        constants.ShouldNotContain(XRRuntimeEnvironment.PublishedDefineConstant);
        constants.ShouldNotContain(XRRuntimeEnvironment.AotRuntimeDefineConstant);
    }

    [Test]
    public void DefaultLauncher_DefineConstantsSelectNoArchiveOrPublishedKind()
    {
        HashSet<string> constants = ComposeConstants(new BuildSettings());

        constants.ShouldNotContain(XRRuntimeEnvironment.DevelopmentArchivesDefineConstant);
        constants.ShouldNotContain(XRRuntimeEnvironment.PublishedDefineConstant);
        constants.ShouldNotContain(XRRuntimeEnvironment.AotRuntimeDefineConstant);
    }

    [Test]
    public void NativeAotLauncher_NeverTakesTheDevelopmentArchiveConstant()
    {
        HashSet<string> constants = ComposeConstants(new BuildSettings
        {
            PublishLauncherAsNativeAot = true,
            DevelopmentLauncherReadsArchives = true,
            LauncherDefineConstants = XRRuntimeEnvironment.DevelopmentArchivesDefineConstant,
        });

        constants.ShouldContain(XRRuntimeEnvironment.PublishedDefineConstant);
        constants.ShouldContain(XRRuntimeEnvironment.AotRuntimeDefineConstant);
        constants.ShouldNotContain(XRRuntimeEnvironment.DevelopmentArchivesDefineConstant);
    }

    [Test]
    public void DevelopmentArchiveLauncher_ProgramReadsTheArchivesAsADevelopmentBuild()
    {
        BuildSettings settings = new() { DevelopmentLauncherReadsArchives = true };
        string active = ActiveLauncherCode(settings);

        active.ShouldContain("XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.DevelopmentArchives);", Case.Sensitive);
        active.ShouldNotContain("EXRRuntimeBuildKind.Published", Case.Sensitive);
        active.ShouldNotContain("EXRRuntimeBuildKind.Development);", Case.Sensitive);
        active.ShouldContain(PreprocessedSource.Squash($"Console.WriteLine(\"{CodeManager.DevelopmentArchiveLauncherStartupMessage}\");"), Case.Sensitive);
        active.ShouldContain("AssetManager.ConfigurePublishedArchives(archivePath,contentArchivePath,commonAssetsArchivePath);", Case.Sensitive);
        active.ShouldNotContain("AssetManager.ConfigurePublishedArchives(null,null,null);", Case.Sensitive);
        active.ShouldContain("if(!File.Exists(commonAssetsArchivePath))", Case.Sensitive);
        // The published-only paths stay out of a development build.
        active.ShouldNotContain("LoadRequiredPublishedAsset", Case.Sensitive);
        active.ShouldNotContain("RunAotSmoke(", Case.Sensitive);
        // The required startup load fails with its own error when both loads give null.
        active.ShouldContain("Developmentarchivebuild:therequiredstartupsettings'startup.asset'didnotload", Case.Sensitive);
        active.ShouldNotContain("Console.Error.WriteLine(\"Failedtoloadstartupsettings.\");", Case.Sensitive);
    }

    [Test]
    public void DefaultLauncher_ProgramReadsLooseFilesAsADevelopmentBuild()
    {
        string active = ActiveLauncherCode(new BuildSettings());

        active.ShouldContain("XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);", Case.Sensitive);
        active.ShouldNotContain("EXRRuntimeBuildKind.DevelopmentArchives", Case.Sensitive);
        active.ShouldNotContain(PreprocessedSource.Squash(CodeManager.DevelopmentArchiveLauncherStartupMessage), Case.Sensitive);
        active.ShouldContain("AssetManager.ConfigurePublishedArchives(null,null,null);", Case.Sensitive);
    }

    [Test]
    public void NativeAotLauncher_ProgramStaysAPublishedAotBuild_WithTheDevelopmentOption()
    {
        BuildSettings settings = new() { PublishLauncherAsNativeAot = true, DevelopmentLauncherReadsArchives = true };
        string active = ActiveLauncherCode(settings);

        active.ShouldContain("XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);", Case.Sensitive);
        active.ShouldNotContain("EXRRuntimeBuildKind.DevelopmentArchives", Case.Sensitive);
        active.ShouldNotContain(PreprocessedSource.Squash(CodeManager.DevelopmentArchiveLauncherStartupMessage), Case.Sensitive);
        active.ShouldContain("AssetManager.ConfigurePublishedArchives(archivePath,contentArchivePath,commonAssetsArchivePath);", Case.Sensitive);
    }

    [Test]
    public void NonAotLauncher_WithAPublishedDefine_DoesNotCompose()
    {
        BuildSettings settings = new() { LauncherDefineConstants = XRRuntimeEnvironment.PublishedDefineConstant };

        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => CodeManager.ComposeLauncherDefineConstantsForTests(settings));
        error.Message.ShouldContain("NativeAOT");
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void ComposeDefineConstants_RejectsDevelopmentArchivesWithAPublishedConstant(bool published, bool aot)
        => Should.Throw<ArgumentException>(() => XRRuntimeEnvironment.ComposeDefineConstants(
            null, includePublishedBuild: published, includeAotRuntime: aot, includeDevelopmentArchives: true));

    private static HashSet<string> ComposeConstants(BuildSettings settings)
        => CodeManager
            .ComposeLauncherDefineConstantsForTests(settings)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Gives the generated launcher code that the compiler sees with the launcher define constants, with no white space.
    /// </summary>
    private static string ActiveLauncherCode(BuildSettings settings)
    {
        string source = CodeManager.BuildLauncherProgramSourceForTests(
            settings,
            "startup.asset",
            "editor_preferences.asset",
            "user_settings.asset");
        return PreprocessedSource.Active(source, ComposeConstants(settings));
    }
}
