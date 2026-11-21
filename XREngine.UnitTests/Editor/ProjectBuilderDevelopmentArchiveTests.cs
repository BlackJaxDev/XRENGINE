using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// A non-AOT launcher that reads the archives is a development build. A Release build is a production build, so the
/// build stops when it asks for such a launcher.
/// </summary>
[TestFixture]
public sealed class ProjectBuilderDevelopmentArchiveTests
{
    [TestCase(EBuildConfiguration.Debug)]
    [TestCase(EBuildConfiguration.Development)]
    public void DevelopmentArchiveLauncher_IsAllowed_ForDevelopmentConfigurations(EBuildConfiguration configuration)
    {
        BuildSettings settings = new() { Configuration = configuration, DevelopmentLauncherReadsArchives = true };

        Should.NotThrow(() => ProjectBuilder.ValidateLauncherBuildKind(settings));
        CodeManager.UsesDevelopmentArchiveLauncher(settings).ShouldBeTrue();
    }

    [Test]
    public void DevelopmentArchiveLauncher_FailsTheBuild_ForARelease()
    {
        BuildSettings settings = new() { Configuration = EBuildConfiguration.Release, DevelopmentLauncherReadsArchives = true };

        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ProjectBuilder.ValidateLauncherBuildKind(settings));
        error.Message.ShouldContain(nameof(BuildSettings.DevelopmentLauncherReadsArchives));
        error.Message.ShouldContain("NativeAOT");
    }

    [Test]
    public void DevelopmentArchiveDefineByHand_FailsTheBuild_ForARelease()
    {
        BuildSettings settings = new()
        {
            Configuration = EBuildConfiguration.Release,
            LauncherDefineConstants = $"CUSTOM;{XRRuntimeEnvironment.DevelopmentArchivesDefineConstant}",
        };

        Should.Throw<InvalidOperationException>(() => ProjectBuilder.ValidateLauncherBuildKind(settings));
    }

    [Test]
    public void NativeAotRelease_IgnoresTheDevelopmentOption()
    {
        BuildSettings settings = new()
        {
            Configuration = EBuildConfiguration.Release,
            PublishLauncherAsNativeAot = true,
            DevelopmentLauncherReadsArchives = true,
        };

        CodeManager.UsesDevelopmentArchiveLauncher(settings).ShouldBeFalse();
        Should.NotThrow(() => ProjectBuilder.ValidateLauncherBuildKind(settings));
    }

    [Test]
    public void DevelopmentArchiveLauncher_RequiresTheLauncherStep()
    {
        BuildSettings settings = new() { DevelopmentLauncherReadsArchives = true, BuildLauncherExecutable = false };

        Should.Throw<InvalidOperationException>(() => ProjectBuilder.ValidateLauncherBuildKind(settings));
    }

    [Test]
    public void ReleaseLauncher_WithoutTheOption_IsUnchanged()
    {
        BuildSettings settings = new() { Configuration = EBuildConfiguration.Release };

        CodeManager.UsesDevelopmentArchiveLauncher(settings).ShouldBeFalse();
        Should.NotThrow(() => ProjectBuilder.ValidateLauncherBuildKind(settings));
    }

    [TestCase(EBuildConfiguration.Debug, "XRE_PUBLISHED")]
    [TestCase(EBuildConfiguration.Development, "XRE_PUBLISHED")]
    [TestCase(EBuildConfiguration.Release, "XRE_PUBLISHED")]
    [TestCase(EBuildConfiguration.Release, "XRE_AOT_RUNTIME")]
    public void NonAotLauncher_WithAPublishedDefine_FailsTheBuild_InEveryConfiguration(
        EBuildConfiguration configuration,
        string define)
    {
        BuildSettings settings = new() { Configuration = configuration, LauncherDefineConstants = $"CUSTOM;{define}" };

        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ProjectBuilder.ValidateLauncherBuildKind(settings));
        error.Message.ShouldContain(define);
        error.Message.ShouldContain("Production published builds must use NativeAOT");
        error.Message.ShouldContain(nameof(BuildSettings.DevelopmentLauncherReadsArchives));
    }

    [Test]
    public void NonAotLauncher_WithAPublishedDefineAndTheDevelopmentOption_FailsTheBuild()
    {
        BuildSettings settings = new()
        {
            Configuration = EBuildConfiguration.Debug,
            DevelopmentLauncherReadsArchives = true,
            LauncherDefineConstants = XRRuntimeEnvironment.PublishedDefineConstant,
        };

        Should.Throw<InvalidOperationException>(() => ProjectBuilder.ValidateLauncherBuildKind(settings));
        Should.Throw<InvalidOperationException>(() => CodeManager.ComposeLauncherDefineConstantsForTests(settings));
    }

    [TestCase("XRE_PUBLISHED,FOO")]
    [TestCase("FOO XRE_PUBLISHED")]
    [TestCase("FOO;\tXRE_AOT_RUNTIME")]
    public void NonAotLauncher_FindsAPublishedDefine_WithAnySeparator(string defines)
    {
        BuildSettings settings = new() { LauncherDefineConstants = defines };

        Should.Throw<InvalidOperationException>(() => ProjectBuilder.ValidateLauncherBuildKind(settings));
    }

    [Test]
    public void NonAotLauncher_Rejection_NamesTheSettingsAsset()
    {
        BuildSettings settings = new() { LauncherDefineConstants = XRRuntimeEnvironment.PublishedDefineConstant };

        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ProjectBuilder.ValidateLauncherBuildKind(settings));
        error.Message.ShouldContain("build_settings.asset");
    }

    [Test]
    public void NativeAotLauncher_WithThePublishedDefines_IsAllowed()
    {
        BuildSettings settings = new()
        {
            Configuration = EBuildConfiguration.Release,
            PublishLauncherAsNativeAot = true,
            LauncherDefineConstants = "XRE_PUBLISHED;XRE_AOT_RUNTIME",
        };

        Should.NotThrow(() => ProjectBuilder.ValidateLauncherBuildKind(settings));
    }
}
