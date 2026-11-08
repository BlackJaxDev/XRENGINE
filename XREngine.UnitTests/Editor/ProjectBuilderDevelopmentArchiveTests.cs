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

        Should.NotThrow(() => ProjectBuilder.ValidateDevelopmentArchiveLauncher(settings));
        CodeManager.UsesDevelopmentArchiveLauncher(settings).ShouldBeTrue();
    }

    [Test]
    public void DevelopmentArchiveLauncher_FailsTheBuild_ForARelease()
    {
        BuildSettings settings = new() { Configuration = EBuildConfiguration.Release, DevelopmentLauncherReadsArchives = true };

        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => ProjectBuilder.ValidateDevelopmentArchiveLauncher(settings));
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

        Should.Throw<InvalidOperationException>(() => ProjectBuilder.ValidateDevelopmentArchiveLauncher(settings));
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
        Should.NotThrow(() => ProjectBuilder.ValidateDevelopmentArchiveLauncher(settings));
    }

    [Test]
    public void DevelopmentArchiveLauncher_RequiresTheLauncherStep()
    {
        BuildSettings settings = new() { DevelopmentLauncherReadsArchives = true, BuildLauncherExecutable = false };

        Should.Throw<InvalidOperationException>(() => ProjectBuilder.ValidateDevelopmentArchiveLauncher(settings));
    }

    [Test]
    public void ReleaseLauncher_WithoutTheOption_IsUnchanged()
    {
        BuildSettings settings = new() { Configuration = EBuildConfiguration.Release };

        CodeManager.UsesDevelopmentArchiveLauncher(settings).ShouldBeFalse();
        Should.NotThrow(() => ProjectBuilder.ValidateDevelopmentArchiveLauncher(settings));
    }
}
