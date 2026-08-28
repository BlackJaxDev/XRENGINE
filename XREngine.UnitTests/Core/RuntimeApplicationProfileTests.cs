using NUnit.Framework;
using Shouldly;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.InputIntegration;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class RuntimeApplicationProfileTests
{
    [TearDown]
    public void TearDown()
        => RuntimeApplicationBootstrap.Uninstall();

    [Test]
    public void HeadlessServer_InstallsSimulationOnlyProfileAndRestoresIt()
    {
        IRuntimeRenderingHostServices previousRendering = RuntimeRenderingHostServices.Current;
        IRuntimeWindowApplicationServices previousWindows = RuntimeWindowApplicationServices.Current;
        IRuntimeInputServices previousInput = RuntimeInputServices.Current;
        IRuntimePlayerControllerServices? previousControllers = RuntimePlayerControllerServices.Current;

        using (RuntimeApplicationBootstrap.Install(RuntimeApplicationProfile.HeadlessServer))
        {
            RuntimeApplicationCapabilityServices.Current.AllowsWindows.ShouldBeFalse();
            RuntimeApplicationCapabilityServices.Current.AllowsLocalInput.ShouldBeFalse();
            RuntimeApplicationCapabilityServices.Current.AllowsAudio.ShouldBeFalse();
            RuntimeApplicationCapabilityServices.Current.AllowsVr.ShouldBeFalse();
            RuntimeRenderingHostServices.Current.ShouldBeSameAs(previousRendering);
            RuntimeWindowApplicationServices.Current.ShouldBeSameAs(previousWindows);
            RuntimeInputServices.Current.ShouldBeSameAs(previousInput);
            RuntimePlayerControllerServices.Current.ShouldBeOfType<RemoteOnlyPlayerControllerServices>();
        }

        RuntimeApplicationCapabilityServices.Current.IsConfigured.ShouldBeFalse();
        RuntimePlayerControllerServices.Current.ShouldBeSameAs(previousControllers);
    }

    [Test]
    public void DesktopAndVrProfiles_DeclareCompleteExpectedAdapters()
    {
        RuntimeApplicationProfile.DesktopClient.AllowsWindows.ShouldBeTrue();
        RuntimeApplicationProfile.DesktopClient.AllowsLocalInput.ShouldBeTrue();
        RuntimeApplicationProfile.DesktopClient.AllowsAudio.ShouldBeTrue();
        RuntimeApplicationProfile.DesktopClient.AllowsVr.ShouldBeFalse();

        RuntimeApplicationProfile.VrClient.AllowsWindows.ShouldBeTrue();
        RuntimeApplicationProfile.VrClient.AllowsLocalInput.ShouldBeTrue();
        RuntimeApplicationProfile.VrClient.AllowsAudio.ShouldBeTrue();
        RuntimeApplicationProfile.VrClient.AllowsVr.ShouldBeTrue();
    }

    [Test]
    public void StartupPolicy_NormalizesStableValuesAndRejectsHeadlessWindows()
    {
        GameStartupSettings settings = new()
        {
            RunWithoutWindows = true,
            TargetUpdatesPerSecond = 72.0f,
            FixedFramesPerSecond = 90.0f,
            TargetFramesPerSecond = null,
            StartupWindows = [],
        };

        RuntimeStartupPlan plan = RuntimeStartupPolicy.Normalize(settings);
        plan.Values.RunWithoutWindows.ShouldBeTrue();
        plan.Values.TargetUpdatesPerSecond.ShouldBe(72.0f);
        plan.Windows.ShouldBeEmpty();
        Should.NotThrow(() => RuntimeStartupPolicy.ValidateProfile(RuntimeApplicationProfile.HeadlessServer, plan));

        settings.StartupWindows.Add(new GameWindowStartupSettings());
        RuntimeStartupPlan invalid = RuntimeStartupPolicy.Normalize(settings);
        Should.Throw<InvalidOperationException>(() =>
            RuntimeStartupPolicy.ValidateProfile(RuntimeApplicationProfile.HeadlessServer, invalid));
    }
}
